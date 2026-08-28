using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public static class BridgeSession
    {
        public static string ProjectPath => _projectPath;
        public static string UnityVersion => _unityVersion;
        public static string BridgeVersion => "1.2.2";

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
        public const int WireContract = 1;
        public static bool IsCompiling => _isCompiling;
        public static bool IsPlaying => _isPlaying;
        public static string Mode => "live";
        public static bool Connected => _connected;

        private static string _projectPath;
        private static string _unityVersion;
        private static volatile bool _isCompiling;
        private static volatile bool _isPlaying;
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
        }

        private static void CacheStaticState()
        {
            _projectPath = System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            _unityVersion = Application.unityVersion;
        }

        private static void RefreshVolatileState()
        {
            _isCompiling = EditorApplication.isCompiling;
            _isPlaying = EditorApplication.isPlaying;
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

        public static bool IsInitialized => _initialized;
    }
}
