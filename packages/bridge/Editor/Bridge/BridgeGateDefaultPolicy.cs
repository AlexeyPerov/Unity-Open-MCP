using System;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public static class BridgeGateDefaultPolicy
    {
        public const string Enforce = "enforce";
        public const string Warn = "warn";
        public const string Off = "off";
        public const string Default = Enforce;

        public static readonly string[] ValidModes = { Enforce, Warn, Off };

        public static event Action Changed;

        public static string GetDefault()
        {
            var data = BridgeProjectSettings.Data;
            var mode = data != null ? data.defaultGateMode : null;
            return IsValid(mode) ? mode : Default;
        }

        public static void SetDefault(string mode)
        {
            if (!IsValid(mode))
            {
                Debug.LogWarning($"[BridgeGateDefaultPolicy] Ignoring invalid gate mode '{mode}'. " +
                                 $"Valid values: {string.Join(", ", ValidModes)}.");
                return;
            }

            if (GetDefault() == mode) return;

            var data = BridgeProjectSettings.Data;
            data.defaultGateMode = mode;
            BridgeProjectSettings.Save();
            try { Changed?.Invoke(); } catch { }
        }

        public static bool IsValid(string mode)
        {
            return mode == Enforce || mode == Warn || mode == Off;
        }

        public static string DescribePrecedence()
        {
            return "Precedence: request body `gate` > project default (.unity-open-mcp/settings.json) > tool-level default.";
        }
    }
}
