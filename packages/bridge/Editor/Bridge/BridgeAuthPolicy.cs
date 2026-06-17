// M14 — Bridge auth policy: whether the HTTP layer enforces the per-session
// bearer token from the instance lock.
//
//   - "none"     (default): the bridge accepts any request. The token is still
//                minted into the lock file so the MCP client always sends one;
//                flipping to "required" needs no restart.
//   - "required": every request must carry `Authorization: Bearer <token>`
//                matching the live instance's token. Used for hardened setups
//                (e.g. shared dev machines, anything other than strict
//                127.0.0.1-only) — the bridge still binds loopback only.
//
// Mirrors the BridgeGateDefaultPolicy shape: the project default lives in
// .unity-open-mcp/settings.json under `authMode`, validated against ValidModes,
// with a Changed event for reactive UI.
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
