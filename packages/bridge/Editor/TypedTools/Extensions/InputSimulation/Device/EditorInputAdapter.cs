// The one adapter over Editor internals used for legacy input injection.
//
// Legacy Input Manager state (Input.touchCount / GetTouch, and through
// Input.simulateMouseWithTouches the legacy mouse API) is fed in the Editor by
// the internal `UnityEngine.Input.SimulateTouch(Touch)` — the entry point the
// Device Simulator's legacy backend calls. It needs no Simulator window, no
// Game view focus and no OS focus. Game view event injection (SendEvent /
// QueueGameViewInputEvent) was evaluated and does not reach legacy mouse or key
// state, so it is not used. Nothing here patches Unity or rewrites
// InputManager.asset; the entry point is resolved by reflection, behind a
// version table, and its signature is probed before use.
//
// Version table: an Editor outside every entry refuses with
// `unsupported_editor_version` (never guesses). 2022.3 is not listed until it
// is verified there.
//
// Held-touch tracking: a simulated touch stays pressed until released. The
// adapter remembers the held finger so exiting play mode or a domain reload
// releases it (Canceled) — a deliberate `inputsim_touch press` never outlives
// the play session.
using System;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    internal enum DeviceTouchPhase { Began, Moved, Stationary, Ended, Canceled }

    [InitializeOnLoad]
    internal static class EditorInputAdapter
    {
        internal sealed class VersionRule
        {
            public readonly int FromMajor, FromMinor, ToMajorExclusive;
            public readonly string VerifiedOn;
            public VersionRule(int fromMajor, int fromMinor, int toMajorExclusive, string verifiedOn)
            {
                FromMajor = fromMajor; FromMinor = fromMinor; ToMajorExclusive = toMajorExclusive; VerifiedOn = verifiedOn;
            }
        }

        // Unity 6 line: verified on 6000.3.21f1 (macOS). The signature probe below
        // still guards every call. Add 2022.3 here only after a walkthrough there.
        internal static readonly VersionRule[] Table =
        {
            new VersionRule(6000, 0, 7000, "6000.3.21f1 (macOS)"),
        };

        // Test seam: overrides Application.unityVersion.
        internal static string UnityVersionOverride;

        private static MethodInfo simulateTouch;
        private static bool resolved;
        private static string resolveError;

        private static bool held;
        private static int heldFinger;
        private static Vector2 heldPosition;

        static EditorInputAdapter()
        {
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingPlayMode) ReleaseHeld();
            };
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseHeld;
        }

        internal static string UnityVersion => UnityVersionOverride ?? Application.unityVersion;

        // "6000.3.21f1" → (6000, 3). False for anything unparsable.
        internal static bool TryParseVersion(string version, out int major, out int minor)
        {
            major = minor = 0;
            if (string.IsNullOrEmpty(version)) return false;
            var parts = version.Split('.');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)) return false;
            int digits = 0;
            while (digits < parts[1].Length && char.IsDigit(parts[1][digits])) digits++;
            return digits > 0 && int.TryParse(parts[1].Substring(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out minor);
        }

        internal static bool IsVersionSupported(string version, out string detail)
        {
            if (!TryParseVersion(version, out var major, out var minor))
            {
                detail = "Unrecognised Unity version '" + version + "'.";
                return false;
            }
            foreach (var rule in Table)
            {
                bool atLeast = major > rule.FromMajor || (major == rule.FromMajor && minor >= rule.FromMinor);
                if (atLeast && major < rule.ToMajorExclusive)
                {
                    detail = "Unity " + version + " is in the verified range (verified on " + rule.VerifiedOn + ").";
                    return true;
                }
            }
            detail = "Unity " + version + " is not in the legacy input injection version table (supported: Unity 6 line, verified on " +
                     Table[0].VerifiedOn + "). 2022.3 and other lines stay disabled until verified.";
            return false;
        }

        // Resolve + signature-probe the internal entry point. Cached per domain.
        internal static bool TryResolve(out string error)
        {
            if (!resolved)
            {
                resolved = true;
                try
                {
                    var method = typeof(Input).GetMethod("SimulateTouch",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                        null, new[] { typeof(Touch) }, null);
                    if (method == null) resolveError = "UnityEngine.Input.SimulateTouch(Touch) was not found in this Editor.";
                    else if (method.ReturnType != typeof(void)) resolveError = "UnityEngine.Input.SimulateTouch has an unexpected signature.";
                    else simulateTouch = method;
                }
                catch (Exception ex) { resolveError = "Resolving UnityEngine.Input.SimulateTouch failed: " + ex.Message; }
            }
            error = resolveError;
            return simulateTouch != null;
        }

        // Both gates in one call: version table + entry point.
        internal static bool IsAvailable(out string code, out string message)
        {
            if (!IsVersionSupported(UnityVersion, out var detail))
            {
                code = "unsupported_editor_version";
                message = detail;
                return false;
            }
            if (!TryResolve(out var error))
            {
                code = "legacy_entry_point_missing";
                message = error;
                return false;
            }
            code = null;
            message = null;
            return true;
        }

        internal static TouchPhase ToLegacy(DeviceTouchPhase phase)
        {
            switch (phase)
            {
                case DeviceTouchPhase.Began: return TouchPhase.Began;
                case DeviceTouchPhase.Moved: return TouchPhase.Moved;
                case DeviceTouchPhase.Stationary: return TouchPhase.Stationary;
                case DeviceTouchPhase.Ended: return TouchPhase.Ended;
                default: return TouchPhase.Canceled;
            }
        }

        // Queue one legacy touch sample in Screen pixels (bottom-left origin).
        // Observed by the next legacy input update (same frame when called from
        // the start of EarlyUpdate, next frame from Editor code).
        internal static void SimulateTouch(int fingerId, Vector2 position, DeviceTouchPhase phase)
        {
            if (!TryResolve(out var error)) throw new InvalidOperationException(error);
            var touch = new Touch
            {
                fingerId = fingerId,
                position = position,
                rawPosition = position,
                phase = ToLegacy(phase),
                tapCount = 1,
                pressure = 1f,
                maximumPossiblePressure = 1f,
                type = TouchType.Direct,
            };
            simulateTouch.Invoke(null, new object[] { touch });
            if (phase == DeviceTouchPhase.Ended || phase == DeviceTouchPhase.Canceled)
            {
                if (held && heldFinger == fingerId) held = false;
            }
            else
            {
                held = true;
                heldFinger = fingerId;
                heldPosition = position;
            }
        }

        internal static bool LegacyTouchHeld => held;
        internal static int HeldFinger => heldFinger;
        internal static Vector2 HeldPosition => heldPosition;

        // Release a held legacy touch (Canceled). Safe to call any time.
        internal static void ReleaseHeld()
        {
            if (!held) return;
            try { SimulateTouch(heldFinger, heldPosition, DeviceTouchPhase.Canceled); }
            catch (Exception) { /* entry point unavailable — nothing can be held */ }
            held = false;
        }
    }
}
