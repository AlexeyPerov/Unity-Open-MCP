using System;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public static class BridgeAuthPolicy
    {
        public const string None = "none";
        public const string Required = "required";
        public const string Default = None;

        public static readonly string[] ValidModes = { None, Required };

        public static event Action Changed;

        public static string GetDefault()
        {
            return BridgeProjectSettings.AuthMode;
        }

        public static void SetDefault(string mode)
        {
            if (!IsValid(mode))
            {
                Debug.LogWarning($"[BridgeAuthPolicy] Ignoring invalid auth mode '{mode}'. " +
                                 $"Valid values: {string.Join(", ", ValidModes)}.");
                return;
            }

            if (GetDefault() == mode) return;

            BridgeProjectSettings.SetAuthMode(mode);
            try { Changed?.Invoke(); } catch { }
        }

        public static bool IsValid(string mode)
        {
            return mode == None || mode == Required;
        }
    }
}
