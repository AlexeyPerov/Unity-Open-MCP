using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityAgentBridge.MetaTools;

namespace UnityAgentBridge
{
    [InitializeOnLoad]
    public static class BridgeHttpServer
    {
        const string PortEnvVar = "UNITY_AGENT_BRIDGE_PORT";
        const string PortArgPrefix = "-UNITY_AGENT_BRIDGE_PORT=";
        const int DefaultPort = 19120;
        const string BindAddress = "127.0.0.1";
        const int DefaultTimeoutMs = 30000;
        const int MinTimeoutMs = 1000;
        const int MaxTimeoutMs = 300000;

        static readonly HashSet<string> KnownTools = new()
        {
            "unity_agent_execute_csharp",
            "unity_agent_invoke_method",
            "unity_agent_execute_menu",
            "unity_agent_find_members",
            "unity_agent_validate_edit",
            "unity_agent_checkpoint_create",
            "unity_agent_delta"
        };

        static readonly HashSet<string> DirectResponseTools = new()
        {
            "unity_agent_validate_edit",
            "unity_agent_checkpoint_create",
            "unity_agent_delta"
        };

        static readonly HashSet<string> MutatingTools = new()
        {
            "unity_agent_execute_csharp",
            "unity_agent_invoke_method",
            "unity_agent_execute_menu"
        };

        static HttpListener _listener;
        static Thread _listenerThread;
        static volatile bool _running;
        static int _port;

        public static int Port => _port;
        public static bool IsRunning => _running;

        static BridgeHttpServer()
        {
            BridgeToolRegistry.Scan();
            BridgeResourceRegistry.Scan();

            _port = ResolvePort();
            Start();

            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        static int ResolvePort()
        {
            var envValue = Environment.GetEnvironmentVariable(PortEnvVar);
            if (!string.IsNullOrEmpty(envValue) && int.TryParse(envValue, out var envPort) && IsValidPort(envPort))
                return envPort;

            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(PortArgPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var argPortStr = args[i].Substring(PortArgPrefix.Length);
                    if (int.TryParse(argPortStr, out var argPort) && IsValidPort(argPort))
                        return argPort;
                }
            }

            return DefaultPort;
        }

        static bool IsValidPort(int port) => port is >= 1 and <= 65535;

        public static void Start()
        {
            if (_running) return;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://{BindAddress}:{_port}/");
                _listener.Start();
                _running = true;
                BridgeSession.SetConnected(true);

                _listenerThread = new Thread(ListenLoop)
                {
                    Name = "Unity Agent Bridge HTTP Listener",
                    IsBackground = true
                };
                _listenerThread.Start();

                UnityEngine.Debug.Log($"[Unity Agent Bridge] Listening on http://{BindAddress}:{_port}/");
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[Unity Agent Bridge] Failed to start listener: {e.Message}");
                _running = false;
                BridgeSession.SetConnected(false);
            }
        }

        public static void Stop()
        {
            if (!_running) return;
            _running = false;
            BridgeSession.SetConnected(false);

            try
            {
                _listener?.Stop();
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogWarning($"[Unity Agent Bridge] Error stopping listener: {e.Message}");
            }

            _listener = null;

            try
            {
                _listenerThread?.Join(2000);
            }
            catch { }

            _listenerThread = null;
            UnityEngine.Debug.Log("[Unity Agent Bridge] Stopped.");
        }

