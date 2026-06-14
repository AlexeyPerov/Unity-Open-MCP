// Project default gate mode for the bridge runtime (M4.5-7).
//
// Per architecture/gate-policy.md precedence contract:
//   1. Request-level `gate` value in the incoming tool body (if present)
//   2. Project default from `.unity-open-mcp/settings.json` (this class)
//   3. Tool-level default (`[BridgeTool(..., Gate=...)]` attribute) or "enforce"
//
// The value is the source-of-truth project default; per-request `gate` and tool-attribute
// defaults always override at the dispatch site. v1 only stores a single project default
// (Q9 — no per-tool defaults in v1).
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
