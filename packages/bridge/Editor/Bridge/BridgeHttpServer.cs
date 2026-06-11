using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

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
            var json = BuildPingJson();
            SendJson(context, 200, json);
        }

        static void HandleToolDispatch(HttpListenerContext context, string toolName)
        {
            var body = ReadRequestBody(context.Request);
            var timeoutMs = ExtractTimeoutMs(body);
            var gateMode = ExtractGateMode(body);
            var sw = Stopwatch.StartNew();

            try
            {
                var task = MainThreadDispatcher.EnqueueAsync(
                    () => DispatchTool(toolName, body), timeoutMs);

                var result = task.Result;
                sw.Stop();
                SendJson(context, 200, BuildMutationEnvelope(result, gateMode));
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

        static ToolDispatchResult DispatchTool(string toolName, string body)
        {
            return ToolDispatchResult.Ok();
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

        static string BuildMutationEnvelope(ToolDispatchResult result, string gateMode)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"mutation\":{\"success\":");
            sb.Append(result.Success ? "true" : "false");
            sb.Append(",\"output\":");
            sb.Append(result.Output != null ? EscapeString(result.Output) : "null");
            if (result.ErrorCode != null)
            {
                sb.Append(",\"error\":{\"code\":\"").Append(EscapeStringContent(result.ErrorCode));
                sb.Append("\",\"message\":\"").Append(EscapeStringContent(result.ErrorMessage ?? ""));
                sb.Append("\"}");
            }
            else
            {
                sb.Append(",\"error\":null");
            }
            sb.Append("},\"gate\":{\"mode\":\"").Append(EscapeStringContent(gateMode));
            sb.Append("\",\"skipped\":true,\"validation\":null,\"delta\":null}");
            sb.Append(",\"agentNextSteps\":[]}");
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

        static string BuildPingJson()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"connected\":").Append(BridgeSession.Connected ? "true" : "false").Append(',');
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
