using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public static class BridgeSession
    {
        public static string ProjectPath => _projectPath;
        public static string UnityVersion => _unityVersion;
        public static string BridgeVersion => "1.6.0";

        // specs/feedback.md 2026-08-24 — a WIRE-CONTRACT revision that moves
        // independently of the package semver.
        //
        // The motivating incident: a fixed-and-shipped blocker (registry tools
        // rejecting the transport envelope keys, so editor_status was uncallable
        // with default args) recurred in the field against an installed bridge
        // that still reported bridgeVersion "1.0.0" — the same string the fixed
        // source reports. From the call site "your installed bridge predates the
        // fix" and "this regressed" were indistinguishable, so the agent could
        // not tell whether to reinstall or to file a regression.
        //
        // This integer is the disambiguator: bump it in the SAME change as any
        // observable change to the request/response contract a client can
        // depend on — accepted parameter keys, error codes, envelope fields,
        // declared schema ceilings. It is reported by /ping and surfaced by
        // bridge_status next to bridgeVersion (see
        // mcp-server/src/constants.ts EXPECTED_BRIDGE_WIRE_CONTRACT, which is
        // the revision the paired server was built against). An older bridge
        // omits the field entirely, which reads as "stale" — that absence is
        // itself the answer to the question above.
        //
        // Revisions:
        //   1 — transport-envelope key exemption in registry dispatch
        //       (gate / timeout_ms / ignore_scene_dirty / confirm_bypass),
        //       "(none)" allow-list rendering for zero-parameter tools,
        //       batch_execute pre-flight refusal of server-polled steps
        //       (batch_step_requires_server_poll), run_tests in-flight run_id
        //       refusal (run_id_in_flight).
        //   2 — source-aware /compile-state (also as an opt-in tool-response
        //       header) and request-correlated atomic JSON replies.
        //   3 — input backends: inputsim_touch `backend`, inputsim_pointer
        //       `routing`/`backend`, inputsim_gesture + POST /playmode-operations
        //       (shared play-mode scheduler), input_backend_unavailable /
        //       legacy_multitouch_unsupported / unsupported_editor_version.
        //   4 — UI snapshot and label targeting: ui_snapshot; target_text /
        //       under / index / text_match on inputsim_pointer and
        //       inputsim_touch (and inputsim_gesture keyframes); `textTarget(s)`
        //       in their results; ambiguous_target `matches`, target_not_found
        //       `hidden_matches`, conflicting_targets / index_out_of_range /
        //       under_not_found / ui_text_unavailable.
        //   5 — Game view and Device Simulator: game_view (get / set_resolution /
        //       set_device / list_devices, play-mode settle on the scheduler,
        //       not_settled / device_not_found / size_not_found /
        //       editor_internals_unavailable); composed captures at native size
        //       by default, letterboxed (never stretched) to a different
        //       width/height with scaled_from + content_rect, `size: native`,
        //       `overlay: safe_area`, `capturedAt` on game/composed captures;
        //       batch refusal of game_view set_*.
        //   6 — Waits and capture series: playtest_wait (log / ui / object /
        //       project_command / frames / ms conditions, all / any, time_scale,
        //       fail_on; timed_out / exception_thrown / condition_error as
        //       failed scheduler states with result.error_code),
        //       capture_series (at_ms / every_ms / at_frames, start_on, region,
        //       scale, metric, contact_sheet), inputsim_gesture capture_at_ms;
        //       render_target_unavailable; batch refusal of both new tools;
        //       registry arguments read from the depth-1 projection when a
        //       condition / start_on payload is present (playtest_wait's
        //       timeout_ms is its own parameter; its start dispatch keeps the
        //       default budget).
        //   7 — Player-state sandbox: player_state (snapshot / diff / restore /
        //       list / delete); request-level mutability (only restore with
        //       confirm:true is a gated mutation); snapshot_exists /
        //       snapshot_not_found / snapshot_unreadable / snapshot_corrupt /
        //       snapshot_project_mismatch / edit_mode_required /
        //       persistent_data_too_large / prefs_store_unreadable /
        //       restore_incomplete / plan_required / plan_changed (confirm
        //       applies only the dry run's plan) / persistent_path_link /
        //       prefs_store_unverified / editor_internals_unavailable (EditorPrefs
        //       without a flush); a registry tool may return a failed result
        //       (mutation.success:false) and a failed mutation's own next steps
        //       reach agentNextSteps.
        //   8 — Reload/transition handshake: /ping `transition`; the instance
        //       lock's `compile` block (generation, running, failed,
        //       errorCount, pending, requestedAt); /compile-state `pending`;
        //       a mutation that meets a compile or play-mode change answers
        //       HTTP 503 editor_reloading (dispatched:false) instead of
        //       running; a tool call cut off by a domain reload answers a
        //       well-formed editor_reloading body (503 not run / 200
        //       dispatched:true) instead of an empty 200; the gate envelope's
        //       `compile` block; execute_menu Assets/Refresh is a gated
        //       mutation with assets_refresh's lifecycle and default scope.
        //   9 — A call that requests a domain reload (RequestScriptReload) is
        //       answered before the reload: the envelope's reloadRequested /
        //       reloadTiming ("after_response" | "after_play_mode"); until the
        //       reload starts the lock state and /ping transition read
        //       "reloading" and mutations answer 503 editor_reloading.
        //  10 — The gate delta reports a copy / move / save-as destination's
        //       issues its source already had as inherited, not new:
        //       inheritedErrors / inheritedWarnings / inheritedIssues
        //       (+Truncated, ByRule) / inheritedFrom.
        public const int WireContract = 10;
        public static bool IsCompiling => _isCompiling;
        public static bool IsPlaying => _isPlaying;

        // The Editor transition in progress, as /ping reports it: "compiling"
        // (a compile is running or due, or the domain is about to reload),
        // "reloading" (a call requested a domain reload that has not started),
        // "entering_playmode" (play requested, play mode not reached yet),
        // "exiting_playmode", or null when settled. Thread-safe: built from the
        // volatile flags the main thread refreshes every update and on every
        // play-mode change.
        public static string Transition
        {
            get
            {
                if (_isCompiling) return "compiling";
                // A tool call requested a domain reload that has not started
                // yet (held until its response was written, then due).
                if (BridgeReloadRequest.Due) return "reloading";
                var willPlay = _willChangePlaymode;
                var playing = _isPlaying;
                if (willPlay && !playing) return "entering_playmode";
                if (!willPlay && playing) return "exiting_playmode";
                return null;
            }
        }
        public static string Mode => "live";
        public static bool Connected => _connected;

        private static string _projectPath;
        private static string _unityVersion;
        private static volatile bool _isCompiling;
        private static volatile bool _isPlaying;
        private static volatile bool _willChangePlaymode;
        private static volatile bool _connected;
        private static volatile bool _initialized;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            CacheStaticState();
            _initialized = true;

            EditorApplication.update -= RefreshVolatileState;
            EditorApplication.update += RefreshVolatileState;

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        // A play-mode change is visible to /ping at once, not one update later.
        private static void OnPlayModeStateChanged(PlayModeStateChange change) => RefreshVolatileState();

        private static void CacheStaticState()
        {
            _projectPath = System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            _unityVersion = Application.unityVersion;
        }

        // Main thread. Also called by a tool that just changed play mode, so
        // the transition shows before the next update tick.
        internal static void RefreshVolatileState()
        {
            _isCompiling = EditorApplication.isCompiling;
            _isPlaying = EditorApplication.isPlaying;
            _willChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private static void OnBeforeAssemblyReload()
        {
            _isCompiling = true;
            _connected = false;
        }

        private static void OnAfterAssemblyReload()
        {
            CacheStaticState();
            _initialized = true;
        }

        public static void SetConnected(bool value)
        {
            _connected = value;
        }

        // feedback-fable-04-08 §9 — test seam so the compilePending gate
        // advisory can be exercised without driving a real compile. The flag
        // is normally refreshed by the main-thread update tick; this setter
        // lets a unit test pin it and assert the envelope surfaces the
        // advisory. Production never calls this (the update tick owns the flag).
        internal static void SetCompilingForTest(bool value)
        {
            _isCompiling = value;
        }

        // feedback.md issue 5 — same pattern as SetCompilingForTest: lets a unit
        // test pin the play-mode flag so the gate's play-mode short-circuit can
        // be exercised without entering actual play mode. Production never calls
        // this (the update tick owns the flag).
        internal static void SetPlayingForTest(bool value)
        {
            _isPlaying = value;
        }

        // Same pattern: pin the "play mode requested" flag for a transition test.
        internal static void SetWillChangePlaymodeForTest(bool value)
        {
            _willChangePlaymode = value;
        }

        public static bool IsInitialized => _initialized;
    }
}
