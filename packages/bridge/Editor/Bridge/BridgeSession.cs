using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public static class BridgeSession
    {
        public static string ProjectPath => _projectPath;
        public static string UnityVersion => _unityVersion;
        public static string BridgeVersion => "0.3.1";
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

        public static bool IsInitialized => _initialized;
    }
}
