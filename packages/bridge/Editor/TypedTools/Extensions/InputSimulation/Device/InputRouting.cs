// Backend routing for device-level input simulation.
//
// One decision per call, reported as `backend` (an array). Backends:
//   legacy_touch  — legacy Input Manager touches (+ emulated legacy mouse), via
//                   EditorInputAdapter. Single finger (id 0) in v1.
//   input_system  — Input System Touchscreen state events (needs a Touchscreen
//                   device and, under the default editor input behaviour, Game
//                   view focus inside Unity).
//   ugui_dispatch — ExecuteEvents dispatch (inputsim_pointer routing:dispatch);
//                   not chosen here.
//
// `auto`: inject into every enabled backend that is reachable. Legacy touches do
// not reach Input System devices and vice versa (verified), so with
// activeInputHandler Both a project polling either API sees the finger. A
// backend that is enabled but unreachable is listed in `skipped` with a reason;
// when nothing is reachable the call refuses with a stable code and the detected
// configuration — never a silent ok. The Device Simulator is never opened or
// focused here.
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    // Registered by the Input System sub-assembly when com.unity.inputsystem is present.
    internal interface IInputSystemTouchBackend
    {
        string PackageVersion { get; }
        bool HasTouchscreen { get; }
        // True when the Input System routes pointer input to the player only while
        // the Game view has focus (the default editor input behaviour).
        bool RespectsGameViewFocus { get; }
        void Queue(int finger, DeviceTouchPhase phase, Vector2 position);
        // Process queued events now (synchronous paths).
        void UpdateNow();
        // Advance N frames with an input update before each gameplay Update (0 = UpdateNow).
        void StepFrames(int frames);
    }

    // Registered by the uGUI sub-assembly when com.unity.ugui is present.
    internal interface IUiHitProbe
    {
        // Hierarchy path of the top EventSystem raycast hit at a screen point, or null.
        string TopHitPath(Vector2 screenPoint);
        // Type name of the active EventSystem's input module, or null.
        string InputModuleName { get; }
    }

    internal static class InputBackends
    {
        internal static IInputSystemTouchBackend InputSystem;
        internal static IUiHitProbe Ui;
    }

    // Detected input configuration (pure data so routing is unit-testable).
    internal sealed class InputEnvironment
    {
        public int ActiveInputHandler;          // 0 legacy, 1 Input System, 2 Both (from the running Editor's defines)
        public bool LegacyEnabled;
        public bool InputSystemEnabled;
        public bool InputSystemPackage;         // the Input System backend is compiled in
        public string InputSystemVersion;
        public bool Touchscreen;
        public bool RespectsGameViewFocus;
        public string UnityVersion;
        public bool EditorVersionSupported;
        public string EditorVersionDetail;
        public bool LegacyEntryPoint;
        public string LegacyEntryPointError;
        public bool SimulateMouseWithTouches = true;
        public bool DeviceSimulatorOpen;
        public bool GameViewOpen;

        public static InputEnvironment Detect()
        {
            var env = new InputEnvironment();
#if ENABLE_LEGACY_INPUT_MANAGER
            env.LegacyEnabled = true;
#endif
#if ENABLE_INPUT_SYSTEM
            env.InputSystemEnabled = true;
#endif
            env.ActiveInputHandler = env.LegacyEnabled && env.InputSystemEnabled ? 2 : env.InputSystemEnabled ? 1 : 0;
            var backend = InputBackends.InputSystem;
            env.InputSystemPackage = backend != null;
            if (backend != null)
            {
                env.InputSystemVersion = backend.PackageVersion;
                try { env.Touchscreen = backend.HasTouchscreen; } catch { env.Touchscreen = false; }
                try { env.RespectsGameViewFocus = backend.RespectsGameViewFocus; } catch { env.RespectsGameViewFocus = true; }
            }
            env.UnityVersion = EditorInputAdapter.UnityVersion;
            env.EditorVersionSupported = EditorInputAdapter.IsVersionSupported(env.UnityVersion, out env.EditorVersionDetail);
            env.LegacyEntryPoint = EditorInputAdapter.TryResolve(out env.LegacyEntryPointError);
#if ENABLE_LEGACY_INPUT_MANAGER
            try { env.SimulateMouseWithTouches = Input.simulateMouseWithTouches; } catch { env.SimulateMouseWithTouches = true; }
#endif
            env.DeviceSimulatorOpen = GameViewWindows.IsDeviceSimulatorOpen();
            env.GameViewOpen = GameViewWindows.FindGameView() != null;
            return env;
        }

        public string ToJson()
        {
            var sb = new StringBuilder(384);
            sb.Append("{\"activeInputHandler\":").Append(ActiveInputHandler);
            sb.Append(",\"legacyInputManager\":").Append(LegacyEnabled ? "true" : "false");
            sb.Append(",\"inputSystem\":").Append(InputSystemEnabled ? "true" : "false");
            sb.Append(",\"inputSystemPackage\":").Append(InputSystemPackage ? BridgeJson.EscapeString(InputSystemVersion ?? "present") : "null");
            sb.Append(",\"touchscreen\":").Append(Touchscreen ? "true" : "false");
            sb.Append(",\"unityVersion\":").Append(BridgeJson.EscapeString(UnityVersion ?? ""));
            sb.Append(",\"editorVersionSupported\":").Append(EditorVersionSupported ? "true" : "false");
            sb.Append(",\"legacyEntryPoint\":").Append(LegacyEntryPoint ? "true" : "false");
            sb.Append(",\"simulateMouseWithTouches\":").Append(SimulateMouseWithTouches ? "true" : "false");
            sb.Append(",\"deviceSimulatorOpen\":").Append(DeviceSimulatorOpen ? "true" : "false");
            sb.Append(",\"gameViewOpen\":").Append(GameViewOpen ? "true" : "false");
            sb.Append(",\"focusRequirement\":").Append(BridgeJson.EscapeString(
                "legacy_touch: none (no Game view or OS focus needed); input_system: Game view focus inside Unity" +
                (RespectsGameViewFocus ? " (taken with EditorWindow.Focus)" : " (not required by the current editor input behaviour)")));
            sb.Append('}');
            return sb.ToString();
        }
    }

    internal sealed class RouteDecision
    {
        public readonly List<string> Backends = new List<string>();
        public readonly List<KeyValuePair<string, string>> Skipped = new List<KeyValuePair<string, string>>();
        public string ErrorCode;
        public string ErrorMessage;
        public readonly List<string> NextSteps = new List<string>();
        public InputEnvironment Environment;

        public bool Ok => ErrorCode == null;
        public bool Uses(string backend) => Backends.Contains(backend);

        public string BackendJson()
        {
            var sb = new StringBuilder(64).Append('[');
            for (int i = 0; i < Backends.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(Backends[i]));
            }
            return sb.Append(']').ToString();
        }

        public string SkippedJson()
        {
            var sb = new StringBuilder(64).Append('[');
            for (int i = 0; i < Skipped.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"backend\":").Append(BridgeJson.EscapeString(Skipped[i].Key));
                sb.Append(",\"reason\":").Append(BridgeJson.EscapeString(Skipped[i].Value)).Append('}');
            }
            return sb.Append(']').ToString();
        }

        // Complete error object for a refused route.
        public string ErrorJson()
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"error\":{\"code\":").Append(BridgeJson.EscapeString(ErrorCode));
            sb.Append(",\"message\":").Append(BridgeJson.EscapeString(ErrorMessage ?? ""));
            if (Environment != null) sb.Append(",\"detected\":").Append(Environment.ToJson());
            sb.Append(",\"skipped\":").Append(SkippedJson());
            sb.Append("},\"agentNextSteps\":[");
            for (int i = 0; i < NextSteps.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(NextSteps[i]));
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }

    internal static class InputRouting
    {
        internal const string Auto = "auto";
        internal const string LegacyTouch = "legacy_touch";
        internal const string InputSystem = "input_system";
        internal const string UguiDispatch = "ugui_dispatch";

        // `fingerIds` = the distinct finger ids the request uses.
        internal static RouteDecision Decide(InputEnvironment env, string requested, ICollection<int> fingerIds)
        {
            var d = new RouteDecision { Environment = env };
            requested = string.IsNullOrEmpty(requested) ? Auto : requested;
            if (requested != Auto && requested != LegacyTouch && requested != InputSystem)
            {
                d.ErrorCode = "invalid_backend";
                d.ErrorMessage = "Unknown backend '" + requested + "'. Valid: auto, legacy_touch, input_system.";
                return d;
            }
            bool explicitLegacy = requested == LegacyTouch, explicitIs = requested == InputSystem;
            string legacyFailCode = null;

            if (requested == Auto || explicitLegacy)
            {
                if (!env.LegacyEnabled)
                    Reject(d, explicitLegacy, LegacyTouch, "legacy_input_disabled", "input_backend_unavailable",
                        "The legacy Input Manager is disabled (activeInputHandler " + env.ActiveInputHandler + ").");
                else if (!env.EditorVersionSupported)
                {
                    legacyFailCode = "unsupported_editor_version";
                    Reject(d, explicitLegacy, LegacyTouch, "unsupported_editor_version", "unsupported_editor_version", env.EditorVersionDetail);
                }
                else if (!env.LegacyEntryPoint)
                    Reject(d, explicitLegacy, LegacyTouch, "legacy_entry_point_missing", "input_backend_unavailable", env.LegacyEntryPointError);
                else
                    d.Backends.Add(LegacyTouch);
                if (d.ErrorCode != null) return Finish(d, env);
            }

            if (requested == Auto || explicitIs)
            {
                if (!env.InputSystemEnabled)
                    Reject(d, explicitIs, InputSystem, "input_system_disabled", "input_backend_unavailable",
                        "The Input System is disabled (activeInputHandler " + env.ActiveInputHandler + ").");
                else if (!env.InputSystemPackage)
                    Reject(d, explicitIs, InputSystem, "input_system_package_missing", "input_backend_unavailable",
                        "com.unity.inputsystem is not installed, so no Input System backend is compiled in.");
                else if (!env.Touchscreen)
                    Reject(d, explicitIs, InputSystem, "no_touchscreen", "no_device",
                        "Touchscreen.current is null: the Editor has no Input System touch device.");
                else
                    d.Backends.Add(InputSystem);
                if (d.ErrorCode != null) return Finish(d, env);
            }

            if (d.Uses(LegacyTouch) && fingerIds != null)
            {
                foreach (var id in fingerIds)
                {
                    if (id == 0 && fingerIds.Count == 1) continue;
                    d.ErrorCode = "legacy_multitouch_unsupported";
                    d.ErrorMessage = "The legacy_touch backend is single-finger in this version (finger id 0 only); the request uses finger id(s) " +
                                     string.Join(", ", fingerIds) + ".";
                    d.NextSteps.Add("Use one finger with id 0, or pass backend: \"input_system\" when a Touchscreen device exists.");
                    return Finish(d, env);
                }
            }

            if (d.Backends.Count == 0)
            {
                d.ErrorCode = legacyFailCode ?? "input_backend_unavailable";
                d.ErrorMessage = "No input backend can reach this project's input. " + Describe(d.Skipped);
            }
            return Finish(d, env);
        }

        private static void Reject(RouteDecision d, bool explicitRequest, string backend, string reason, string explicitCode, string message)
        {
            d.Skipped.Add(new KeyValuePair<string, string>(backend, reason));
            if (!explicitRequest) return;
            d.ErrorCode = explicitCode;
            d.ErrorMessage = message;
        }

        private static string Describe(List<KeyValuePair<string, string>> skipped)
        {
            if (skipped.Count == 0) return "";
            var sb = new StringBuilder("Skipped: ");
            for (int i = 0; i < skipped.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(skipped[i].Key).Append(" (").Append(skipped[i].Value).Append(')');
            }
            return sb.Append('.').ToString();
        }

        private static RouteDecision Finish(RouteDecision d, InputEnvironment env)
        {
            if (d.ErrorCode == null || d.ErrorCode == "legacy_multitouch_unsupported" || d.ErrorCode == "invalid_backend") return d;
            foreach (var skip in d.Skipped)
            {
                switch (skip.Value)
                {
                    case "no_touchscreen":
                        d.NextSteps.Add("Input System touch needs a Touchscreen device: open the Device Simulator explicitly (game_view set_device) or add one with execute_csharp (UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Touchscreen>()).");
                        break;
                    case "unsupported_editor_version":
                        d.NextSteps.Add("Legacy input injection is enabled only on verified Editor versions; on this Editor drive the game through invoke_method / project commands, or inputsim_pointer (routing: dispatch) for uGUI.");
                        break;
                    case "legacy_entry_point_missing":
                        d.NextSteps.Add("This Editor does not expose the legacy touch simulation entry point; use inputsim_pointer (routing: dispatch) for uGUI or invoke_method for gameplay.");
                        break;
                    case "input_system_package_missing":
                    case "input_system_disabled":
                        if (env.LegacyEnabled) d.NextSteps.Add("Use backend: \"legacy_touch\" (or auto) — the legacy Input Manager is enabled.");
                        break;
                    case "legacy_input_disabled":
                        if (env.InputSystemEnabled) d.NextSteps.Add("Use backend: \"input_system\" (or auto) — this project reads the Input System.");
                        break;
                }
            }
            if (d.NextSteps.Count == 0)
                d.NextSteps.Add("Drive the behaviour through invoke_method / a project command, or inputsim_pointer (routing: dispatch) for uGUI.");
            return d;
        }
    }

    // Game view / Device Simulator lookups (internal window types, by name).
    internal static class GameViewWindows
    {
        private static System.Type gameViewType;

        internal static System.Type GameViewType =>
            gameViewType ?? (gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));

        internal static EditorWindow FindGameView()
        {
            var type = GameViewType;
            if (type == null) return null;
            var all = Resources.FindObjectsOfTypeAll(type);
            return all.Length > 0 ? all[0] as EditorWindow : null;
        }

        internal static bool IsDeviceSimulatorOpen()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (window != null && window.GetType().Name == "SimulatorWindow") return true;
            return false;
        }

        // The Input System routes pointer input to the player only while the Game
        // view is focused (default editor behaviour). EditorWindow.Focus() inside
        // Unity is allowed; the Unity app is never raised to the OS foreground.
        // Returns true when focus was moved.
        internal static bool EnsureGameViewFocusForInputSystem(RouteDecision route)
        {
            if (route == null || !route.Uses(InputRouting.InputSystem)) return false;
            var backend = InputBackends.InputSystem;
            if (backend == null || !backend.RespectsGameViewFocus) return false;
            if (Application.isFocused) return false;
            var gameView = FindGameView();
            if (gameView == null) return false;
            gameView.Focus();
            return true;
        }

        // Main Game view size in pixels — Screen.width/height read from Editor
        // code report the current GUIView, not the game.
        internal static Vector2 MainGameViewSize()
        {
            var size = Handles.GetMainGameViewSize();
            return size.x > 0 && size.y > 0 ? size : new Vector2(Screen.width, Screen.height);
        }
    }
}
