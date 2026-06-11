using System;
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
                            SendToolNotFound(context, path.Substring("/tools/".Length));
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
