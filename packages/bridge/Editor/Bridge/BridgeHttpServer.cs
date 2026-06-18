using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge
{
    [InitializeOnLoad]
    public static class BridgeHttpServer
    {
        const string PortEnvVar = "UNITY_OPEN_MCP_BRIDGE_PORT";
        const string PortArgPrefix = "-UNITY_OPEN_MCP_BRIDGE_PORT=";
        const int DefaultTimeoutMs = 30000;
        const int MinTimeoutMs = 1000;
        // Matches the documented maximum in the run-tests tool schema
        // (mcp-server/src/tools/run-tests.ts). Previously 300000, which silently
        // clamped a caller's explicit value below the advertised ceiling.
        const int MaxTimeoutMs = 600000;

        static readonly HashSet<string> KnownTools = new()
        {
            "unity_open_mcp_execute_csharp",
            "unity_open_mcp_invoke_method",
            "unity_open_mcp_execute_menu",
            "unity_open_mcp_find_members",
            "unity_open_mcp_validate_edit",
            "unity_open_mcp_checkpoint_create",
            "unity_open_mcp_delta",
            "unity_open_mcp_find_references",
            "unity_open_mcp_scan_paths",
            "unity_open_mcp_apply_fix",
            "unity_open_mcp_reserialize",
            "unity_open_mcp_read_asset",
            "unity_open_mcp_search_assets",
            // M16 Plan 1 — typed asset/material/shader/prefab tools.
            "unity_open_mcp_assets_create_folder",
            "unity_open_mcp_assets_copy",
            "unity_open_mcp_assets_move",
            "unity_open_mcp_assets_delete",
            "unity_open_mcp_assets_refresh",
            "unity_open_mcp_material_create",
            "unity_open_mcp_material_get_properties",
            "unity_open_mcp_material_set_property",
            "unity_open_mcp_material_get_keywords",
            "unity_open_mcp_material_set_keyword",
            "unity_open_mcp_material_set_shader",
            "unity_open_mcp_shader_list_all",
            "unity_open_mcp_shader_get_data",
            "unity_open_mcp_prefab_instantiate",
            "unity_open_mcp_prefab_create",
            "unity_open_mcp_prefab_open",
            "unity_open_mcp_prefab_close",
            "unity_open_mcp_prefab_save",
            "unity_open_mcp_prefab_apply",
            "unity_open_mcp_prefab_revert",
            "unity_open_mcp_prefab_unpack",
            "unity_open_mcp_prefab_get_overrides",
            "unity_open_mcp_prefab_status",
            // M16 Plan 2 — typed GameObject/component tools.
            "unity_open_mcp_gameobject_create",
            "unity_open_mcp_gameobject_destroy",
            "unity_open_mcp_gameobject_duplicate",
            "unity_open_mcp_gameobject_find",
            "unity_open_mcp_gameobject_modify",
            "unity_open_mcp_gameobject_set_parent",
            "unity_open_mcp_component_add",
            "unity_open_mcp_component_destroy",
            "unity_open_mcp_component_get",
            "unity_open_mcp_component_modify",
            "unity_open_mcp_component_list_all",
            // M16 Plan 3 — typed scene management tools.
            "unity_open_mcp_scene_create",
            "unity_open_mcp_scene_open",
            "unity_open_mcp_scene_save",
            "unity_open_mcp_scene_unload",
            "unity_open_mcp_scene_set_active",
            "unity_open_mcp_scene_list_opened",
            "unity_open_mcp_scene_get_data",
            "unity_open_mcp_scene_get_dirty_summary",
            "unity_open_mcp_scene_focus",
            // M16 Plan 4 — typed Package Manager tools.
            "unity_open_mcp_package_list",
            "unity_open_mcp_package_search",
            "unity_open_mcp_package_add",
            "unity_open_mcp_package_remove",
            "unity_open_mcp_package_get_info",
            "unity_open_mcp_package_get_dependencies",
            "unity_open_mcp_package_check",
            // M16 Plan 5 — typed console / editor state / selection / undo /
            // tags / layers tools.
            "unity_open_mcp_console_clear",
            "unity_open_mcp_console_log",
            "unity_open_mcp_editor_set_state",
            "unity_open_mcp_selection_get",
            "unity_open_mcp_selection_set",
            "unity_open_mcp_editor_undo",
            "unity_open_mcp_editor_redo",
            "unity_open_mcp_editor_get_tags",
            "unity_open_mcp_editor_get_layers",
            "unity_open_mcp_editor_add_tag",
            "unity_open_mcp_editor_add_layer",
            "unity_senses_run_tests",
            "unity_senses_screenshot",
            "unity_senses_read_console",
            "unity_senses_profiler_capture",
            "unity_senses_profiler_memory",
            "unity_senses_profiler_rendering",
            "unity_senses_spatial_query"
        };

        static readonly HashSet<string> DirectResponseTools = new()
        {
            "unity_open_mcp_validate_edit",
            "unity_open_mcp_checkpoint_create",
            "unity_open_mcp_delta",
            "unity_open_mcp_find_references",
            "unity_open_mcp_scan_paths",
            // Compact drill-down reads: bridge returns the structured model JSON
            // directly; the MCP server applies the shared compression module.
            "unity_open_mcp_read_asset",
            "unity_open_mcp_search_assets",
            // Test runner: starts async test run, returns { status, runId } directly.
            "unity_senses_run_tests",
            // Agent senses (non-mutating): return tool JSON directly.
            "unity_senses_screenshot",
            "unity_senses_read_console",
            "unity_senses_profiler_capture",
            "unity_senses_profiler_memory",
            "unity_senses_profiler_rendering",
            "unity_senses_spatial_query",
            // M16 Plan 1 — read-only typed tools (gate-free). They return JSON
            // directly without the gate envelope, matching search_assets/read_asset.
            "unity_open_mcp_material_get_properties",
            "unity_open_mcp_material_get_keywords",
            "unity_open_mcp_shader_list_all",
            "unity_open_mcp_shader_get_data",
            "unity_open_mcp_prefab_get_overrides",
            "unity_open_mcp_prefab_status",
            // M16 Plan 2 — read-only typed tools (gate-free).
            "unity_open_mcp_gameobject_find",
            "unity_open_mcp_component_get",
            "unity_open_mcp_component_list_all",
            // M16 Plan 3 — read-only typed tools (gate-free). scene_get_data
            // is the structured scene hierarchy read that supersedes the
            // standalone M10 scene snapshot.
            "unity_open_mcp_scene_list_opened",
            "unity_open_mcp_scene_get_data",
            "unity_open_mcp_scene_get_dirty_summary",
            // M16 Plan 4 — read-only typed Package Manager tools (gate-free).
            // list / search / get_info hit UPM async requests;
            // get_dependencies / check read Packages/manifest.json directly.
            "unity_open_mcp_package_list",
            "unity_open_mcp_package_search",
            "unity_open_mcp_package_get_info",
            "unity_open_mcp_package_get_dependencies",
            "unity_open_mcp_package_check",
            // M16 Plan 5 — gate-free typed editor-state tools. They mutate
            // editor state (console, selection, play mode, undo/redo) but
            // write NO assets, so the gate (asset-reference validation) has
            // nothing to validate. editor_set_state runs its own inline dirty
            // guard (entering play mode can trigger Unity's native save modal).
            // editor_get_tags / editor_get_layers are pure reads.
            // editor_add_tag / editor_add_layer are NOT here — they write
            // TagManager.asset and run the full gate (see MutatingTools).
            "unity_open_mcp_console_clear",
            "unity_open_mcp_console_log",
            "unity_open_mcp_editor_set_state",
            "unity_open_mcp_selection_get",
            "unity_open_mcp_selection_set",
            "unity_open_mcp_editor_undo",
            "unity_open_mcp_editor_redo",
            "unity_open_mcp_editor_get_tags",
            "unity_open_mcp_editor_get_layers"
        };

        static readonly HashSet<string> MutatingTools = new()
        {
            "unity_open_mcp_execute_csharp",
            "unity_open_mcp_invoke_method",
            "unity_open_mcp_execute_menu",
            "unity_open_mcp_apply_fix",
            "unity_open_mcp_reserialize",
            // M16 Plan 1 — typed asset/material/prefab mutators. Each requires
            // paths_hint; assets_refresh is a light mutation that may bind
            // whole-project scope when whole_project: true (handled below).
            "unity_open_mcp_assets_create_folder",
            "unity_open_mcp_assets_copy",
            "unity_open_mcp_assets_move",
            "unity_open_mcp_assets_delete",
            "unity_open_mcp_assets_refresh",
            "unity_open_mcp_material_create",
            "unity_open_mcp_material_set_property",
            "unity_open_mcp_material_set_keyword",
            "unity_open_mcp_material_set_shader",
            "unity_open_mcp_prefab_instantiate",
            "unity_open_mcp_prefab_create",
            "unity_open_mcp_prefab_open",
            "unity_open_mcp_prefab_close",
            "unity_open_mcp_prefab_save",
            "unity_open_mcp_prefab_apply",
            "unity_open_mcp_prefab_revert",
            "unity_open_mcp_prefab_unpack",
            // M16 Plan 2 — typed GameObject/component mutators. Each requires
            // paths_hint scoped to the scene that contains (or will contain)
            // the target. They touch scene hierarchy only — no asset writes.
            "unity_open_mcp_gameobject_create",
            "unity_open_mcp_gameobject_destroy",
            "unity_open_mcp_gameobject_duplicate",
            "unity_open_mcp_gameobject_modify",
            "unity_open_mcp_gameobject_set_parent",
            "unity_open_mcp_component_add",
            "unity_open_mcp_component_destroy",
            "unity_open_mcp_component_modify",
            // M16 Plan 3 — typed scene mutators. paths_hint is the scene asset
            // path (or scene hierarchy path for scene_focus). scene_open is
            // RestartThenSettle (Single-mode open can lose unsaved changes in
            // currently-open scenes — the dirty guard preflights it).
            "unity_open_mcp_scene_create",
            "unity_open_mcp_scene_open",
            "unity_open_mcp_scene_save",
            "unity_open_mcp_scene_unload",
            "unity_open_mcp_scene_set_active",
            "unity_open_mcp_scene_focus",
            // M16 Plan 4 — typed Package Manager mutators. Each writes
            // Packages/manifest.json and triggers package resolution; the
            // caller must scope paths_hint to "Packages/manifest.json"
            // (the lock file is touched implicitly).
            "unity_open_mcp_package_add",
            "unity_open_mcp_package_remove",
            // M16 Plan 5 — typed TagManager mutators. Each rewrites
            // ProjectSettings/TagManager.asset; the caller must scope
            // paths_hint to that asset. The other Plan 5 tools mutate editor
            // state (console, selection, play mode, undo/redo) but write NO
            // assets, so they are NOT mutating in gate terms — they route as
            // gate-free direct-response tools (see DirectResponseTools).
            "unity_open_mcp_editor_add_tag",
            "unity_open_mcp_editor_add_layer"
        };

        static HttpListener _listener;
        static Thread _listenerThread;
        static volatile bool _running;
        static int _port;

        // Per-request activity record. Set on the listener worker thread at the start
        // of HandleRequest and read by nested handlers (e.g. HandleToolDispatch) before
        // FinishActivity records it to the ring buffer. Thread-static because each
        // request runs on a ThreadPool worker.
        [ThreadStatic] static BridgeActivityEvent _currentActivity;
        static BridgeActivityEvent CurrentActivity => _currentActivity;

        public static int Port => _port;
        public static bool IsRunning => _running;

        static BridgeHttpServer()
        {
            BridgeToolRegistry.Scan();
            BridgeResourceRegistry.Scan();

            _port = ResolvePort();
            Start();

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;
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
        static void OnBeforeAssemblyReload() => Stop(releaseLock: false);

        // Graceful editor quit: full release — delete the lock so a stale entry
        // doesn't linger for a closed editor.
        static void OnQuitting() => Stop(releaseLock: true);

        // M13 T4.3 — Per-project port with override precedence:
        //   1. UNITY_OPEN_MCP_BRIDGE_PORT env var
        //   2. -UNITY_OPEN_MCP_BRIDGE_PORT=<n> CLI arg
        //   3. deterministic hash of the project path (20000 + sha256 % 10000)
        // An explicit override always wins so existing configs that pin a port
        // keep working; the hash default lets two projects run bridges
        // concurrently on different ports with zero configuration. The MCP
        // server computes the same hash and reads the lock file, so it finds
        // the right bridge per project without sharing config.
        static int ResolvePort()
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

        // The project path used for port hashing. Falls back to a stable
        // placeholder if the editor hasn't initialized Application.dataPath
        // yet — in practice this runs inside [InitializeOnLoad] after the
        // project is loaded, but we never want to throw during static init.
        static string GetProjectPathForPort()
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

        public static void Start()
        {
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
                UnityEngine.Debug.LogError(
                    $"[Unity Open MCP Bridge] Refusing to start: {bindDecision.RefusalReason}");
                _running = false;
                BridgeSession.SetConnected(false);
                return;
            }
            var effectiveBind = bindDecision.ResolvedAddress;

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
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[Unity Open MCP Bridge] Failed to start listener: {e.Message}");
                _running = false;
                BridgeSession.SetConnected(false);
            }
        }

        public static void Stop() => Stop(releaseLock: true);

        // Stop the HTTP listener. releaseLock=false (domain reload) keeps the
        // instance lock on disk so the MCP server can detect a dead bridge
        // assembly via the stale-heartbeat + live-PID signature; releaseLock=true
        // (graceful quit) deletes it.
        static void Stop(bool releaseLock)
        {
            if (!_running) return;
            _running = false;
            BridgeSession.SetConnected(false);

            // Stop the heartbeat first so it can't rewrite the lock after a
            // release. When releaseLock is false (domain reload), the heartbeat
            // has already forced a "reloading" write before this method runs.
            try { BridgeHeartbeat.Stop(); } catch { }
            if (releaseLock)
            {
                try { BridgeInstanceLock.Release(); } catch { }
            }

            try
            {
                _listener?.Stop();
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogWarning($"[Unity Open MCP Bridge] Error stopping listener: {e.Message}");
            }

            _listener = null;

            try
            {
                _listenerThread?.Join(2000);
            }
            catch { }

            _listenerThread = null;
            UnityEngine.Debug.Log("[Unity Open MCP Bridge] Stopped.");
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
                        UnityEngine.Debug.LogError($"[Unity Open MCP Bridge] Listener error: {e.Message}");
                }
            }
        }

        static void HandleRequest(HttpListenerContext context)
        {
            var activity = BeginActivity(context);
            _currentActivity = activity;
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
                            SendJsonError(context, 405, "method_not_allowed", "GET required for /events");
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
                            SendJsonError(context, 405, "method_not_allowed", "GET required for /events/poll");
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
                            SendJsonError(context, 405, "method_not_allowed", "GET required for resource endpoints");
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
                                    SendToolNotFound(context, toolName);
                                }
                            }
                            else
                            {
                                activity.Kind = BridgeActivityKind.ToolError;
                                activity.Outcome = BridgeActivityOutcome.Failed;
                                activity.ErrorCode = "method_not_allowed";
                                SendJsonError(context, 405, "method_not_allowed", "POST required for tool endpoints");
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
                                SendJsonError(context, 405, "method_not_allowed", "GET required for resource endpoints");
                            }
                        }
                        else
                        {
                            activity.Kind = BridgeActivityKind.UnknownPath;
                            SendNotFound(context, path);
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
                    SendJsonError(context, 500, "bridge_internal_error", "Unhandled bridge exception");
                }
                catch { }
            }
            finally
            {
                FinishActivity(context, activity);
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
        static bool CheckAuth(HttpListenerContext context, BridgeActivityEvent activity)
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
            SendJsonError(context, 401, "unauthorized",
                "Missing or invalid Authorization header. Set authMode to \"none\" in " +
                ".unity-open-mcp/settings.json, or send Authorization: Bearer <token>.");
            return false;
        }

        static BridgeActivityEvent BeginActivity(HttpListenerContext context)
        {
            var evt = new BridgeActivityEvent
            {
                Timestamp = DateTime.Now,
                Kind = BridgeActivityKind.UnknownPath,
                ToolName = null,
                GateMode = null,
                Outcome = BridgeActivityOutcome.Unknown,
                DurationMs = 0,
                HttpStatus = 0,
                RequestBodyLength = SafeContentLength(context?.Request),
                ErrorCode = null,
                ErrorMessage = null
            };
            return evt;
        }

        static int SafeContentLength(HttpListenerRequest request)
        {
            if (request == null) return 0;
            try
            {
                var cl = request.ContentLength64;
                if (cl > 0 && cl < int.MaxValue) return (int)cl;
                return 0;
            }
            catch { return 0; }
        }

        static void FinishActivity(HttpListenerContext context, BridgeActivityEvent activity)
        {
            if (activity == null) return;
            try
            {
                activity.HttpStatus = context?.Response?.StatusCode ?? 0;
                if (activity.Outcome == BridgeActivityOutcome.Unknown)
                {
                    if (activity.HttpStatus >= 500) activity.Outcome = BridgeActivityOutcome.Failed;
                    else if (activity.HttpStatus >= 400) activity.Outcome = BridgeActivityOutcome.Failed;
                    else if (activity.HttpStatus > 0) activity.Outcome = BridgeActivityOutcome.Success;
                }
            }
            catch { }
            try { BridgeActivityLog.Record(activity); } catch { }
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

        // M13 T4.3 — /instance returns the live instance lock JSON so the MCP
        // server can verify the on-disk lock matches the live bridge (and
        // read the heartbeat state without an HTTP round-trip via the file).
        // Falls back to 503 when the bridge hasn't acquired a lock yet.
        static void HandleInstance(HttpListenerContext context)
        {
            var json = BridgeInstanceLock.ReadCurrentJson();
            if (json == null)
            {
                SendJson(context, 503, "{\"error\":{\"code\":\"no_instance\",\"message\":\"Bridge has not acquired an instance lock.\"}}");
                return;
            }
            SendJson(context, 200, json);
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
        static void HandleEventsSse(HttpListenerContext context)
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
            var activity = CurrentActivity;
            if (activity != null) activity.StreamingResponse = true;

            try
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream; charset=utf-8";
                context.Response.SendChunked = true;

                // Initial hello so the client immediately knows the subscriber id
                // and that the stream is live (without waiting for the first event).
                WriteSseEvent(context, "ready", "{\"subscriber\":\"" + EscapeStringContent(subscriber) + "\"}");

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

        static void WriteSseEvent(HttpListenerContext context, string eventName, string data)
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
        static void HandleEventsPoll(HttpListenerContext context)
        {
            var query = context.Request.QueryString;
            var subscriber = query["subscriber"];
            if (string.IsNullOrEmpty(subscriber))
                subscriber = System.Guid.NewGuid().ToString("N");

            int maxEvents = 100;
            if (int.TryParse(query["max_events"], out var parsed) && parsed > 0 && parsed <= 1000)
                maxEvents = parsed;

            var drain = BridgeEventSource.Drain(subscriber, maxEvents);
            SendJson(context, 200, BridgeEventSource.RenderDrain(drain));
        }

        static void HandleToolDispatch(HttpListenerContext context, string toolName)
        {
            var body = ReadRequestBody(context.Request);
            var timeoutMs = ExtractTimeoutMs(body);
            var activity = CurrentActivity;

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
                SendJson(context, 200, BridgeToolTogglePolicy.BuildDisabledErrorJson(toolName));
                return;
            }

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

            bool runtimeGateSpecified = !string.IsNullOrEmpty(body) && body.Contains("\"gate\"");
            if (isRegistryTool && !runtimeGateSpecified)
            {
                var attrGate = registryEntry.Gate;
                effectiveGateMode = attrGate switch
                {
                    GateMode.Enforce => "enforce",
                    GateMode.Warn => "warn",
                    GateMode.Off => "off",
                    _ => BridgeGateDefaultPolicy.GetDefault()
                };
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
                        pathsHint = PathsFromIssueId(issueId);
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
                            SendJson(context, 200, BuildPathsHintErrorEnvelope(toolName, effectiveGateMode));
                            return;
                        }
                    }
                }
            }

            try
            {
                var task = MainThreadDispatcher.EnqueueAsync(
                    () => DispatchWithGate(toolName, body, effectiveGateMode, pathsHint), timeoutMs);

                var result = task.Result;
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

                RecordGateRun(toolName, effectiveGateMode, result, pathsHint);
                ApplyToolResultToActivity(activity, result, sw.ElapsedMilliseconds);
                SendJson(context, 200, BuildGateEnvelope(result, effectiveGateMode, lifecycle));
            }
            catch (AggregateException ae)
            {
                sw.Stop();
                var inner = ae.InnerException;
                if (inner is TimeoutException)
                {
                    ApplyToolFailureToActivity(activity, "timeout", inner.Message, sw.ElapsedMilliseconds);
                    SendJson(context, 200, BuildTimeoutEnvelope(toolName, effectiveGateMode, timeoutMs));
                }
                else
                {
                    ApplyToolFailureToActivity(activity, "execution_error", inner?.Message, sw.ElapsedMilliseconds);
                    SendJson(context, 200, BuildFaultEnvelope(inner, effectiveGateMode));
                }
            }
            catch (System.Exception e)
            {
                sw.Stop();
                ApplyToolFailureToActivity(activity, "execution_error", e.Message, sw.ElapsedMilliseconds);
                SendJson(context, 200, BuildFaultEnvelope(e, effectiveGateMode));
            }
        }

        static void ApplyToolResultToActivity(BridgeActivityEvent activity, GateDispatchResult result, long durationMs)
        {
            if (activity == null) return;
            activity.DurationMs = durationMs;
            activity.Outcome = result.Outcome switch
            {
                GateOutcome.Passed => BridgeActivityOutcome.Success,
                GateOutcome.Warned => BridgeActivityOutcome.Success,
                GateOutcome.Skipped => BridgeActivityOutcome.Skipped,
                GateOutcome.Failed => result.Mutation != null && !result.Mutation.Success
                    ? BridgeActivityOutcome.Failed
                    : BridgeActivityOutcome.Failed,
                _ => BridgeActivityOutcome.Unknown
            };
            if (result.Mutation != null && !result.Mutation.Success && !string.IsNullOrEmpty(result.Mutation.ErrorCode))
            {
                activity.ErrorCode = result.Mutation.ErrorCode;
                activity.ErrorMessage = TruncateMessage(result.Mutation.ErrorMessage);
            }
        }

        static void ApplyToolFailureToActivity(BridgeActivityEvent activity, string code, string message, long durationMs)
        {
            if (activity == null) return;
            activity.DurationMs = durationMs;
            activity.Outcome = code == "timeout" ? BridgeActivityOutcome.Timeout : BridgeActivityOutcome.Failed;
            activity.ErrorCode = code;
            activity.ErrorMessage = TruncateMessage(message);
        }

        static string TruncateMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            const int max = 200;
            return message.Length <= max ? message : message.Substring(0, max) + "…";
        }

        static void RecordGateRun(string toolName, string effectiveMode, GateDispatchResult result, string[] pathsHint)
        {
            try
            {
                var record = new BridgeGateRunRecord
                {
                    ToolName = toolName,
                    RequestedMode = effectiveMode,
                    EffectiveMode = effectiveMode,
                    Outcome = result.Outcome,
                    GateRan = result.GateRan,
                    GateFailed = result.GateFailed,
                    NewErrors = result.Delta?.NewErrors ?? 0,
                    NewWarnings = result.Delta?.NewWarnings ?? 0,
                    ResolvedErrors = result.Delta?.ResolvedErrors ?? 0,
                    ResolvedWarnings = result.Delta?.ResolvedWarnings ?? 0,
                    CheckpointDurationMs = result.CheckpointDurationMs,
                    ValidationDurationMs = result.ValidationDurationMs,
                    TotalGateDurationMs = result.TotalGateDurationMs,
                    CategoriesRun = result.CategoriesRun,
                    AgentNextSteps = result.AgentNextSteps,
                    MutationError = result.Mutation?.ErrorMessage,
                    Timestamp = DateTime.Now
                };
                BridgeGateRunHistory.Record(record);
            }
            catch
            {
                // History capture is best-effort; never let it break the response.
            }

            // M14 T5.5 — on-disk audit log (opt-in). Mirrors the gate-run
            // record shape so an auditor can correlate the two. Best-effort:
            // a write failure is logged once and the record dropped, never
            // breaking the dispatch path.
            try
            {
                RecordAudit(toolName, effectiveMode, result, pathsHint);
            }
            catch
            {
                // ignored — audit logging is non-essential
            }
        }

        // M14 T5.5 — build + persist the audit record. Outcome vocabulary is
        // the GateOutcome enum lowercased, plus "denied" when the deny
        // heuristic refused the mutation (carried as the mutation error code).
        static void RecordAudit(string toolName, string effectiveMode, GateDispatchResult result, string[] pathsHint)
        {
            if (!BridgeAuditLog.Enabled) return;

            var mutationError = result.Mutation?.ErrorCode;
            var denied = mutationError == "denied_by_policy" || mutationError == "menu_blocked";
            var outcome = denied
                ? "denied"
                : result.Outcome.ToString().ToLowerInvariant();

            var record = new BridgeAuditRecord
            {
                Timestamp = DateTime.UtcNow,
                ProjectHash = ResolveAuditProjectHash(),
                Tool = toolName,
                GateMode = effectiveMode,
                PathsHint = pathsHint,
                Outcome = outcome,
                GateRan = result.GateRan,
                NewErrors = result.Delta?.NewErrors ?? 0,
                NewWarnings = result.Delta?.NewWarnings ?? 0,
                ResolvedErrors = result.Delta?.ResolvedErrors ?? 0,
                ResolvedWarnings = result.Delta?.ResolvedWarnings ?? 0,
                CheckpointId = result.CheckpointId,
                TotalGateDurationMs = result.TotalGateDurationMs,
                MutationErrorCode = mutationError,
                BypassedDenyList = effectiveMode == BridgeGateDefaultPolicy.Off && !denied,
                DeniedPattern = ExtractDeniedPattern(result.Mutation?.ErrorMessage)
            };
            BridgeAuditLog.Record(record);
        }

        static string ResolveAuditProjectHash()
        {
            var projectPath = BridgeSession.ProjectPath ?? GetProjectPathForPort();
            try { return InstancePortResolver.ProjectHash(projectPath); }
            catch { return "unknown"; }
        }

        // The deny heuristic embeds "Matched pattern: <pat>." in the error
        // message. Extract it so the audit record has a structured field for
        // grep / SIEM correlation. Returns null when the message isn't a deny
        // refusal.
        static string ExtractDeniedPattern(string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage)) return null;
            const string marker = "Matched pattern: ";
            var idx = errorMessage.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            var start = idx + marker.Length;
            var end = errorMessage.IndexOf('.', start);
            if (end < 0) end = errorMessage.Length;
            return errorMessage.Substring(start, end - start);
        }

        static GateDispatchResult DispatchWithGate(string toolName, string body, string gateMode, string[] pathsHint)
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
                if (!guard.Allowed)
                {
                    return new GateDispatchResult
                    {
                        Mutation = ToolDispatchResult.Fail("scene_dirty", guard.RefusalMessage),
                        GateRan = false,
                        Outcome = GateOutcome.Failed,
                        GateFailed = true,
                        DirtyScenePaths = guard.DirtyScenePaths,
                        AgentNextSteps = BuildSceneDirtyNextSteps(guard.DirtyScenePaths)
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

            return GatePolicy.Execute(mode, pathsHint, () => DispatchTool(toolName, body));
        }

        static string[] BuildSceneDirtyNextSteps(string[] dirtyPaths)
        {
            var list = new List<string>(3);
            if (dirtyPaths == null || dirtyPaths.Length == 0)
            {
                list.Add("Save or discard the active scene's unsaved changes, then retry.");
            }
            else
            {
                list.Add("Save or discard changes to the dirty scene(s) before retrying: "
                    + string.Join(", ", dirtyPaths) + ".");
            }
            list.Add("To save via the bridge: unity_open_mcp_execute_csharp with " +
                     "EditorSceneManager.SaveScene(EditorSceneManager.GetSceneManagerSetup()[0].path) " +
                     "(or MarkSceneDirty + SaveScene for an unsaved scene).");
            list.Add("To discard: EditorSceneManager.RestoreSavedSceneState(), or retry the " +
                     "original call with ignore_scene_dirty: true to proceed and accept the risk.");
            return list.ToArray();
        }

        static ToolDispatchResult DispatchTool(string toolName, string body)
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
                // M16 Plan 4 — typed Package Manager tools. list / search /
                // get_info hit UPM async requests; get_dependencies / check
                // read Packages/manifest.json directly. add / remove write
                // manifest.json (paths_hint scoped to it by the caller).
                "unity_open_mcp_package_list" => PackagesTools.List(body),
                "unity_open_mcp_package_search" => PackagesTools.Search(body),
                "unity_open_mcp_package_add" => PackagesTools.Add(body),
                "unity_open_mcp_package_remove" => PackagesTools.Remove(body),
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
                "unity_open_mcp_editor_get_tags" => EditorConsoleSelectionTools.EditorGetTags(body),
                "unity_open_mcp_editor_get_layers" => EditorConsoleSelectionTools.EditorGetLayers(body),
                "unity_open_mcp_editor_add_tag" => EditorConsoleSelectionTools.EditorAddTag(body),
                "unity_open_mcp_editor_add_layer" => EditorConsoleSelectionTools.EditorAddLayer(body),
                _ => BridgeToolRegistry.TryDispatch(toolName, body)
                     ?? ToolDispatchResult.Fail("tool_not_found", $"Unknown tool: {toolName}")
            };
        }

        // dry_run apply_fix short-circuit — runs the fix preview (Describe / fix
        // list / unknown-fix listing) without invoking the gate. The gate only
        // has something to validate when the project actually changes, and a
        // dry-run never changes the project.
        static void HandleDryRunApplyFix(
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
                RecordGateRun("unity_open_mcp_apply_fix", gateMode, gateResult, null);
                ApplyToolResultToActivity(activity, gateResult, sw.ElapsedMilliseconds);
                SendJson(context, 200, BuildGateEnvelope(gateResult, gateMode, LifecyclePolicy.EditorSettle));
            }
            catch (AggregateException ae)
            {
                sw.Stop();
                var inner = ae.InnerException;
                if (inner is TimeoutException)
                {
                    ApplyToolFailureToActivity(activity, "timeout", inner.Message, sw.ElapsedMilliseconds);
                    SendJson(context, 200, BuildTimeoutEnvelope("unity_open_mcp_apply_fix", gateMode, timeoutMs));
                }
                else
                {
                    ApplyToolFailureToActivity(activity, "execution_error", inner?.Message, sw.ElapsedMilliseconds);
                    SendJson(context, 200, BuildFaultEnvelope(inner, gateMode));
                }
            }
            catch (System.Exception e)
            {
                sw.Stop();
                ApplyToolFailureToActivity(activity, "execution_error", e.Message, sw.ElapsedMilliseconds);
                SendJson(context, 200, BuildFaultEnvelope(e, gateMode));
            }
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

        internal static int ExtractTimeoutMs(string body)
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
            // Precedence per architecture/gate-policy.md:
            //   1. Request body `gate` value
            //   2. Project default from `.unity-open-mcp/settings.json`
            //   3. Tool-level default (caller-provided)
            if (string.IsNullOrEmpty(body)) return BridgeGateDefaultPolicy.GetDefault();

            const string key = "\"gate\"";
            var idx = body.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return BridgeGateDefaultPolicy.GetDefault();

            var colonIdx = body.IndexOf(':', idx + key.Length);
            if (colonIdx < 0) return BridgeGateDefaultPolicy.GetDefault();

            var start = colonIdx + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;

            if (start >= body.Length || body[start] != '"') return BridgeGateDefaultPolicy.GetDefault();
            start++;

            var end = start;
            while (end < body.Length && body[end] != '"') end++;

            if (end == start) return BridgeGateDefaultPolicy.GetDefault();

            var value = body.Substring(start, end - start);
            return BridgeGateDefaultPolicy.IsValid(value) ? value : BridgeGateDefaultPolicy.GetDefault();
        }

        static string[] PathsFromIssueId(string issueId)
        {
            if (string.IsNullOrEmpty(issueId)) return null;
            var parts = issueId.Split('|');
            if (parts.Length < 3) return null;
            var assetPath = parts[2];
            if (string.IsNullOrEmpty(assetPath)) return null;
            return new[] { assetPath };
        }

        static string BuildGateEnvelope(GateDispatchResult result, string gateMode, LifecyclePolicy lifecycle)
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

            // M13 T4.1 — lifecycle hint + settle telemetry. Emitted on every
            // gate envelope so agents know whether the op was read-only, may
            // have settled, or survived a domain reload. settleMs is the time
            // the bridge blocked waiting for the editor to finish compiling.
            sb.Append(",\"lifecycle\":\"").Append(lifecycle.ToWireString()).Append("\"");
            sb.Append(",\"settleMs\":").Append(result.SettleMs);

            // M13 T4.2 — dirty-scene paths when the op was refused by the
            // active-scene guard. Omitted (null) when allowed.
            if (result.DirtyScenePaths != null && result.DirtyScenePaths.Length > 0)
            {
                sb.Append(",\"dirtyScenes\":[");
                for (int i = 0; i < result.DirtyScenePaths.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeStringContent(result.DirtyScenePaths[i])).Append('"');
                }
                sb.Append("]");
            }
            else
            {
                sb.Append(",\"dirtyScenes\":null");
            }

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
