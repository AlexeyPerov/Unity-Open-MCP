using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.Console;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge
{
    // The Unity-side HTTP bridge. Boots on [InitializeOnLoad], opens an
    // HttpListener on a per-project port (InstancePortResolver), and routes
    // MCP tool/resource/event requests. Mutating tools run through the gate
    // flow (GatePolicy); read-only tools return JSON directly.
    //
    // This file is the transport + dispatch-orchestration core. Concerns that
    // used to live inline have been split into sibling internal helpers:
    //   - BridgeToolCatalog      — KnownTools / DirectResponseTools / MutatingTools tables
    //   - BridgeJson             — JSON escape + envelope builders
    //   - BridgeRequestBody      — timeout/gate/issue-id body parsing
    //   - BridgeActivityRecorder — per-request activity bookkeeping
    //   - BridgeAuditRecorder    — gate-run + on-disk audit recording
    //   - BridgeHttpResponse     — SendJson / Send*NotFound helpers
    [InitializeOnLoad]
    public static class BridgeHttpServer
    {
        private const string PortEnvVar = "UNITY_OPEN_MCP_BRIDGE_PORT";
        private const string PortArgPrefix = "-UNITY_OPEN_MCP_BRIDGE_PORT=";

        // Tool classification tables (KnownTools / DirectResponseTools /
        // MutatingTools) live in BridgeToolClassification.cs. Aliased here so the
        // dispatch path reads them as plain KnownTools.Contains(...) without
        // qualifying every call site.
        private static readonly HashSet<string> KnownTools = BridgeToolClassification.KnownTools;
        private static readonly HashSet<string> DirectResponseTools = BridgeToolClassification.DirectResponseTools;
        private static readonly HashSet<string> MutatingTools = BridgeToolClassification.MutatingTools;

        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static volatile bool _running;
        private static int _port;

        // M26 Plan 4 — bounded in-process recovery when bind fails with
        // "address already in use" (zombie listener / OS release lag).
        private const int PortBindRetryAttempts = 5;
        private const int PortBindRetryDelayMs = 100;

        public static int Port => _port;
        public static bool IsRunning => _running;

        static BridgeHttpServer()
        {
            // The bridge HTTP listener only makes sense in the interactive
            // Editor — it is the MCP server's live entry point. Unity also
            // loads this assembly in child processes it spawns for asset
            // import / background work (AssetImportWorker*, launched with
            // -batchMode -parentPid). Those workers used to run Start() from
            // this [InitializeOnLoad] ctor and bind the project's deterministic
            // port; the main Editor then could not bind ("Address already in
            // use"), its heartbeat never restarted, and the MCP server
            // classified the instance as dead_bridge while /ping still
            // succeeded against the worker's listener — a confusing
            // false-positive that recurred frequently under heavy automation
            // (mcp-full-test, CI). The deliberate headless path
            // (BridgeBatchEntry, -executeMethod) never relies on this listener,
            // so skipping it in every batch-mode process is safe.
            if (IsWorkerOrBatchProcess)
            {
                UnityEngine.Debug.Log(
                    $"[Unity Open MCP Bridge] Skipping listener in batch/worker process ({WorkerProcessName()}).");
                return;
            }

            BridgeToolRegistry.Scan();
            BridgeResourceRegistry.Scan();

            _port = ResolvePort();
            Start();

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;
        }

        // True when this process is NOT the interactive Editor — i.e. Unity
        // launched it with -batchMode (covers AssetImportWorker children on
        // both Unity 2022.3 and Unity 6; -parentPid is Unity-6-only and can't
        // be the sole signal because the bridge floor is 2022.3). The
        // deliberate BridgeBatchEntry headless run is also -batchmode, but it
        // is -executeMethod-driven and exits on its own — it never depends on
        // the [InitializeOnLoad] listener, so gating it out here is harmless.
        internal static bool IsWorkerOrBatchProcess =>
            UnityEngine.Application.isBatchMode;

        // Best-effort label for the skip log line. Reads -name <n> from the
        // command line; falls back to "batch" when absent.
        private static string WorkerProcessName()
            => ReadNameArg(Environment.GetCommandLineArgs());

        // Pure arg parser extracted so the EditMode test can exercise it without
        // mutating the process command line. Returns the value after the first
        // -name token, or "batch" when absent / no value follows.
        internal static string ReadNameArg(string[] args)
        {
            if (args == null) return "batch";
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-name") return args[i + 1];
            }
            return "batch";
        }

        // Domain reload: stop the listener but KEEP the instance lock on disk.
        // The heartbeat (registered before this in Start) has already forced a
        // "reloading" write, so the frozen lock reads state="reloading" with a
        // heartbeatAt frozen at reload time. If the bridge assembly itself
        // failed to compile, [InitializeOnLoad] never re-runs, the heartbeat
        // never advances again, and the PID is still alive — that stale-lock +
        // live-PID signature is the only out-of-band signal the MCP server has
        // to detect a dead bridge and fail fast instead of hanging on /ping.
        // On a normal reload, Start() runs again after the reload and Acquire()
        // overwrites this lock with fresh state.
        private static void OnBeforeAssemblyReload() =>
            ForceStopListener(releaseLock: false, logStopped: false);

        // Graceful editor quit: full release — delete the lock so a stale entry
        // doesn't linger for a closed editor.
        private static void OnQuitting() => Stop(releaseLock: true);

        // M13 T4.3 — Per-project port with override precedence:
        //   1. UNITY_OPEN_MCP_BRIDGE_PORT env var
        //   2. -UNITY_OPEN_MCP_BRIDGE_PORT=<n> CLI arg
        //   3. deterministic hash of the project path (20000 + sha256 % 10000)
        // An explicit override always wins so existing configs that pin a port
        // keep working; the hash default lets two projects run bridges
        // concurrently on different ports with zero configuration. The MCP
        // server computes the same hash and reads the lock file, so it finds
        // the right bridge per project without sharing config.
        private static int ResolvePort()
        {
            int? envPort = null;
            var envValue = Environment.GetEnvironmentVariable(PortEnvVar);
            if (!string.IsNullOrEmpty(envValue) && int.TryParse(envValue, out var envParsed)
                && InstancePortResolver.IsValidPort(envParsed))
            {
                envPort = envParsed;
            }

            int? argPort = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(PortArgPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var argPortStr = args[i].Substring(PortArgPrefix.Length);
                    if (int.TryParse(argPortStr, out var argParsed)
                        && InstancePortResolver.IsValidPort(argParsed))
                    {
                        argPort = argParsed;
                    }
                }
            }

            return InstancePortResolver.ResolvePort(GetProjectPathForPort(), envPort, argPort);
        }

        // The project path used for port hashing (and by BridgeAuditRecorder for
        // the audit project hash). Falls back to a stable placeholder if the
        // editor hasn't initialized Application.dataPath yet — in practice this
        // runs inside [InitializeOnLoad] after the project is loaded, but we
        // never want to throw during static init.
        internal static string GetProjectPathForPort()
        {
            try
            {
                var dataPath = UnityEngine.Application.dataPath;
                if (!string.IsNullOrEmpty(dataPath))
                {
                    var parent = System.IO.Directory.GetParent(dataPath)?.FullName ?? dataPath;
                    return parent;
                }
            }
            catch { }
            // Last-resort fallback so ResolvePort never throws. Hashes to a
            // stable but generic port; the lock acquire will retry with the
            // real path once BridgeSession.ProjectPath is available.
            return "unity-open-mcp-unknown-project";
        }

        // Set when Start() fails (bind refusal or listener exception). Cleared on
        // success. Surfaced on the bridge Status tab so operators see recovery
        // steps instead of only a Console LogError.
        public static string LastStartError { get; private set; }

        public static void Start()
        {
            LastStartError = null;
            if (_running) return;

            // M14 T5.4 — resolve the bind address through the policy. Remote
            // (0.0.0.0) is refused unless authMode is "required". The decision
            // is made BEFORE touching HttpListener so a misconfigured project
            // fails fast with the actionable refusal message instead of a
            // generic listener exception.
            var bindAddress = BridgeProjectSettings.BindAddress;
            var bindDecision = BridgeBindAddress.Decide(bindAddress, BridgeAuthPolicy.GetDefault());
            if (!bindDecision.Allowed)
            {
                LastStartError = bindDecision.RefusalReason;
                UnityEngine.Debug.LogError(
                    $"[Unity Open MCP Bridge] Refusing to start: {bindDecision.RefusalReason}");
                _running = false;
                BridgeSession.SetConnected(false);
                return;
            }
            var effectiveBind = bindDecision.ResolvedAddress;

            Exception lastFailure = null;
            for (var attempt = 0; attempt <= PortBindRetryAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    ForceStopListener(releaseLock: false, logStopped: false);
                    Thread.Sleep(PortBindRetryDelayMs * attempt);
                    UnityEngine.Debug.LogWarning(
                        $"[Unity Open MCP Bridge] Port {_port} in use — in-process recovery attempt " +
                        $"{attempt}/{PortBindRetryAttempts}...");
                }

                if (TryStartListener(effectiveBind, out var failure))
                {
                    if (attempt > 0)
                    {
                        UnityEngine.Debug.Log(
                            $"[Unity Open MCP Bridge] Listener recovered on port {_port} " +
                            $"after {attempt} retry attempt(s).");
                    }
                    return;
                }

                lastFailure = failure;
                if (failure == null || !BridgeStartRecovery.IsPortInUseError(failure.Message))
                    break;
            }

            LastStartError = lastFailure?.Message ?? "Unknown listener start failure.";
            UnityEngine.Debug.LogError(
                $"[Unity Open MCP Bridge] Failed to start listener: {LastStartError}");
            _running = false;
            BridgeSession.SetConnected(false);

            // Defense in depth: when Start fails after a domain reload, the
            // lock on disk is frozen at state="reloading" with a heartbeatAt
            // from before the reload. The MCP server reads that stale-heartbeat
            // + live-PID signature as dead_bridge even though this Editor is
            // alive (just unable to bind — e.g. a foreign process holds the
            // port). If we already hold a lock from a prior successful Start in
            // this process, restart the heartbeat so the lock advances and the
            // classification reflects reality (idle, not stuck reloading). We
            // deliberately do NOT mint a new lock here — the contract is to
            // never advertise a port nothing is listening on (Acquire rewrites
            // port/pid), and we have no listener to back it.
            if (BridgeInstanceLock.IsAcquired)
            {
                BridgeHeartbeat.Start();
            }
        }

        // Bind the HTTP listener, start the worker thread, and publish the
        // instance lock + heartbeat. Cleans up partial state on failure.
        private static bool TryStartListener(string effectiveBind, out Exception failure)
        {
            failure = null;
            CleanupPartialListener();

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://{effectiveBind}:{_port}/");
                _listener.Start();
                _running = true;
                BridgeSession.SetConnected(true);

                _listenerThread = new Thread(ListenLoop)
                {
                    Name = "Unity Open MCP Bridge HTTP Listener",
                    IsBackground = true
                };
                _listenerThread.Start();

                // M13 T4.3/T4.7 — publish our instance + start the heartbeat
                // only after the listener is bound, so the lock never
                // advertises a port nothing is listening on. Acquire rewrites
                // the path/port from BridgeSession (the authoritative source)
                // rather than the port-resolver fallback used at static init.
                BridgeInstanceLock.Acquire(BridgeSession.ProjectPath ?? GetProjectPathForPort(), _port);
                BridgeHeartbeat.Start();

                var bindNote = BridgeBindAddress.IsRemote(effectiveBind)
                    ? " (remote — authMode required)"
                    : "";
                UnityEngine.Debug.Log($"[Unity Open MCP Bridge] Listening on http://{effectiveBind}:{_port}/{bindNote}");
                return true;
            }
            catch (System.Exception e)
            {
                failure = e;
                CleanupPartialListener();
                _running = false;
                BridgeSession.SetConnected(false);
                return false;
            }
        }

        // Tear down listener + heartbeat regardless of _running. Used on domain
        // reload, manual Stop, and port-in-use recovery. Lock retention follows
        // releaseLock (false on reload/recovery, true on graceful quit).
        internal static void ForceStopListener(bool releaseLock, bool logStopped)
        {
            _running = false;
            BridgeSession.SetConnected(false);

            try { BridgeHeartbeat.Stop(); } catch { }
            if (releaseLock)
            {
                try { BridgeInstanceLock.Release(); } catch { }
            }

            CleanupPartialListener();

            if (logStopped)
                UnityEngine.Debug.Log("[Unity Open MCP Bridge] Stopped.");
        }

        private static void CleanupPartialListener()
        {
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;

            try { _listenerThread?.Join(2000); } catch { }
            _listenerThread = null;
        }

        public static void Stop() => Stop(releaseLock: true);

        // Stop the HTTP listener. releaseLock=false (domain reload) keeps the
        // instance lock on disk so the MCP server can detect a dead bridge
        // assembly via the stale-heartbeat + live-PID signature; releaseLock=true
        // (graceful quit) deletes it.
        private static void Stop(bool releaseLock)
        {
            if (!_running && _listener == null && _listenerThread == null && !BridgeHeartbeat.IsRunning)
                return;

            ForceStopListener(releaseLock, logStopped: true);
        }

        private static void ListenLoop()
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
                        UnityEngine.Debug.LogError($"[Unity Open MCP Bridge] Listener error: {e.Message}");
                }
            }
        }

        private static void HandleRequest(HttpListenerContext context)
        {
            var activity = BridgeActivityRecorder.BeginActivity(context);
            BridgeActivityRecorder.CurrentActivity = activity;
            try
            {
                // M14 — auth check runs before routing so every endpoint
                // (/ping, /instance, /tools/*, /events, ...) is gated equally
                // when authMode is "required". The MCP client always carries
                // the bearer from the instance lock; a hand-rolled curl without
                // it gets a 401. No endpoint is exempt.
                if (!CheckAuth(context, activity))
                {
                    return;
                }

                var path = context.Request.Url.AbsolutePath.TrimEnd('/');
                switch (path)
                {
                    case "/ping":
                        activity.Kind = BridgeActivityKind.Ping;
                        HandlePing(context);
                        break;
                    case "/instance":
                        activity.Kind = BridgeActivityKind.Ping;
                        HandleInstance(context);
                        break;
                    case "/events":
                        if (context.Request.HttpMethod == "GET")
                        {
                            activity.Kind = BridgeActivityKind.ResourceRequest;
                            HandleEventsSse(context);
                        }
                        else
                        {
                            activity.Kind = BridgeActivityKind.ResourceRequest;
                            BridgeHttpResponse.SendJsonError(context, 405, "method_not_allowed", "GET required for /events");
                        }
                        break;
                    case "/events/poll":
                        if (context.Request.HttpMethod == "GET")
                        {
                            activity.Kind = BridgeActivityKind.ResourceRequest;
                            HandleEventsPoll(context);
                        }
                        else
                        {
                            activity.Kind = BridgeActivityKind.ResourceRequest;
                            BridgeHttpResponse.SendJsonError(context, 405, "method_not_allowed", "GET required for /events/poll");
                        }
                        break;
                    case "/resources":
                        if (context.Request.HttpMethod == "GET")
                        {
                            activity.Kind = BridgeActivityKind.ResourceRequest;
                            HandleResourceList(context);
                        }
                        else
                        {
                            activity.Kind = BridgeActivityKind.ResourceRequest;
                            BridgeHttpResponse.SendJsonError(context, 405, "method_not_allowed", "GET required for resource endpoints");
                        }
                        break;
                    case "/tools":
                        // M18 Plan 2 / T18.2.3 — compiled-state capability
                        // endpoint. Returns the set of tool names the bridge
                        // compiled in (KnownTools ∪ BridgeToolRegistry). The
                        // MCP server consults this from capabilities /
                        // manage_tools list_groups to report per-group
                        // availability (e.g. whether the navigation domain
                        // compiled in via UNITY_OPEN_MCP_EXT_NAVIGATION). Read-
                        // only, gate-free.
                        if (context.Request.HttpMethod == "GET")
                        {
                            activity.Kind = BridgeActivityKind.ResourceRequest;
                            HandleToolsList(context);
                        }
                        else
                        {
                            activity.Kind = BridgeActivityKind.ResourceRequest;
                            BridgeHttpResponse.SendJsonError(context, 405, "method_not_allowed", "GET required for /tools");
                        }
                        break;
                    default:
                        if (path.StartsWith("/tools/"))
                        {
                            var toolName = path.Substring("/tools/".Length);
                            activity.ToolName = toolName;
                            if (context.Request.HttpMethod == "POST")
                            {
                                if (KnownTools.Contains(toolName) || BridgeToolRegistry.Contains(toolName))
                                {
                                    activity.Kind = BridgeActivityKind.ToolRequest;
                                    HandleToolDispatch(context, toolName);
                                }
                                else
                                {
                                    activity.Kind = BridgeActivityKind.ToolError;
                                    activity.Outcome = BridgeActivityOutcome.Failed;
                                    activity.ErrorCode = "tool_not_found";
                                    BridgeHttpResponse.SendToolNotFound(context, toolName);
                                }
                            }
                            else
                            {
                                activity.Kind = BridgeActivityKind.ToolError;
                                activity.Outcome = BridgeActivityOutcome.Failed;
                                activity.ErrorCode = "method_not_allowed";
                                BridgeHttpResponse.SendJsonError(context, 405, "method_not_allowed", "POST required for tool endpoints");
                            }
                        }
                        else if (path.StartsWith("/resources/"))
                        {
                            if (context.Request.HttpMethod == "GET")
                            {
                                var route = path.Substring("/resources/".Length);
                                activity.Kind = BridgeActivityKind.ResourceRequest;
                                HandleResourceDispatch(context, route);
                            }
                            else
                            {
                                activity.Kind = BridgeActivityKind.ResourceRequest;
                                BridgeHttpResponse.SendJsonError(context, 405, "method_not_allowed", "GET required for resource endpoints");
                            }
                        }
                        else
                        {
                            activity.Kind = BridgeActivityKind.UnknownPath;
                            BridgeHttpResponse.SendNotFound(context, path);
                        }
                        break;
                }
            }
            catch
            {
                try
                {
                    activity.Kind = activity.Kind == BridgeActivityKind.ToolRequest
                        ? BridgeActivityKind.ToolError
                        : (activity.Kind == BridgeActivityKind.ResourceRequest
                            ? BridgeActivityKind.ResourceError
                            : activity.Kind);
                    activity.Outcome = BridgeActivityOutcome.Failed;
                    activity.ErrorCode = "bridge_internal_error";
                    BridgeHttpResponse.SendJsonError(context, 500, "bridge_internal_error", "Unhandled bridge exception");
                }
                catch { }
            }
            finally
            {
                BridgeActivityRecorder.FinishActivity(context, activity);
                // SSE owns its own response lifecycle — it streams for minutes and
                // closes the OutputStream itself. Closing here would abort the
                // long-lived stream the moment HandleRequest returned.
                if (!activity.StreamingResponse)
                {
                    try { context.Response.Close(); } catch { }
                }
            }
        }

        // M14 — bridge auth gate. Returns true when the request may proceed,
        // false when CheckAuth has already written a 401. Pure decision lives
        // in BridgeAuthCheck so it can be unit-tested without HttpListener.
        // The activity is annotated for the activity log regardless of outcome.
        private static bool CheckAuth(HttpListenerContext context, BridgeActivityEvent activity)
        {
            string headerValue = null;
            try { headerValue = context.Request.Headers["Authorization"]; }
            catch { /* malformed header — treat as missing */ }

            var policy = BridgeAuthPolicy.GetDefault();
            var expected = BridgeInstanceLock.AuthToken;

            if (BridgeAuthCheck.IsAuthorized(policy, headerValue, expected))
                return true;

            activity.Kind = BridgeActivityKind.UnknownPath;
            activity.Outcome = BridgeActivityOutcome.Failed;
            activity.ErrorCode = "unauthorized";
            BridgeHttpResponse.SendJsonError(context, 401, "unauthorized",
                "Missing or invalid Authorization header. Set authMode to \"none\" in " +
                ".unity-open-mcp/settings.json, or send Authorization: Bearer <token>.");
            return false;
        }

        // M23 Plan 3 — extract the agent identity from the X-Agent-Id header.
        // The MCP server sets it on every POST so the fair round-robin queue
        // can schedule across agents. When absent (e.g. a hand-rolled curl),
        // a synthetic per-request id is used so single-agent traffic still
        // flows through the queue's single-agent bypass path.
        private static string ExtractAgentId(HttpListenerRequest request)
        {
            try
            {
                var id = request.Headers["X-Agent-Id"];
                if (!string.IsNullOrEmpty(id)) return id;
            }
            catch { /* malformed header — treat as missing */ }
            // Synthetic id for untracked callers. Uses the request's local
            // endpoint port as a disambiguator so two concurrent curl calls
            // from different sessions do not collapse into one agent.
            return "agent-anon-" + request.RemoteEndPoint?.Port;
        }

        private static void HandlePing(HttpListenerContext context)
        {
            if (!BridgeSession.IsInitialized)
            {
                var fallback = "{\"connected\":false,\"projectPath\":null,\"unityVersion\":null,\"bridgeVersion\":\"0.3.2\",\"mode\":\"live\",\"compiling\":true,\"isPlaying\":false}";
                BridgeHttpResponse.SendJson(context, 503, fallback);
                return;
            }
            var json = BridgeJson.BuildPingJson();
            BridgeHttpResponse.SendJson(context, 200, json);
        }

        // M13 T4.3 — /instance returns the live instance lock JSON so the MCP
        // server can verify the on-disk lock matches the live bridge (and
        // read the heartbeat state without an HTTP round-trip via the file).
        // Falls back to 503 when the bridge hasn't acquired a lock yet.
        private static void HandleInstance(HttpListenerContext context)
        {
            var json = BridgeInstanceLock.ReadCurrentJson();
            if (json == null)
            {
                BridgeHttpResponse.SendJson(context, 503, "{\"error\":{\"code\":\"no_instance\",\"message\":\"Bridge has not acquired an instance lock.\"}}");
                return;
            }
            BridgeHttpResponse.SendJson(context, 200, json);
        }

        // M13 T4.4 — SSE streaming endpoint. Subscribes to BridgeEventSource,
        // flushes the current backlog, then keeps the connection open and pushes
        // incremental events as they arrive. Long-lived on a ThreadPool worker
        // thread; the connection closes when the client disconnects, the bridge
        // stops, or the SSE timeout (10 min default) elapses.
        //
        // Query params:
        //   subscriber=<id>  — opaque id so a reconnecting client keeps its
        //                      cursor and doesn't replay events it already saw.
        //                      A new id is minted when omitted.
        //   max_per_poll=<n> — cap events per drain tick (default 100). Bounds
        //                      burst replay after a reconnect.
        private static void HandleEventsSse(HttpListenerContext context)
        {
            const int sseTimeoutMs = 10 * 60 * 1000;
            const int pollIntervalMs = 100;

            var query = context.Request.QueryString;
            var subscriber = query["subscriber"];
            var maxPerPollRaw = query["max_per_poll"];
            int maxPerPoll = 100;
            if (int.TryParse(maxPerPollRaw, out var parsed) && parsed > 0 && parsed <= 1000)
                maxPerPoll = parsed;

            if (string.IsNullOrEmpty(subscriber))
                subscriber = System.Guid.NewGuid().ToString("N");

            // Take ownership of the response lifecycle so HandleRequest's finally
            // does not Close() on us mid-stream.
            var activity = BridgeActivityRecorder.CurrentActivity;
            if (activity != null) activity.StreamingResponse = true;

            try
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream; charset=utf-8";
                context.Response.SendChunked = true;

                // Initial hello so the client immediately knows the subscriber id
                // and that the stream is live (without waiting for the first event).
                WriteSseEvent(context, "ready", "{\"subscriber\":\"" + BridgeJson.EscapeStringContent(subscriber) + "\"}");

                var deadline = DateTime.UtcNow.AddMilliseconds(sseTimeoutMs);
                while (_running && BridgeSession.Connected && DateTime.UtcNow < deadline)
                {
                    var drain = BridgeEventSource.Drain(subscriber, maxPerPoll);
                    if (drain.Events != null)
                    {
                        foreach (var evt in drain.Events)
                        {
                            WriteSseEvent(context, evt.Type, BridgeEventSource.RenderEvent(evt));
                        }
                    }
                    if (drain.Missed > 0)
                    {
                        WriteSseEvent(context, "missed", "{\"missed\":" + drain.Missed + "}");
                    }
                    // If we flushed anything, drain again immediately; otherwise
                    // pace the loop so we don't spin the worker thread.
                    if (drain.Events == null || drain.Events.Count == 0)
                    {
                        System.Threading.Thread.Sleep(pollIntervalMs);
                    }
                    context.Response.OutputStream.Flush();
                }

                try { WriteSseEvent(context, "close", "{\"reason\":\"timeout_or_shutdown\"}"); } catch { }
            }
            catch
            {
                // Client disconnect or write failure — exit silently.
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        private static void WriteSseEvent(HttpListenerContext context, string eventName, string data)
        {
            var sb = new StringBuilder(64 + data.Length);
            sb.Append("event: ").Append(eventName).Append('\n');
            // SSE data lines can't contain raw newlines; split multi-line data
            // (e.g. log stacks) across multiple `data:` prefixes.
            var lines = data.Split('\n');
            foreach (var line in lines)
            {
                sb.Append("data: ").Append(line).Append('\n');
            }
            sb.Append('\n');
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        // M13 T4.4 — non-SSE drain. Returns the events buffered since the
        // caller's last poll as a single JSON envelope. The MCP server uses
        // this surface (instead of SSE) because it lives behind a stdio
        // transport and would otherwise need to forward SSE→MCP notifications.
        private static void HandleEventsPoll(HttpListenerContext context)
        {
            var query = context.Request.QueryString;
            var subscriber = query["subscriber"];
            if (string.IsNullOrEmpty(subscriber))
                subscriber = System.Guid.NewGuid().ToString("N");

            int maxEvents = 100;
            if (int.TryParse(query["max_events"], out var parsed) && parsed > 0 && parsed <= 1000)
                maxEvents = parsed;

            var drain = BridgeEventSource.Drain(subscriber, maxEvents);
            BridgeHttpResponse.SendJson(context, 200, BridgeEventSource.RenderDrain(drain));
        }

        private static void HandleToolDispatch(HttpListenerContext context, string toolName)
        {
            var body = BridgeRequestBody.ReadRequestBody(context.Request);
            var timeoutMs = BridgeRequestBody.ExtractTimeoutMs(body);
            var activity = BridgeActivityRecorder.CurrentActivity;

            if (BridgeActivityLog.Verbose && activity != null && !string.IsNullOrEmpty(body))
            {
                activity.RequestSnippet = BridgeActivityLog.TruncateSnippet(body);
            }

            if (BridgeToolTogglePolicy.IsDisabled(toolName))
            {
                if (activity != null)
                {
                    activity.Kind = BridgeActivityKind.ToolDisabled;
                    activity.Outcome = BridgeActivityOutcome.Skipped;
                    activity.ErrorCode = BridgeToolTogglePolicy.DisabledErrorCode;
                }
                BridgeHttpResponse.SendJson(context, 200, BridgeToolTogglePolicy.BuildDisabledErrorJson(toolName));
                return;
            }

            if (DirectResponseTools.Contains(toolName))
            {
                HandleDirectResponseTool(context, toolName, body, timeoutMs);
                return;
            }

            var gateMode = BridgeRequestBody.ExtractGateMode(body);
            var sw = Stopwatch.StartNew();

            bool isRegistryTool = BridgeToolRegistry.TryGet(toolName, out var registryEntry);
            bool isMutating = MutatingTools.Contains(toolName) || (isRegistryTool && registryEntry.IsMutating);
            // Effective gate precedence (packages/bridge/AGENTS.md §Gate policy):
            //   request `gate`  →  project default (BridgeGateDefaultPolicy)  →  tool attribute.
            // ExtractGateMode already resolves (1) → (2); the [BridgeTool].Gate
            // attribute is catalog metadata only (the tool's recommended gate)
            // and must NOT override the project default here. Previously registry
            // tools let the attribute win, which silently bypassed the project
            // default the user set in the Settings/Gate tab.
            string effectiveGateMode = gateMode;

            // apply_fix in dry_run mode is a no-op mutation — the gate would run a
            // full checkpoint+validate cycle around a Describe() call that changes
            // nothing. Short-circuit to DispatchTool directly so dry-run previews
            // stay cheap (matches the MCP tool description: "Ignored for dry_run").
            if (toolName == "unity_open_mcp_apply_fix" && JsonBody.GetBool(body, "dry_run", true))
            {
                HandleDryRunApplyFix(context, body, gateMode, timeoutMs, activity, sw);
                return;
            }

            if (activity != null)
            {
                activity.GateMode = effectiveGateMode;
            }

            string[] pathsHint = null;
            if (isMutating)
            {
                pathsHint = JsonBody.GetStringArray(body, "paths_hint");
                if (pathsHint == null || pathsHint.Length == 0)
                {
                    if (toolName == "unity_open_mcp_apply_fix")
                    {
                        var issueId = JsonBody.GetString(body, "issue_id");
                        pathsHint = BridgeRequestBody.PathsFromIssueId(issueId);
                    }
                    else if (toolName == "unity_open_mcp_reserialize")
                    {
                        // reserialize's `paths` array IS the mutation scope — reuse it as the gate hint.
                        pathsHint = JsonBody.GetStringArray(body, "paths");
                    }
                    else if (toolName == "unity_open_mcp_assets_refresh")
                    {
                        // Refresh is whole-project by nature: when whole_project
                        // is true (default), paths_hint may be empty. Otherwise
                        // (scoped refresh) the caller must enumerate paths.
                        if (JsonBody.GetBool(body, "whole_project", true))
                        {
                            pathsHint = new[] { "Assets" };
                        }
                    }
                    else if (toolName == "unity_open_mcp_reimport_package")
                    {
                        // reimport_package's scope IS the named local package;
                        // there is no Assets/ path for the gate to validate
                        // (the source lives outside Assets/). Default the hint
                        // to Packages/<package_id> so the gate still has a
                        // non-empty scope without forcing the caller to pass
                        // one. A trailing @version is stripped.
                        var pid = JsonBody.GetString(body, "package_id");
                        if (!string.IsNullOrWhiteSpace(pid))
                        {
                            var bare = pid.Contains("@")
                                ? pid.Substring(0, pid.IndexOf('@'))
                                : pid;
                            pathsHint = new[] { "Packages/" + bare };
                        }
                    }

                    if (pathsHint == null || pathsHint.Length == 0)
                    {
                        bool skipPathsHint = toolName == "unity_open_mcp_execute_menu"
                            && ExecuteMenuTool.IsReadOnlyMenu(JsonBody.GetString(body, "menu_path"));

                        if (!skipPathsHint)
                        {
                            if (activity != null)
                            {
                                activity.Outcome = BridgeActivityOutcome.Failed;
                                activity.ErrorCode = "paths_hint_required";
                                activity.DurationMs = sw.ElapsedMilliseconds;
                            }
                            BridgeHttpResponse.SendJson(context, 200, BridgeJson.BuildPathsHintErrorEnvelope(toolName, effectiveGateMode));
                            return;
                        }
                    }
                }
            }

            try
            {
                // M23 Plan 3 — route through the fair round-robin queue instead
                // of dispatching directly to the main thread. The queue bypasses
                // scheduling for the single-agent case (identical to the old
                // direct dispatch) and activates read-batch/write-serialize
                // fairness only when ≥2 agents share this bridge. The agent id
                // comes from the X-Agent-Id header (set by the MCP server);
                // when absent a synthetic per-request id is used so single-
                // agent traffic still flows through the bypass path.
                string agentId = ExtractAgentId(context.Request);
                // isMutating was computed above (MutatingTools.Contains ||
                // registryEntry.IsMutating); reuse it so the queue knows
                // whether to count this as a write for the per-frame limit.
                GateDispatchResult result = null;
                System.Exception dispatchError = null;

                var queueTask = BridgeRequestQueue.Enqueue(agentId, toolName, isMutating, () =>
                {
                    try
                    {
                        result = DispatchWithGate(toolName, body, effectiveGateMode, pathsHint);
                    }
                    catch (System.Exception e)
                    {
                        dispatchError = e;
                        throw;
                    }
                });

                // Apply the timeout on the worker thread (the queue has no
                // built-in timeout). Wait for either completion or the deadline.
                if (!queueTask.Wait(timeoutMs))
                {
                    sw.Stop();
                    BridgeActivityRecorder.ApplyToolFailureToActivity(activity, "timeout", $"Tool {toolName} timed out after {timeoutMs}ms", sw.ElapsedMilliseconds);
                    BridgeHttpResponse.SendJson(context, 200, BridgeJson.BuildTimeoutEnvelope(toolName, effectiveGateMode, timeoutMs));
                    return;
                }

                // Re-throw dispatch exceptions so the catch blocks below build
                // the right error envelope (same shape as the pre-queue path).
                if (dispatchError != null) throw dispatchError;

                sw.Stop();

                // M13 T4.1 — compile-settle wait. Done on THIS worker thread,
                // not the main thread that DispatchWithGate ran on, because the
                // main thread is the one doing the compiling. Only wait when the
                // mutation actually succeeded and the policy requires it; a
                // failed mutation (e.g. scene_dirty refusal) should return
                // immediately with no settle wait.
                var lifecycle = ToolLifecycle.Resolve(toolName);
                if (result.Mutation != null && result.Mutation.Success
                    && ToolLifecycle.RequiresSettleWait(lifecycle))
                {
                    result.SettleMs = EditorSettleWait.Wait(lifecycle);
                }

                BridgeAuditRecorder.RecordGateRun(toolName, effectiveGateMode, result, pathsHint);
                BridgeActivityRecorder.ApplyToolResultToActivity(activity, result, sw.ElapsedMilliseconds);
                BridgeHttpResponse.SendJson(context, 200, BridgeJson.BuildGateEnvelope(result, effectiveGateMode, lifecycle));
            }
            catch (AggregateException ae)
            {
                sw.Stop();
                var inner = ae.InnerException;
                if (inner is MainThreadBlockedException)
                {
                    // specs/feedback.md 2026-07-03 — main thread never drained
                    // the dispatch (a Unity modal is blocking it). Surface a
                    // structured main_thread_blocked error instead of the
                    // generic timeout so the agent can react.
                    BridgeActivityRecorder.ApplyToolFailureToActivity(activity, "main_thread_blocked", inner.Message, sw.ElapsedMilliseconds);
                    BridgeHttpResponse.SendJson(context, 200, BridgeJson.BuildMainThreadBlockedEnvelope(toolName, effectiveGateMode, timeoutMs));
                }
                else if (inner is TimeoutException)
                {
                    BridgeActivityRecorder.ApplyToolFailureToActivity(activity, "timeout", inner.Message, sw.ElapsedMilliseconds);
                    BridgeHttpResponse.SendJson(context, 200, BridgeJson.BuildTimeoutEnvelope(toolName, effectiveGateMode, timeoutMs));
                }
                else
                {
                    BridgeActivityRecorder.ApplyToolFailureToActivity(activity, "execution_error", inner?.Message, sw.ElapsedMilliseconds);
                    BridgeHttpResponse.SendJson(context, 200, BridgeJson.BuildFaultEnvelope(inner, effectiveGateMode));
                }
            }
            catch (System.Exception e)
            {
                sw.Stop();
                BridgeActivityRecorder.ApplyToolFailureToActivity(activity, "execution_error", e.Message, sw.ElapsedMilliseconds);
                BridgeHttpResponse.SendJson(context, 200, BridgeJson.BuildFaultEnvelope(e, effectiveGateMode));
            }
        }

        private static GateDispatchResult DispatchWithGate(string toolName, string body, string gateMode, string[] pathsHint)
        {
            // M22 T22.1.3 — capture console entries emitted during this dispatch
            // (scene-dirty guard + checkpoint + validate + mutate) as a before/
            // after delta. Must run on the main thread (this whole method is
            // dispatched via MainThreadDispatcher) — LogEntries is main-thread-
            // only. Captured entries are attached to the result so
            // BuildGateEnvelope can surface them as the `logs` array.
            int captureStart = LogEntriesReader.StartCapture();
            // No try/catch: if DispatchWithGateCore throws, the exception propagates
            // before this line is reached, so `result` is always assigned before use.
            // StopCapture returns empty when unavailable (older Unity).
            var result = DispatchWithGateCore(toolName, body, gateMode, pathsHint);
            result.Logs = LogEntriesReader.StopCapture(captureStart);
            return result;
        }

        private static GateDispatchResult DispatchWithGateCore(string toolName, string body, string gateMode, string[] pathsHint)
        {
            bool isMutating = MutatingTools.Contains(toolName)
                || (BridgeToolRegistry.TryGet(toolName, out var regEntry) && regEntry.IsMutating);

            var mode = GatePolicy.ParseMode(gateMode);

            // M13 T4.2 — active-scene dirty guard. Only ops that can disrupt the
            // editor (recompile / scene switch) are preflighted; mutating-but-
            // settled ops (apply_fix, reserialize) never trigger the native save
            // modal, so guarding them would just add friction. Runs on the main
            // thread (this whole method is dispatched via MainThreadDispatcher),
            // which is required for EditorSceneManager access.
            if (SceneDirtyGuard.AppliesTo(toolName, body))
            {
                var guard = SceneDirtyGuard.Check();
                if (!guard.Allowed && SceneDirtyAutoSave.IsEnabled)
                {
                    SceneDirtyAutoSave.TrySaveAllDirty(out _, out _);
                    guard = SceneDirtyGuard.Check();
                }
                if (!guard.Allowed)
                {
                    return new GateDispatchResult
                    {
                        Mutation = ToolDispatchResult.Fail("scene_dirty", guard.RefusalMessage),
                        GateRan = false,
                        Outcome = GateOutcome.Failed,
                        GateFailed = true,
                        DirtyScenePaths = guard.DirtyScenePaths,
                        AgentNextSteps = BridgeJson.BuildSceneDirtyNextSteps(guard.DirtyScenePaths)
                    };
                }
            }

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

            // M25 Plan 2 — non-dry-run apply_fix runs through the rollback
            // runner so a fix that fails or introduces new errors under enforce
            // is restored to its pre-fix state. Reuses GatePolicy internally;
            // dry-run apply_fix is short-circuited earlier (HandleDryRunApplyFix).
            if (toolName == "unity_open_mcp_apply_fix")
                return ApplyFixGateRunner.Execute(body, gateMode, pathsHint);

            return GatePolicy.Execute(mode, pathsHint, () => DispatchTool(toolName, body));
        }

        private static ToolDispatchResult DispatchTool(string toolName, string body)
        {
            return toolName switch
            {
                "unity_open_mcp_execute_csharp" => ExecuteCSharpTool.Execute(body),
                "unity_open_mcp_invoke_method" => InvokeMethodTool.Execute(body),
                "unity_open_mcp_execute_menu" => ExecuteMenuTool.Execute(body),
                "unity_open_mcp_find_members" => FindMembersTool.Execute(body),
                "unity_open_mcp_validate_edit" => ValidateEditTool.Execute(body),
                "unity_open_mcp_checkpoint_create" => CheckpointCreateTool.Execute(body),
                "unity_open_mcp_delta" => DeltaTool.Execute(body),
                "unity_open_mcp_find_references" => FindReferencesTool.Execute(body),
                // unity_open_mcp_dependencies is registry-discovered (M22 Plan 3 /
                // T-fix-1): it lives in the Dependencies sub-asmdef and dispatches
                // via BridgeToolRegistry.TryDispatch (the switch fallback below).
                "unity_open_mcp_scan_paths" => ScanPathsTool.Execute(body),
                "unity_open_mcp_apply_fix" => ApplyFixTool.Execute(body),
                "unity_open_mcp_reserialize" => ReserializeAssetsTool.Execute(body),
                "unity_open_mcp_read_asset" => ReadAssetTool.Execute(body),
                "unity_open_mcp_search_assets" => SearchAssetsTool.Execute(body),
                // M16 Plan 1 — typed asset/material/shader/prefab tools.
                "unity_open_mcp_assets_create_folder" => AssetsTools.CreateFolder(body),
                "unity_open_mcp_assets_copy" => AssetsTools.Copy(body),
                "unity_open_mcp_assets_move" => AssetsTools.Move(body),
                "unity_open_mcp_assets_delete" => AssetsTools.Delete(body),
                "unity_open_mcp_assets_refresh" => AssetsTools.Refresh(body),
                "unity_open_mcp_material_create" => MaterialTools.Create(body),
                "unity_open_mcp_material_get_properties" => MaterialTools.GetProperties(body),
                "unity_open_mcp_material_set_property" => MaterialTools.SetProperty(body),
                "unity_open_mcp_material_get_keywords" => MaterialTools.GetKeywords(body),
                "unity_open_mcp_material_set_keyword" => MaterialTools.SetKeyword(body),
                "unity_open_mcp_material_set_shader" => MaterialTools.SetShader(body),
                "unity_open_mcp_shader_list_all" => ShaderTools.ListAll(body),
                "unity_open_mcp_shader_get_data" => ShaderTools.GetData(body),
                "unity_open_mcp_prefab_instantiate" => PrefabTools.Instantiate(body),
                "unity_open_mcp_prefab_create" => PrefabTools.Create(body),
                "unity_open_mcp_prefab_open" => PrefabTools.Open(body),
                "unity_open_mcp_prefab_close" => PrefabTools.Close(body),
                "unity_open_mcp_prefab_save" => PrefabTools.Save(body),
                "unity_open_mcp_prefab_apply" => PrefabTools.Apply(body),
                "unity_open_mcp_prefab_revert" => PrefabTools.Revert(body),
                "unity_open_mcp_prefab_unpack" => PrefabTools.Unpack(body),
                "unity_open_mcp_prefab_get_overrides" => PrefabTools.GetOverrides(body),
                "unity_open_mcp_prefab_status" => PrefabTools.Status(body),
                // M16 Plan 2 — typed GameObject/component tools.
                "unity_open_mcp_gameobject_create" => GameObjectsTools.Create(body),
                "unity_open_mcp_gameobject_destroy" => GameObjectsTools.Destroy(body),
                "unity_open_mcp_gameobject_duplicate" => GameObjectsTools.Duplicate(body),
                "unity_open_mcp_gameobject_find" => GameObjectsTools.Find(body),
                "unity_open_mcp_gameobject_modify" => GameObjectsTools.Modify(body),
                "unity_open_mcp_gameobject_set_parent" => GameObjectsTools.SetParent(body),
                "unity_open_mcp_component_add" => ComponentsTools.Add(body),
                "unity_open_mcp_component_destroy" => ComponentsTools.Destroy(body),
                "unity_open_mcp_component_get" => ComponentsTools.Get(body),
                "unity_open_mcp_component_modify" => ComponentsTools.Modify(body),
                "unity_open_mcp_component_list_all" => ComponentsTools.ListAll(body),
                // M16 Plan 3 — typed scene lifecycle and data tools.
                "unity_open_mcp_scene_create" => ScenesTools.Create(body),
                "unity_open_mcp_scene_open" => ScenesTools.Open(body),
                "unity_open_mcp_scene_save" => ScenesTools.Save(body),
                "unity_open_mcp_scene_unload" => ScenesTools.Unload(body),
                "unity_open_mcp_scene_set_active" => ScenesTools.SetActive(body),
                "unity_open_mcp_scene_list_opened" => ScenesTools.ListOpened(body),
                "unity_open_mcp_scene_get_data" => ScenesTools.GetData(body),
                "unity_open_mcp_scene_get_dirty_summary" => ScenesTools.GetDirtySummary(body),
                "unity_open_mcp_scene_focus" => ScenesTools.Focus(body),
                // M20 Plan 9 / T20.9.4 — SceneView camera pose tools.
                // sceneview_get_camera is read-only (gate-free direct-response).
                // sceneview_set_camera mutates editor UI camera state and runs the
                // full gate path with paths_hint scoped to the active scene.
                "unity_open_mcp_sceneview_get_camera" => ScenesTools.SceneViewGetCamera(body),
                "unity_open_mcp_sceneview_set_camera" => ScenesTools.SceneViewSetCamera(body),
                // M16 Plan 4 — typed Package Manager tools. list / search /
                // get_info hit UPM async requests; get_dependencies / check
                // read Packages/manifest.json directly. add / remove write
                // manifest.json (paths_hint scoped to it by the caller).
                "unity_open_mcp_package_list" => PackagesTools.List(body),
                "unity_open_mcp_package_search" => PackagesTools.Search(body),
                "unity_open_mcp_package_add" => PackagesTools.Add(body),
                "unity_open_mcp_package_remove" => PackagesTools.Remove(body),
                "unity_open_mcp_reimport_package" => PackagesTools.ReimportPackage(body),
                "unity_open_mcp_package_get_info" => PackagesTools.GetInfo(body),
                "unity_open_mcp_package_get_dependencies" => PackagesTools.GetDependencies(body),
                "unity_open_mcp_package_check" => PackagesTools.Check(body),
                // M16 Plan 5 — typed console / editor state / selection / undo
                // / tags / layers tools. console_clear / console_log /
                // editor_set_state / selection_get / selection_set /
                // editor_undo / editor_redo / editor_get_tags /
                // editor_get_layers are gate-free direct-response tools (they
                // mutate editor state but write no assets). editor_add_tag /
                // editor_add_layer write ProjectSettings/TagManager.asset and
                // run the full gate path (MutatingTools below).
                "unity_open_mcp_console_clear" => EditorConsoleSelectionTools.ConsoleClear(body),
                "unity_open_mcp_console_log" => EditorConsoleSelectionTools.ConsoleLog(body),
                "unity_open_mcp_editor_set_state" => EditorConsoleSelectionTools.EditorSetState(body),
                "unity_open_mcp_selection_get" => EditorConsoleSelectionTools.SelectionGet(body),
                "unity_open_mcp_selection_set" => EditorConsoleSelectionTools.SelectionSet(body),
                "unity_open_mcp_editor_undo" => EditorConsoleSelectionTools.EditorUndo(body),
                "unity_open_mcp_editor_redo" => EditorConsoleSelectionTools.EditorRedo(body),
                // M20 Plan 9 / T20.9.4 — undo stack read/reset tools.
                "unity_open_mcp_editor_undo_history" => EditorConsoleSelectionTools.EditorUndoHistory(body),
                "unity_open_mcp_editor_clear_history" => EditorConsoleSelectionTools.EditorClearHistory(body),
                "unity_open_mcp_editor_get_tags" => EditorConsoleSelectionTools.EditorGetTags(body),
                "unity_open_mcp_editor_get_layers" => EditorConsoleSelectionTools.EditorGetLayers(body),
                "unity_open_mcp_editor_add_tag" => EditorConsoleSelectionTools.EditorAddTag(body),
                "unity_open_mcp_editor_add_layer" => EditorConsoleSelectionTools.EditorAddLayer(body),
                // M16 Plan 6 — typed reflection / scripts / object data tools.
                // type_schema / script_read / object_get_data are read-only
                // (DirectResponseTools); script_write / script_delete /
                // object_modify are mutators (MutatingTools). find_members /
                // invoke_method stay in their original case entries above,
                // enhanced in place.
                "unity_open_mcp_type_schema" => ReflectionScriptsObjectsTools.TypeSchema(body),
                "unity_open_mcp_script_read" => ReflectionScriptsObjectsTools.ScriptRead(body),
                "unity_open_mcp_script_write" => ReflectionScriptsObjectsTools.ScriptWrite(body),
                "unity_open_mcp_script_delete" => ReflectionScriptsObjectsTools.ScriptDelete(body),
                "unity_open_mcp_object_get_data" => ReflectionScriptsObjectsTools.ObjectGetData(body),
                "unity_open_mcp_object_modify" => ReflectionScriptsObjectsTools.ObjectModify(body),
                // M20 Plan 5 / T20.5.1 — typed ScriptableObject create + list-by-
                // type. scriptableobject_create is a mutator (MutatingTools);
                // list_assets_of_type is read-only (DirectResponseTools).
                "unity_open_mcp_scriptableobject_create" => ReflectionScriptsObjectsTools.ScriptableObjectCreate(body),
                "unity_open_mcp_list_assets_of_type" => ReflectionScriptsObjectsTools.ListAssetsOfType(body),
                // M20 Plan 5 / T20.5.2 — typed Assembly Definition tools. asmdef_list
                // / asmdef_get are read-only (DirectResponseTools); asmdef_create /
                // asmdef_modify are mutators with RestartThenSettle (creating or
                // editing an asmdef triggers a domain reload + recompile).
                "unity_open_mcp_asmdef_list" => AssemblyDefinitionTools.List(body),
                "unity_open_mcp_asmdef_get" => AssemblyDefinitionTools.Get(body),
                "unity_open_mcp_asmdef_create" => AssemblyDefinitionTools.Create(body),
                "unity_open_mcp_asmdef_modify" => AssemblyDefinitionTools.Modify(body),
                // M16 Plan 7 — typed profiler session / diagnostics tools. All
                // are gate-free direct-response tools except profiler_save_data
                // (a mutator that writes a .json snapshot — MutatingTools).
                // The M10 capture / memory / rendering reads are NOT duplicated
                // — agents use them for per-frame hierarchy / allocator bytes
                // / GPU + QualitySettings batch.
                "unity_open_mcp_profiler_start" => ProfilerSessionTools.Start(body),
                "unity_open_mcp_profiler_stop" => ProfilerSessionTools.Stop(body),
                "unity_open_mcp_profiler_get_status" => ProfilerSessionTools.GetStatus(body),
                "unity_open_mcp_profiler_get_config" => ProfilerSessionTools.GetConfig(body),
                "unity_open_mcp_profiler_set_config" => ProfilerSessionTools.SetConfig(body),
                "unity_open_mcp_profiler_list_modules" => ProfilerSessionTools.ListModules(body),
                "unity_open_mcp_profiler_enable_module" => ProfilerSessionTools.EnableModule(body),
                "unity_open_mcp_profiler_clear_data" => ProfilerSessionTools.ClearData(body),
                "unity_open_mcp_profiler_save_data" => ProfilerSessionTools.SaveData(body),
                "unity_open_mcp_profiler_load_data" => ProfilerSessionTools.LoadData(body),
                "unity_open_mcp_profiler_get_script_stats" => ProfilerSessionTools.GetScriptStats(body),
                // M16 Plan 8 — typed gate intelligence tools. All read-only
                // direct-response tools (gate-free). impact_preview +
                // gate_budget_estimate are pre-mutation (scope-first);
                // mutation_explain is post-mutation (latest gate run or an
                // explicit checkpoint_id).
                "unity_open_mcp_impact_preview" => GateIntelligenceTools.ImpactPreview(body),
                "unity_open_mcp_gate_budget_estimate" => GateIntelligenceTools.GateBudgetEstimate(body),
                "unity_open_mcp_mutation_explain" => GateIntelligenceTools.MutationExplain(body),
                // M16 Plan 9 — typed build pipeline + project-settings tools.
                // The reads (build_get_targets / build_get_active_target /
                // build_get_scenes / build_get_defines / settings_get_*) are
                // gate-free direct-response tools; build_set_target /
                // build_set_scenes / build_set_defines / settings_set_* run
                // the full gate path with paths_hint on the touched
                // ProjectSettings asset. build_start requires the deny bypass
                // (gate: "off" + confirm_bypass: true) because
                // BuildPipeline.BuildPlayer is on the default deny list.
                "unity_open_mcp_build_get_targets" => BuildSettingsTools.GetTargets(body),
                "unity_open_mcp_build_get_active_target" => BuildSettingsTools.GetActiveTarget(body),
                "unity_open_mcp_build_set_target" => BuildSettingsTools.SetTarget(body),
                "unity_open_mcp_build_get_scenes" => BuildSettingsTools.GetScenes(body),
                "unity_open_mcp_build_set_scenes" => BuildSettingsTools.SetScenes(body),
                "unity_open_mcp_build_start" => BuildSettingsTools.StartBuild(body),
                "unity_open_mcp_build_get_defines" => BuildSettingsTools.GetDefines(body),
                "unity_open_mcp_build_set_defines" => BuildSettingsTools.SetDefines(body),
                "unity_open_mcp_settings_get_player" => BuildSettingsTools.SettingsGetPlayer(body),
                "unity_open_mcp_settings_set_player" => BuildSettingsTools.SettingsSetPlayer(body),
                "unity_open_mcp_settings_get_quality" => BuildSettingsTools.SettingsGetQuality(body),
                "unity_open_mcp_settings_set_quality" => BuildSettingsTools.SettingsSetQuality(body),
                "unity_open_mcp_settings_get_physics" => BuildSettingsTools.SettingsGetPhysics(body),
                "unity_open_mcp_settings_set_physics" => BuildSettingsTools.SettingsSetPhysics(body),
                "unity_open_mcp_settings_get_lighting" => BuildSettingsTools.SettingsGetLighting(body),
                "unity_open_mcp_settings_set_lighting" => BuildSettingsTools.SettingsSetLighting(body),
                // M20 Plan 9 / T20.9.3 — Project Settings remainder. Time +
                // quality-level mutators write ProjectSettings/*.asset (full
                // gate path); render_pipeline is a read-only probe (gate-free).
                "unity_open_mcp_settings_get_time" => BuildSettingsTools.SettingsGetTime(body),
                "unity_open_mcp_settings_set_time" => BuildSettingsTools.SettingsSetTime(body),
                "unity_open_mcp_settings_get_render_pipeline" => BuildSettingsTools.SettingsGetRenderPipeline(body),
                "unity_open_mcp_settings_set_quality_level" => BuildSettingsTools.SettingsSetQualityLevel(body),
                // M20 Plan 9 / T20.9.2 — KV preferences. PlayerPrefs + EditorPrefs
                // write to the registry / Library (not project assets), so they
                // route as direct-response mutators like editor_undo (gate-free).
                "unity_open_mcp_playerprefs_get" => PlayerPrefsTools.PlayerPrefsGet(body),
                "unity_open_mcp_playerprefs_set" => PlayerPrefsTools.PlayerPrefsSet(body),
                "unity_open_mcp_playerprefs_delete" => PlayerPrefsTools.PlayerPrefsDelete(body),
                "unity_open_mcp_editorprefs_get" => PlayerPrefsTools.EditorPrefsGet(body),
                "unity_open_mcp_editorprefs_set" => PlayerPrefsTools.EditorPrefsSet(body),
                "unity_open_mcp_editorprefs_delete" => PlayerPrefsTools.EditorPrefsDelete(body),
                _ => BridgeToolRegistry.TryDispatch(toolName, body)
                     ?? ToolDispatchResult.Fail("tool_not_found", $"Unknown tool: {toolName}")
            };
        }

        // dry_run apply_fix short-circuit — runs the fix preview (Describe / fix
        // list / unknown-fix listing) without invoking the gate. The gate only
        // has something to validate when the project actually changes, and a
        // dry-run never changes the project.
        private static void HandleDryRunApplyFix(
            HttpListenerContext context, string body, string gateMode,
            int timeoutMs, BridgeActivityEvent activity, Stopwatch sw)
        {
            try
            {
                var task = MainThreadDispatcher.EnqueueAsync(
                    () => ApplyFixTool.Execute(body), timeoutMs);
                var mutation = task.Result;

                // Build a minimal gate envelope that records the run as Skipped.
                var gateResult = new GateDispatchResult
                {
                    Mutation = mutation,
                    GateRan = false,
                    Outcome = mutation.Success ? GateOutcome.Skipped : GateOutcome.Failed,
                    GateFailed = !mutation.Success,
                };

                sw.Stop();
                BridgeAuditRecorder.RecordGateRun("unity_open_mcp_apply_fix", gateMode, gateResult, null);
                BridgeActivityRecorder.ApplyToolResultToActivity(activity, gateResult, sw.ElapsedMilliseconds);
                BridgeHttpResponse.SendJson(context, 200, BridgeJson.BuildGateEnvelope(gateResult, gateMode, LifecyclePolicy.EditorSettle));
            }
            catch (AggregateException ae)
            {
                sw.Stop();
                var inner = ae.InnerException;
                if (inner is MainThreadBlockedException)
                {
                    BridgeActivityRecorder.ApplyToolFailureToActivity(activity, "main_thread_blocked", inner.Message, sw.ElapsedMilliseconds);
                    BridgeHttpResponse.SendJson(context, 200, BridgeJson.BuildMainThreadBlockedEnvelope("unity_open_mcp_apply_fix", gateMode, timeoutMs));
                }
                else if (inner is TimeoutException)
                {
                    BridgeActivityRecorder.ApplyToolFailureToActivity(activity, "timeout", inner.Message, sw.ElapsedMilliseconds);
                    BridgeHttpResponse.SendJson(context, 200, BridgeJson.BuildTimeoutEnvelope("unity_open_mcp_apply_fix", gateMode, timeoutMs));
                }
                else
                {
                    BridgeActivityRecorder.ApplyToolFailureToActivity(activity, "execution_error", inner?.Message, sw.ElapsedMilliseconds);
                    BridgeHttpResponse.SendJson(context, 200, BridgeJson.BuildFaultEnvelope(inner, gateMode));
                }
            }
            catch (System.Exception e)
            {
                sw.Stop();
                BridgeActivityRecorder.ApplyToolFailureToActivity(activity, "execution_error", e.Message, sw.ElapsedMilliseconds);
                BridgeHttpResponse.SendJson(context, 200, BridgeJson.BuildFaultEnvelope(e, gateMode));
            }
        }

        private static void HandleDirectResponseTool(HttpListenerContext context, string toolName, string body, int timeoutMs)
        {
            // M22 T22.1.3 — direct-response tools bypass the gate envelope, so
            // capture happens inside the main-thread callback (alongside
            // DispatchTool) and the captured logs are spliced into the flat
            // tool JSON before it is returned. Read-only tools rarely emit
            // warnings, so this is usually an empty `logs` sibling; it still
            // keeps the field present on every response per the T22.1.3 contract.
            List<LogEntryInfo> capturedLogs = null;
            try
            {
                var task = MainThreadDispatcher.EnqueueAsync(() =>
                {
                    int captureStart = LogEntriesReader.StartCapture();
                    ToolDispatchResult r;
                    try { r = DispatchTool(toolName, body); }
                    finally { capturedLogs = LogEntriesReader.StopCapture(captureStart); }
                    return r;
                }, timeoutMs);

                var result = task.Result;

                if (result.Success && result.Output != null)
                    BridgeHttpResponse.SendJson(context, 200, BridgeJson.SpliceLogsField(result.Output, capturedLogs));
                else if (!result.Success)
                    BridgeHttpResponse.SendJson(context, 200, SpliceLogsIntoFlatError(BridgeJson.BuildDirectToolErrorJson(result), capturedLogs));
                else
                    BridgeHttpResponse.SendJson(context, 200, SpliceLogsIntoFlatError("{\"error\":{\"code\":\"empty_output\",\"message\":\"Tool returned empty output\"}}", capturedLogs));
            }
            catch (AggregateException ae)
            {
                var inner = ae.InnerException;
                if (inner is MainThreadBlockedException)
                    // Direct-response path: build a flat main_thread_blocked
                    // error pointing at the same recovery hints the gate path
                    // surfaces (the diagnostic is the value here, not the gate
                    // envelope shape).
                    BridgeHttpResponse.SendJson(context, 200, SpliceLogsIntoFlatError($"{{\"error\":{{\"code\":\"main_thread_blocked\",\"message\":\"Tool '{BridgeJson.EscapeStringContent(toolName)}' could not run — the Unity main thread is blocked by a modal dialog (unsaved changes, scene modified externally, safe mode). Do NOT raise timeout_ms. Check editor_status / bridge_status, call scene_save before retrying, or restart the editor.\"}}}}", capturedLogs));
                else if (inner is TimeoutException)
                    BridgeHttpResponse.SendJson(context, 200, SpliceLogsIntoFlatError($"{{\"error\":{{\"code\":\"timeout\",\"message\":\"Tool '{BridgeJson.EscapeStringContent(toolName)}' timed out after {timeoutMs}ms\"}}}}", capturedLogs));
                else
                    BridgeHttpResponse.SendJson(context, 200, SpliceLogsIntoFlatError($"{{\"error\":{{\"code\":\"execution_error\",\"message\":\"{BridgeJson.EscapeStringContent(inner?.Message ?? ae.Message)}\"}}}}", capturedLogs));
            }
            catch (System.Exception e)
            {
                BridgeHttpResponse.SendJson(context, 200, SpliceLogsIntoFlatError($"{{\"error\":{{\"code\":\"execution_error\",\"message\":\"{BridgeJson.EscapeStringContent(e.Message)}\"}}}}", capturedLogs));
            }
        }

        // Splice captured logs into a flat direct-response error body. Reuses
        // the same JSON-balanced injection as SpliceLogsField (which targets
        // tool success JSON); the error bodies are also single top-level
        // objects, so the same primitive applies. Null/empty logs pass through.
        private static string SpliceLogsIntoFlatError(string json, List<LogEntryInfo> logs)
            => BridgeJson.SpliceLogsField(json, logs);

        private static void HandleResourceList(HttpListenerContext context)
        {
            var resources = BridgeResourceRegistry.All();
            var sb = new StringBuilder(512);
            sb.Append('[');
            for (int i = 0; i < resources.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var r = resources[i];
                sb.Append('{');
                sb.Append("\"name\":").Append(BridgeJson.EscapeString(r.Name)).Append(',');
                sb.Append("\"route\":").Append(BridgeJson.EscapeString(r.Route)).Append(',');
                sb.Append("\"mimeType\":").Append(BridgeJson.EscapeString(r.MimeType)).Append(',');
                sb.Append("\"description\":").Append(r.Description != null ? BridgeJson.EscapeString(r.Description) : "null");
                sb.Append('}');
            }
            sb.Append(']');
            BridgeHttpResponse.SendJson(context, 200, sb.ToString());
        }

        // M18 Plan 2 / T18.2.3 — compiled-state tool inventory. Used by the
        // MCP server's capabilities surface and manage_tools list_groups to
        // report per-group availability. The output is the union of the
        // legacy KnownTools set and the [BridgeTool]-discovered registry.
        //
        // Group metadata is also surfaced here so the MCP server can render
        // the group catalog without a second round-trip. The catalog is the
        // authoritative per-bridge compiled-state view — only groups whose
        // tools compiled in appear with a non-empty tool roster.
        private static void HandleToolsList(HttpListenerContext context)
        {
            // Build the unioned tool-name set first.
            var names = new HashSet<string>(KnownTools, StringComparer.Ordinal);
            foreach (var entry in BridgeToolRegistry.All())
            {
                names.Add(entry.Name);
            }

            var sortedNames = new List<string>(names);
            sortedNames.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder(1024);
            sb.Append("{\"tools\":[");
            for (int i = 0; i < sortedNames.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(sortedNames[i]));
            }
            sb.Append(']');

            // Group → tools map (registry-side). KnownTools entries do not
            // carry group metadata — only registry-discovered tools have a
            // Group assignment on the attribute. This keeps the report
            // faithful to what the bridge actually compiled in.
            var groupToTools = BridgeToolRegistry.GroupToTools();
            sb.Append(",\"groups\":[");
            bool firstGroup = true;
            // Stable iteration order: alphabetical by group id.
            var groupIds = new List<string>(groupToTools.Keys);
            groupIds.Sort(StringComparer.Ordinal);
            foreach (var groupId in groupIds)
            {
                if (!firstGroup) sb.Append(',');
                firstGroup = false;
                var toolList = groupToTools[groupId];
                sb.Append('{');
                sb.Append("\"id\":").Append(BridgeJson.EscapeString(groupId)).Append(',');
                sb.Append("\"toolCount\":").Append(toolList.Count).Append(',');
                sb.Append("\"tools\":[");
                for (int i = 0; i < toolList.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(toolList[i]));
                }
                sb.Append("]}");
            }
            sb.Append(']');

            sb.Append('}');
            BridgeHttpResponse.SendJson(context, 200, sb.ToString());
        }

        private static void HandleResourceDispatch(HttpListenerContext context, string route)
        {
            var entry = BridgeResourceRegistry.FindByRoute(route);
            if (entry == null)
            {
                var json = $"{{\"error\":{{\"code\":\"resource_not_found\",\"message\":\"Unknown resource route: {BridgeJson.EscapeStringContent(route)}\"}}}}";
                BridgeHttpResponse.SendJson(context, 404, json);
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
                BridgeHttpResponse.SendJsonError(context, 500, "execution_error", inner ?? e.Message);
            }
        }
    }
}
