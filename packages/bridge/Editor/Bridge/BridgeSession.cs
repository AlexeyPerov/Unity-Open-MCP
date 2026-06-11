using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    public static class BridgeSession
    {
        public static string ProjectPath => _projectPath;
        public static string UnityVersion => _unityVersion;
        public static string BridgeVersion => "0.1.0";
        public static bool IsCompiling => _isCompiling;
        public static bool IsPlaying => _isPlaying;
        public static string Mode => "live";
        public static bool Connected => _connected;

        static string _projectPath;
        static string _unityVersion;
        static volatile bool _isCompiling;
        static volatile bool _isPlaying;
        static volatile bool _connected;
        static volatile bool _initialized;

        [InitializeOnLoadMethod]
        static void Initialize()
        {
            CacheStaticState();
            _initialized = true;

            EditorApplication.update -= RefreshVolatileState;
            EditorApplication.update += RefreshVolatileState;

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

        static void CacheStaticState()
        {
            _projectPath = System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            _unityVersion = Application.unityVersion;
        }

        static void RefreshVolatileState()
        {
            _isCompiling = EditorApplication.isCompiling;
            _isPlaying = EditorApplication.isPlaying;
        }

        static void OnBeforeAssemblyReload()
        {
            _isCompiling = true;
            _connected = false;
        }

        static void OnAfterAssemblyReload()
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
