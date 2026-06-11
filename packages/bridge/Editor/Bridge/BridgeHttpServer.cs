using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            "unity_agent_find_members"
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

                Debug.Log($"[Unity Agent Bridge] Listening on http://{BindAddress}:{_port}/");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Unity Agent Bridge] Failed to start listener: {e.Message}");
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
            catch (Exception e)
            {
                Debug.LogWarning($"[Unity Agent Bridge] Error stopping listener: {e.Message}");
            }

            _listener = null;

            try
            {
                _listenerThread?.Join(2000);
            }
            catch { }

            _listenerThread = null;
            Debug.Log("[Unity Agent Bridge] Stopped.");
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
                catch (Exception e)
                {
                    if (_running)
                        Debug.LogError($"[Unity Agent Bridge] Listener error: {e.Message}");
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
                    default:
                        if (path.StartsWith("/tools/"))
                        {
                            var toolName = path.Substring("/tools/".Length);
                            if (context.Request.HttpMethod == "POST")
                            {
                                if (KnownTools.Contains(toolName))
                                    HandleToolDispatch(context, toolName);
                                else
                                    SendToolNotFound(context, toolName);
                            }
                            else
                            {
                                SendJsonError(context, 405, "method_not_allowed", "POST required for tool endpoints");
                            }
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
            var gateMode = ExtractGateMode(body);
            var sw = Stopwatch.StartNew();

            string[] pathsHint = null;
            if (MutatingTools.Contains(toolName))
            {
                pathsHint = JsonBody.GetStringArray(body, "paths_hint");
                if (pathsHint == null || pathsHint.Length == 0)
                {
                    bool skipPathsHint = toolName == "unity_agent_execute_menu"
                        && ExecuteMenuTool.IsReadOnlyMenu(JsonBody.GetString(body, "menu_path"));

                    if (!skipPathsHint)
                    {
                        SendJson(context, 200, BuildPathsHintErrorEnvelope(toolName, gateMode));
                        return;
                    }
                }
            }

            try
            {
                var task = MainThreadDispatcher.EnqueueAsync(
                    () => DispatchWithGate(toolName, body, gateMode, pathsHint), timeoutMs);

                var result = task.Result;
                sw.Stop();
                SendJson(context, 200, BuildGateEnvelope(result, gateMode));
            }
            catch (AggregateException ae)
            {
                sw.Stop();
                var inner = ae.InnerException;
                if (inner is TimeoutException)
                    SendJson(context, 200, BuildTimeoutEnvelope(toolName, gateMode, timeoutMs));
                else
                    SendJson(context, 200, BuildFaultEnvelope(inner, gateMode));
            }
            catch (Exception e)
            {
                sw.Stop();
                SendJson(context, 200, BuildFaultEnvelope(e, gateMode));
            }
        }

        static GateDispatchResult DispatchWithGate(string toolName, string body, string gateMode, string[] pathsHint)
        {
            if (gateMode == "off" || !MutatingTools.Contains(toolName))
            {
                return new GateDispatchResult
                {
                    Mutation = DispatchTool(toolName, body),
                    GateRan = false
                };
            }

            if (pathsHint == null || pathsHint.Length == 0)
            {
                return new GateDispatchResult
                {
                    Mutation = DispatchTool(toolName, body),
                    GateRan = false
                };
            }

            var checkpoint = VerifyGateAdapter.CreateCheckpoint(pathsHint, null);
            var mutation = DispatchTool(toolName, body);

            if (!mutation.Success)
            {
                return new GateDispatchResult
                {
                    Mutation = mutation,
                    GateRan = false,
                    CheckpointId = checkpoint.CheckpointId
                };
            }

            var validation = VerifyGateAdapter.ValidatePaths(pathsHint, null);
            var delta = VerifyGateAdapter.ComputeDelta(checkpoint, validation);
            var gateFailed = gateMode == "enforce" && delta.NewErrors > 0;
            var nextSteps = GenerateAgentNextSteps(delta);

            return new GateDispatchResult
            {
                Mutation = mutation,
                GateRan = true,
                CheckpointId = checkpoint.CheckpointId,
                CategoriesRun = validation.CategoriesRun,
                ValidationDurationMs = validation.DurationMs,
                Delta = delta,
                GateFailed = gateFailed,
                AgentNextSteps = nextSteps
            };
        }

        static ToolDispatchResult DispatchTool(string toolName, string body)
        {
            return toolName switch
            {
                "unity_agent_execute_csharp" => ExecuteCSharpTool.Execute(body),
                "unity_agent_invoke_method" => InvokeMethodTool.Execute(body),
                "unity_agent_execute_menu" => ExecuteMenuTool.Execute(body),
                "unity_agent_find_members" => FindMembersTool.Execute(body),
                _ => ToolDispatchResult.Fail("tool_not_found", $"Unknown tool: {toolName}")
            };
        }

        static string[] GenerateAgentNextSteps(DeltaData delta)
        {
            var steps = new List<string>();

            if (delta.NewErrors > 0)
            {
                var firstIssue = delta.NewIssueKeys.FirstOrDefault() ?? "unknown";
                steps.Add($"Gate detected {delta.NewErrors} new error(s). First: {firstIssue}");
                steps.Add("Review the affected asset and fix the introduced issue before retrying.");
            }
            else if (delta.NewWarnings > 0)
            {
                steps.Add($"Gate detected {delta.NewWarnings} new warning(s). Consider reviewing before proceeding.");
            }
            else if (delta.ResolvedErrors > 0)
            {
                steps.Add($"Gate passed — {delta.ResolvedErrors} previously reported error(s) resolved.");
            }

            if (steps.Count == 0)
                steps.Add("Gate passed — no new issues detected.");

            return steps.ToArray();
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
                sb.Append(",\"validation\":{\"passed\":").Append(!result.GateFailed ? "true" : "false");
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

                sb.Append(",\"delta\":{\"newErrors\":").Append(result.Delta.NewErrors);
                sb.Append(",\"newWarnings\":").Append(result.Delta.NewWarnings);
                sb.Append(",\"resolvedErrors\":").Append(result.Delta.ResolvedErrors);
                sb.Append(",\"resolvedWarnings\":").Append(result.Delta.ResolvedWarnings);
                sb.Append(",\"newIssues\":[");
                if (result.Delta.NewIssueKeys != null)
                {
                    for (int i = 0; i < result.Delta.NewIssueKeys.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(EscapeStringContent(result.Delta.NewIssueKeys[i])).Append('"');
                    }
                }
                sb.Append("],\"resolvedIssues\":[");
                if (result.Delta.ResolvedIssueKeys != null)
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

        static string BuildFaultEnvelope(Exception e, string gateMode)
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
            sb.Append("There is no whole-project fallback in M2 — explicit paths are mandatory.");
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
