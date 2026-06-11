using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    public static class BridgeSession
    {
        public static string ProjectPath => System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        public static string UnityVersion => Application.unityVersion;
        public static string BridgeVersion => "0.1.0";
        public static bool IsCompiling => EditorApplication.isCompiling;
        public static bool IsPlaying => EditorApplication.isPlaying;
        public static string Mode => "live";
        public static bool Connected => _connected;

        static bool _connected;

        public static void SetConnected(bool value)
        {
            _connected = value;
        }
    }
}
