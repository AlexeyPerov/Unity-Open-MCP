using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public static class BridgeSession
    {
        public static string ProjectPath => _projectPath;
        public static string UnityVersion => _unityVersion;
        public static string BridgeVersion => "0.9.0";
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