        static void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                }
                catch (HttpListenerException) { }
                catch (ObjectDisposedException) { }
                catch (System.Exception e)
                {
                    if (_running)
                        UnityEngine.Debug.LogError($"[Unity Agent Bridge] Listener error: {e.Message}");
                }
            }
        }

        static void HandleRequest(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url.AbsolutePath.TrimEnd('/');
                switch (path)
                {
                    case "/ping":
                        HandlePing(context);
                        break;
                    case "/resources":
                        if (context.Request.HttpMethod == "GET")
                            HandleResourceList(context);
                        else
                            SendJsonError(context, 405, "method_not_allowed", "GET required for resource endpoints");
                        break;
                    default:
                        if (path.StartsWith("/tools/"))
                        {
                            var toolName = path.Substring("/tools/".Length);
                            if (context.Request.HttpMethod == "POST")
                            {
                                if (KnownTools.Contains(toolName) || BridgeToolRegistry.Contains(toolName))
                                    HandleToolDispatch(context, toolName);
                                else
                                    SendToolNotFound(context, toolName);
                            }
                            else
                            {
                                SendJsonError(context, 405, "method_not_allowed", "POST required for tool endpoints");
                            }
                        }
                        else if (path.StartsWith("/resources/"))
                        {
                            if (context.Request.HttpMethod == "GET")
                            {
                                var route = path.Substring("/resources/".Length);
                                HandleResourceDispatch(context, route);
                            }
                            else
                                SendJsonError(context, 405, "method_not_allowed", "GET required for resource endpoints");
                        }
                        else
                            SendNotFound(context, path);
                        break;
                }
            }
            catch
            {
                try
                {
                    SendJsonError(context, 500, "bridge_internal_error", "Unhandled bridge exception");
                }
                catch { }
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        static void HandlePing(HttpListenerContext context)
        {
            if (!BridgeSession.IsInitialized)
            {
                var fallback = "{\"connected\":false,\"projectPath\":null,\"unityVersion\":null,\"bridgeVersion\":\"0.1.0\",\"mode\":\"live\",\"compiling\":true,\"isPlaying\":false}";
                SendJson(context, 503, fallback);
                return;
            }
            var json = BuildPingJson();
            SendJson(context, 200, json);
        }

        static void HandleToolDispatch(HttpListenerContext context, string toolName)
        {
            var body = ReadRequestBody(context.Request);
            var timeoutMs = ExtractTimeoutMs(body);

            if (DirectResponseTools.Contains(toolName))
            {
                HandleDirectResponseTool(context, toolName, body, timeoutMs);
                return;
            }

            var gateMode = ExtractGateMode(body);
            var sw = Stopwatch.StartNew();

            bool isRegistryTool = BridgeToolRegistry.TryGet(toolName, out var registryEntry);
            bool isMutating = MutatingTools.Contains(toolName) || (isRegistryTool && registryEntry.IsMutating);
            string effectiveGateMode = gateMode;

            bool runtimeGateSpecified = !string.IsNullOrEmpty(body) && body.Contains("\"gate\"");
            if (isRegistryTool && !runtimeGateSpecified)
            {
                var attrGate = registryEntry.Gate;
                effectiveGateMode = attrGate switch
                {
                    GateMode.Enforce => "enforce",
                    GateMode.Warn => "warn",
                    GateMode.Off => "off",
                    _ => "enforce"
                };
            }

            string[] pathsHint = null;
            if (isMutating)
            {
                pathsHint = JsonBody.GetStringArray(body, "paths_hint");
                if (pathsHint == null || pathsHint.Length == 0)
                {
                    bool skipPathsHint = toolName == "unity_agent_execute_menu"
                        && ExecuteMenuTool.IsReadOnlyMenu(JsonBody.GetString(body, "menu_path"));

                    if (!skipPathsHint)
                    {
                        SendJson(context, 200, BuildPathsHintErrorEnvelope(toolName, effectiveGateMode));
                        return;
                    }
                }
            }

            try
            {
                var task = MainThreadDispatcher.EnqueueAsync(
                    () => DispatchWithGate(toolName, body, effectiveGateMode, pathsHint), timeoutMs);

                var result = task.Result;
                sw.Stop();
                SendJson(context, 200, BuildGateEnvelope(result, effectiveGateMode));
            }
            catch (AggregateException ae)
            {
                sw.Stop();
                var inner = ae.InnerException;
                if (inner is TimeoutException)
                    SendJson(context, 200, BuildTimeoutEnvelope(toolName, effectiveGateMode, timeoutMs));
                else
                    SendJson(context, 200, BuildFaultEnvelope(inner, effectiveGateMode));
            }
            catch (System.Exception e)
            {
                sw.Stop();
                SendJson(context, 200, BuildFaultEnvelope(e, effectiveGateMode));
            }
        }

        static GateDispatchResult DispatchWithGate(string toolName, string body, string gateMode, string[] pathsHint)
        {
            bool isMutating = MutatingTools.Contains(toolName)
                || (BridgeToolRegistry.TryGet(toolName, out var regEntry) && regEntry.IsMutating);

            var mode = GatePolicy.ParseMode(gateMode);

            if (!isMutating)
            {
                var nonMutatingResult = DispatchTool(toolName, body);
                return new GateDispatchResult
                {
                    Mutation = nonMutatingResult,
                    GateRan = false,
                    Outcome = nonMutatingResult.Success ? GateOutcome.Skipped : GateOutcome.Failed,
                    GateFailed = !nonMutatingResult.Success
                };
            }

            return GatePolicy.Execute(mode, pathsHint, () => DispatchTool(toolName, body));
        }

        static ToolDispatchResult DispatchTool(string toolName, string body)
        {
            return toolName switch
            {
                "unity_agent_execute_csharp" => ExecuteCSharpTool.Execute(body),
                "unity_agent_invoke_method" => InvokeMethodTool.Execute(body),
                "unity_agent_execute_menu" => ExecuteMenuTool.Execute(body),
                "unity_agent_find_members" => FindMembersTool.Execute(body),
                "unity_agent_validate_edit" => ValidateEditTool.Execute(body),
                "unity_agent_checkpoint_create" => CheckpointCreateTool.Execute(body),
                "unity_agent_delta" => DeltaTool.Execute(body),
                _ => BridgeToolRegistry.TryDispatch(toolName, body)
                     ?? ToolDispatchResult.Fail("tool_not_found", $"Unknown tool: {toolName}")
            };
        }

        static void HandleDirectResponseTool(HttpListenerContext context, string toolName, string body, int timeoutMs)
        {
            try
            {
                var task = MainThreadDispatcher.EnqueueAsync(
                    () => DispatchTool(toolName, body), timeoutMs);

                var result = task.Result;

                if (result.Success && result.Output != null)
                    SendJson(context, 200, result.Output);
                else if (!result.Success)
                    SendJson(context, 200, BuildDirectToolErrorJson(result));
                else
                    SendJson(context, 200, "{\"error\":{\"code\":\"empty_output\",\"message\":\"Tool returned empty output\"}}");
            }
            catch (AggregateException ae)
            {
                var inner = ae.InnerException;
                if (inner is TimeoutException)
                    SendJson(context, 200, $"{{\"error\":{{\"code\":\"timeout\",\"message\":\"Tool '{EscapeStringContent(toolName)}' timed out after {timeoutMs}ms\"}}}}");
                else
                    SendJson(context, 200, $"{{\"error\":{{\"code\":\"execution_error\",\"message\":\"{EscapeStringContent(inner?.Message ?? ae.Message)}\"}}}}");
            }
            catch (System.Exception e)
            {
                SendJson(context, 200, $"{{\"error\":{{\"code\":\"execution_error\",\"message\":\"{EscapeStringContent(e.Message)}\"}}}}");
            }
        }

        static string BuildDirectToolErrorJson(ToolDispatchResult result)
        {
            return $"{{\"error\":{{\"code\":\"{EscapeStringContent(result.ErrorCode)}\",\"message\":\"{EscapeStringContent(result.ErrorMessage)}\"}}}}";
        }

        static string ReadRequestBody(HttpListenerRequest request)
        {
            using var stream = request.InputStream;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        static int ExtractTimeoutMs(string body)
        {
            if (string.IsNullOrEmpty(body)) return DefaultTimeoutMs;

            const string key = "\"timeout_ms\"";
            var idx = body.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return DefaultTimeoutMs;

            var colonIdx = body.IndexOf(':', idx + key.Length);
            if (colonIdx < 0) return DefaultTimeoutMs;

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;

            var end = start;
            while (end < body.Length && char.IsDigit(body[end])) end++;

            if (end == start || !int.TryParse(body.Substring(start, end - start), out var ms))
                return DefaultTimeoutMs;

            return Math.Clamp(ms, MinTimeoutMs, MaxTimeoutMs);
        }

        static string ExtractGateMode(string body)
        {
            if (string.IsNullOrEmpty(body)) return "enforce";

            const string key = "\"gate\"";
            var idx = body.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return "enforce";

            var colonIdx = body.IndexOf(':', idx + key.Length);
            if (colonIdx < 0) return "enforce";

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;

            if (start >= body.Length || body[start] != '"') return "enforce";
            start++;

            var end = start;
            while (end < body.Length && body[end] != '"') end++;

            if (end == start) return "enforce";

            var value = body.Substring(start, end - start);
            return value is "enforce" or "warn" or "off" ? value : "enforce";
        }

        static string BuildGateEnvelope(GateDispatchResult result, string gateMode)
        {
            var sb = new StringBuilder(1024);

            sb.Append("{\"mutation\":{\"success\":");
            sb.Append(result.Mutation.Success ? "true" : "false");
            sb.Append(",\"output\":");
            sb.Append(result.Mutation.Output ?? "null");
            if (result.Mutation.ErrorCode != null)
            {
                sb.Append(",\"error\":{\"code\":\"").Append(EscapeStringContent(result.Mutation.ErrorCode));
                sb.Append("\",\"message\":\"").Append(EscapeStringContent(result.Mutation.ErrorMessage ?? ""));
                sb.Append("\"}");
            }
            else
            {
                sb.Append(",\"error\":null");
            }
            sb.Append('}');

            sb.Append(",\"gate\":{\"mode\":\"").Append(EscapeStringContent(gateMode));
            sb.Append("\"");

            if (!result.GateRan)
            {
                if (result.CheckpointId != null)
                    sb.Append(",\"checkpointId\":\"").Append(EscapeStringContent(result.CheckpointId)).Append("\"");
                sb.Append(",\"skipped\":true,\"validation\":null,\"delta\":null");
            }
            else
            {
                sb.Append(",\"checkpointId\":\"").Append(EscapeStringContent(result.CheckpointId));
                sb.Append("\",\"skipped\":false");
                sb.Append(",\"validation\":{\"passed\":").Append(result.Outcome == GateOutcome.Passed ? "true" : "false");
                sb.Append(",\"categoriesRun\":[");
                if (result.CategoriesRun != null)
                {
                    for (int i = 0; i < result.CategoriesRun.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(EscapeStringContent(result.CategoriesRun[i])).Append('"');
                    }
                }
                sb.Append("],\"durationMs\":").Append(result.ValidationDurationMs);
                sb.Append('}');

                sb.Append(",\"delta\":{\"newErrors\":").Append(result.Delta?.NewErrors ?? 0);
                sb.Append(",\"newWarnings\":").Append(result.Delta?.NewWarnings ?? 0);
                sb.Append(",\"resolvedErrors\":").Append(result.Delta?.ResolvedErrors ?? 0);
                sb.Append(",\"resolvedWarnings\":").Append(result.Delta?.ResolvedWarnings ?? 0);
                sb.Append(",\"newIssues\":[");
                if (result.Delta?.NewIssueKeys != null)
                {
                    for (int i = 0; i < result.Delta.NewIssueKeys.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(EscapeStringContent(result.Delta.NewIssueKeys[i])).Append('"');
                    }
                }
                sb.Append("],\"resolvedIssues\":[");
                if (result.Delta?.ResolvedIssueKeys != null)
                {
                    for (int i = 0; i < result.Delta.ResolvedIssueKeys.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(EscapeStringContent(result.Delta.ResolvedIssueKeys[i])).Append('"');
                    }
                }
                sb.Append("]}");
            }

            sb.Append('}');

            sb.Append(",\"agentNextSteps\":[");
            var steps = result.AgentNextSteps;
            if (steps != null)
            {
                for (int i = 0; i < steps.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeStringContent(steps[i])).Append('"');
                }
            }
            sb.Append("]}");

            return sb.ToString();
        }

        static string BuildTimeoutEnvelope(string toolName, string gateMode, int timeoutMs)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"mutation\":{\"success\":false,\"output\":null,\"error\":{\"code\":\"timeout\",\"message\":\"Tool '");
            sb.Append(EscapeStringContent(toolName));
            sb.Append("' timed out after ");
            sb.Append(timeoutMs);
            sb.Append("ms\"}},\"gate\":{\"mode\":\"").Append(EscapeStringContent(gateMode));
            sb.Append("\",\"skipped\":true,\"validation\":null,\"delta\":null}");
            sb.Append(",\"agentNextSteps\":[\"Tool execution timed out. Consider increasing timeout_ms or simplifying the operation.\"]}");
            return sb.ToString();
        }

        static string BuildFaultEnvelope(System.Exception e, string gateMode)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"mutation\":{\"success\":false,\"output\":null,\"error\":{\"code\":\"execution_error\",\"message\":\"");
            sb.Append(EscapeStringContent(e.Message));
            sb.Append("\"}},\"gate\":{\"mode\":\"").Append(EscapeStringContent(gateMode));
            sb.Append("\",\"skipped\":true,\"validation\":null,\"delta\":null}");
            sb.Append(",\"agentNextSteps\":[\"Tool execution failed with an unexpected error.\"]}");
            return sb.ToString();
        }

        static string BuildPathsHintErrorEnvelope(string toolName, string gateMode)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"mutation\":{\"success\":false,\"output\":null,\"error\":{\"code\":\"paths_hint_required\",\"message\":\"");
            sb.Append("Mutating tool '");
            sb.Append(EscapeStringContent(toolName));
            sb.Append("' requires a non-empty 'paths_hint' array. ");
            sb.Append("Provide asset paths likely to be affected (e.g. [\\\"Assets/Prefabs/Player.prefab\\\"]) so the gate can scope validation correctly. ");
            sb.Append("There is no whole-project fallback — explicit paths are mandatory.");
            sb.Append("\"}},\"gate\":{\"mode\":\"").Append(EscapeStringContent(gateMode));
            sb.Append("\",\"skipped\":true,\"validation\":null,\"delta\":null}");
            sb.Append(",\"agentNextSteps\":[\"Add 'paths_hint' with at least one asset path before retrying.\"]}");
            return sb.ToString();
        }

        static string BuildPingJson()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"connected\":").Append(BridgeSession.Connected && BridgeSession.IsInitialized ? "true" : "false").Append(',');
            sb.Append("\"projectPath\":").Append(EscapeString(BridgeSession.ProjectPath)).Append(',');
            sb.Append("\"unityVersion\":").Append(EscapeString(BridgeSession.UnityVersion)).Append(',');
            sb.Append("\"bridgeVersion\":").Append(EscapeString(BridgeSession.BridgeVersion)).Append(',');
            sb.Append("\"mode\":").Append(EscapeString(BridgeSession.Mode)).Append(',');
            sb.Append("\"compiling\":").Append(BridgeSession.IsCompiling ? "true" : "false").Append(',');
            sb.Append("\"isPlaying\":").Append(BridgeSession.IsPlaying ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        static void HandleResourceList(HttpListenerContext context)
        {
            var resources = BridgeResourceRegistry.All();
            var sb = new StringBuilder(512);
            sb.Append('[');
            for (int i = 0; i < resources.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var r = resources[i];
                sb.Append('{');
                sb.Append("\"name\":").Append(EscapeString(r.Name)).Append(',');
                sb.Append("\"route\":").Append(EscapeString(r.Route)).Append(',');
                sb.Append("\"mimeType\":").Append(EscapeString(r.MimeType)).Append(',');
                sb.Append("\"description\":").Append(r.Description != null ? EscapeString(r.Description) : "null");
                sb.Append('}');
            }
            sb.Append(']');
            SendJson(context, 200, sb.ToString());
        }

        static void HandleResourceDispatch(HttpListenerContext context, string route)
        {
            var entry = BridgeResourceRegistry.FindByRoute(route);
            if (entry == null)
            {
                var json = $"{{\"error\":{{\"code\":\"resource_not_found\",\"message\":\"Unknown resource route: {EscapeStringContent(route)}\"}}}}";
                SendJson(context, 404, json);
                return;
            }

            try
            {
                var task = MainThreadDispatcher.EnqueueAsync(() =>
                {
                    var instance = entry.GetInstance();
                    var result = entry.Method.Invoke(instance, null);
                    return result?.ToString() ?? "";
                }, 30000);

                var content = task.Result;
                var bytes = Encoding.UTF8.GetBytes(content);
                context.Response.StatusCode = 200;
                context.Response.ContentType = entry.MimeType + "; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (System.Exception e)
            {
                var inner = e is AggregateException ae ? ae.InnerException?.Message : e.Message;
                SendJsonError(context, 500, "execution_error", inner ?? e.Message);
            }
        }

        static void SendToolNotFound(HttpListenerContext context, string toolName)
        {
            var json = $"{{\"error\":{{\"code\":\"tool_not_found\",\"message\":\"Unknown tool: {EscapeStringContent(toolName)}\"}}}}";
            SendJson(context, 404, json);
        }

        static void SendNotFound(HttpListenerContext context, string path)
        {
            var json = $"{{\"error\":{{\"code\":\"not_found\",\"message\":\"Unknown endpoint: {EscapeStringContent(path)}\"}}}}";
            SendJson(context, 404, json);
        }

        static void SendJsonError(HttpListenerContext context, int statusCode, string code, string message)
        {
            var json = $"{{\"error\":{{\"code\":\"{EscapeStringContent(code)}\",\"message\":\"{EscapeStringContent(message)}\"}}}}";
            SendJson(context, statusCode, json);
        }

        static void SendJson(HttpListenerContext context, int statusCode, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        static string EscapeString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            EscapeStringContentTo(sb, s);
            sb.Append('"');
            return sb.ToString();
        }

        static string EscapeStringContent(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
            EscapeStringContentTo(sb, s);
            return sb.ToString();
        }

        static void EscapeStringContentTo(StringBuilder sb, string s)
        {
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                            sb.Append($"\\u{(int)c:X4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
        }
    }
}
