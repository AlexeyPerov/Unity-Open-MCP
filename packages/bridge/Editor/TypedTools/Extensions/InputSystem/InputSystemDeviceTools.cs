// Input simulation embedded domain — Input System device events.
//
// Compiled with com.unity.inputsystem (this folder's sub-asmdef). Queues keyboard
// device events (inputsim_key) and provides the Input System touch backend that
// the bridge's backend-aware inputsim_touch / inputsim_gesture /
// inputsim_pointer(raycast_real) route to (InputRouting in the bridge root). The
// touch tool itself lives in the root assembly so legacy-only projects get it.
//
// State is queued via the public InputSystem.QueueStateEvent API:
//   InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(Key.Space));
//   UpdateInput();   // process queued events this frame
// No unsafe pointers required.
//
// Keys are Input System only: legacy Input.GetKey* has no supported Editor
// injection path (Game view key events do not reach it), so a legacy-only
// project refuses with input_backend_unavailable and a Both project is told
// that legacy key polling is not reached.
//
// Play-mode only (refuses with play_mode_required otherwise). Gate-free
// (IsMutating=false — input writes no assets).
//
// Framed input runs a temporary manual input update before gameplay Update.
// Immediate input processes callbacks without advancing gameplay. duration_ms
// is metadata, not a real-time wait or a Hold-interaction guarantee.
//
// Parameter names are snake_case to match the JSON-schema keys (see
// PointerTools.cs / InputSystemTools.cs for the same convention).
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.ObjectRefs;
// TouchPhase is ambiguous between UnityEngine.TouchPhase (engine) and
// UnityEngine.InputSystem.TouchPhase (this domain). The TouchState struct's
// `phase` field is the Input System one, so alias it here to keep the bare
// `TouchPhase.Began/Moved/Ended` call sites unqualified and unambiguous.
using TouchPhase = UnityEngine.InputSystem.TouchPhase;
// The Input System's Key enum collides with this class's tool method `Key(...)`
// (member lookup shadows the imported type, so `List<Key>` / `Key.LeftShift`
// resolve to the method). Alias the enum to `InputKey` so the type usages are
// unambiguous while the method keeps its name.
using InputKey = UnityEngine.InputSystem.Key;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    [BridgeToolType]
    public static class InputSystemDeviceTools
    {
        // ===================================================================
        // Keyboard — inputsim_key
        // ===================================================================

        [BridgeTool("unity_open_mcp_inputsim_key",
            Title = "Input Simulation: Keyboard (Input System)",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = false,
            IdempotentHint = false,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "input-simulation")]
        [System.ComponentModel.Description(
            "Queue a keyboard event through the Input System (Keyboard.current) " +
            "during play mode. Play-mode only; gate-free. Requires " +
            "com.unity.inputsystem.")]
        public static string Key(
            string action,
            string key,
            int duration_ms = 100,
            int advance_frames = 0,
            bool shift = false,
            bool ctrl = false,
            bool alt = false)
        {
            if (!EditorApplication.isPlaying)
                return DeviceJson.Error("play_mode_required",
                    "Input simulation requires play mode. Call " +
                    "unity_open_mcp_editor_set_state(state=\"play\") first.");

            if (!InputSystemEnabled)
                return DeviceJson.Error("input_backend_unavailable",
                    "The Input System is disabled in this project (Active Input Handling is the " +
                    "legacy Input Manager), and legacy Input.GetKey* cannot be simulated in the " +
                    "Editor. Drive key-bound behaviour through invoke_method or a project command.");

            var keyboard = Keyboard.current;
            if (keyboard == null)
                return DeviceJson.Error("no_device",
                    "Keyboard.current is null. The Input System reported no keyboard " +
                    "device — this usually means the active input handling is set to " +
                    "the old Input Manager. Set Player Settings > Active Input Handling " +
                    "to 'Input System Package' or 'Both' and restart the Editor.");

            var keyEnum = ParseKey(key);
            if (!keyEnum.HasValue)
                return DeviceJson.Error("invalid_key",
                    $"Could not resolve key '{key}'. Pass a UnityEngine.InputSystem " +
                    ".Key enum name ('Space', 'W', 'LeftArrow', 'Digit1', 'F1') " +
                    "or a single character ('a', '1').");

            var mods = new List<InputKey>(3);
            if (shift) mods.Add(InputKey.LeftShift);
            if (ctrl) mods.Add(InputKey.LeftCtrl);
            if (alt) mods.Add(InputKey.LeftAlt);

            if (advance_frames < 0) advance_frames = 0;
            if (advance_frames > 60) advance_frames = 60;

            UpdateInput();
            var dispatched = new List<string>();

            switch (action)
            {
                case "down":
                    QueueKeyboardState(keyboard, keyEnum.Value, mods, true);
                    dispatched.Add("keyDown");
                    break;
                case "up":
                    // Release only the named key and explicitly requested modifiers.
                    QueueKeyboardState(keyboard, keyEnum.Value, mods, false);
                    dispatched.Add("keyUp");
                    break;
                case "tap":
                    // K1 fix: advance N frames BETWEEN down and up so polling game
                    // code (wasPressedThisFrame) can observe the press. Without
                    // advance_frames, both updates run in one synchronous call and no
                    // MonoBehaviour.Update ticks while the key is down — the press is
                    // invisible to polling code (callback-driven InputAction.performed
                    // still fires). Pass advance_frames >= 1 for polling input.
                    QueueKeyboardState(keyboard, keyEnum.Value, mods, true);
                    dispatched.Add("keyDown");
                    ProcessInputFrames(advance_frames);
                    QueueKeyboardState(keyboard, keyEnum.Value, mods, false);
                    dispatched.Add("keyUp");
                    break;
                case "hold":
                    // Hold uses the same framed sequence as tap. Real elapsed
                    // input time, not duration_ms, controls timed interactions.
                    QueueKeyboardState(keyboard, keyEnum.Value, mods, true);
                    dispatched.Add("keyDown");
                    ProcessInputFrames(advance_frames);
                    QueueKeyboardState(keyboard, keyEnum.Value, mods, false);
                    dispatched.Add("keyUp");
                    break;
                default:
                    return DeviceJson.Error("invalid_action",
                        $"Unknown key action '{action}'. Valid: down, up, tap, hold.");
            }

            UpdateInput();

            return DeviceJson.Ok(BuildKeyOk(keyEnum.Value, action, shift, ctrl, alt, duration_ms, advance_frames, dispatched));
        }

#if ENABLE_INPUT_SYSTEM
        internal static readonly bool InputSystemEnabled = true;
#else
        internal static readonly bool InputSystemEnabled = false;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        private static readonly bool LegacyInputEnabled = true;
#else
        private static readonly bool LegacyInputEnabled = false;
#endif

        private static void UpdateInput()
        {
            bool paused = EditorApplication.isPaused;
            var mode = InputSystem.settings.updateMode;
            try
            {
                // InputSystem otherwise selects Editor state while Step is paused.
                EditorApplication.isPaused = false;
                InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
                InputSystem.Update();
            }
            finally
            {
                InputSystem.settings.updateMode = mode;
                EditorApplication.isPaused = paused;
            }
        }

        // EditorApplication.Step advances gameplay but does not run the native
        // Input System update on every supported Editor. Pump input immediately
        // before gameplay Update, inside each stepped frame, so edge flags and
        // touch delta belong to that frame. Manual mode prevents double updates.
        private static void ProcessInputFrames(int frames)
        {
            if (frames <= 0) { UpdateInput(); return; }
            var original = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
            var loop = original;
            var systems = (UnityEngine.LowLevel.PlayerLoopSystem[])loop.subSystemList.Clone();
            bool installed = false;
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i].type != typeof(UnityEngine.PlayerLoop.Update)) continue;
                var update = systems[i];
                var children = new List<UnityEngine.LowLevel.PlayerLoopSystem>(update.subSystemList);
                int index = children.FindIndex(s => s.type == typeof(UnityEngine.PlayerLoop.Update.ScriptRunBehaviourUpdate));
                children.Insert(index < 0 ? 0 : index, new UnityEngine.LowLevel.PlayerLoopSystem
                {
                    type = typeof(InputSystemDeviceTools),
                    updateDelegate = UpdateInput,
                });
                update.subSystemList = children.ToArray();
                systems[i] = update;
                installed = true;
                break;
            }
            if (!installed) throw new System.InvalidOperationException("Player loop has no Update phase.");
            loop.subSystemList = systems;
            var mode = InputSystem.settings.updateMode;
            bool wasPaused = EditorApplication.isPaused;
            try
            {
                InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
                UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(loop);
                for (int i = 0; i < frames; i++) EditorApplication.Step();
            }
            finally
            {
                UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(original);
                InputSystem.settings.updateMode = mode;
                EditorApplication.isPaused = wasPaused;
            }
        }

        private static void QueueKeyboardState(
            Keyboard keyboard, InputKey key, List<InputKey> mods, bool pressed)
        {
            // Derive the complete state from the device, preserving unrelated
            // held keys without storing a second cross-call keyboard state.
            var state = new KeyboardState();
            foreach (var control in keyboard.allKeys)
                if (control.isPressed) state.Press(control.keyCode);
            if (pressed) state.Press(key);
            else state.Release(key);
            foreach (var mod in mods)
            {
                if (pressed) state.Press(mod);
                else state.Release(mod);
            }
            InputSystem.QueueStateEvent(keyboard, state);
        }

        private static InputKey? ParseKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            // 1. Direct Key enum name (case-insensitive): "Space", "LeftArrow".
            if (System.Enum.TryParse(key, true, out InputKey parsed))
                return parsed;

            // 2. Single character → Key mapping for common printable keys. The Key
            // enum assigns A..Z contiguously starting at Key.A, so arithmetic maps
            // directly; digits are mapped by name to avoid assuming enum order.
            if (key.Length == 1)
            {
                char c = key[0];
                if (c >= 'a' && c <= 'z') return (InputKey)((int)InputKey.A + (c - 'a'));
                if (c >= 'A' && c <= 'Z') return (InputKey)((int)InputKey.A + (c - 'A'));
                if (c >= '0' && c <= '9')
                {
                    if (System.Enum.TryParse("Digit" + c, true, out InputKey digitKey))
                        return digitKey;
                }
            }
            return null;
        }

        private static string BuildKeyOk(
            InputKey key, string action, bool shift, bool ctrl, bool alt,
            int durationMs, int advanceFrames, List<string> dispatched)
        {
            var sb = new System.Text.StringBuilder(192);
            sb.Append("\"device\":\"keyboard\"");
            sb.Append(",\"backend\":[\"input_system\"]");
            if (LegacyInputEnabled)
                sb.Append(",\"notes\":[").Append(DeviceJson.Esc(
                    "Legacy Input.GetKey* polling does not observe simulated keys (no supported Editor injection path); only Input System readers do.")).Append(']');
            sb.Append(",\"key\":").Append(DeviceJson.Esc(key.ToString()));
            sb.Append(",\"action\":").Append(DeviceJson.Esc(action));
            sb.Append(",\"mods\":{");
            sb.Append("\"shift\":").Append(shift ? "true" : "false");
            sb.Append(",\"ctrl\":").Append(ctrl ? "true" : "false");
            sb.Append(",\"alt\":").Append(alt ? "true" : "false").Append('}');
            sb.Append(",\"durationMs\":").Append(durationMs);
            sb.Append(",\"advanceFrames\":").Append(advanceFrames);
            sb.Append(",\"queued\":true");
            sb.Append(",\"dispatched\":[");
            for (int i = 0; i < dispatched.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(DeviceJson.Esc(dispatched[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        // ===================================================================
        // Touch backend — registered into the bridge's backend-aware
        // inputsim_touch / inputsim_gesture / inputsim_pointer(raycast_real)
        // (InputRouting). The touch tool itself lives in the bridge root
        // assembly so legacy-only projects get it too.
        // ===================================================================

        internal static UnityEngine.InputSystem.TouchPhase ToInputSystemPhase(DeviceTouchPhase phase)
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

        // Queue a single-touch state event. The minimal TouchState is routed by
        // the Input System to the matching touch control (touchId = finger + 1).
        internal static void QueueTouch(
            Touchscreen touchscreen, int finger, TouchPhase phase, Vector2 position, double time)
        {
            var state = new TouchState
            {
                touchId = finger + 1,
                phase = phase,
                position = position,
                startPosition = position, // TouchState field is startPosition, not pressPosition
                tapCount = phase == TouchPhase.Began ? (byte)1 : (byte)0,
                startTime = time,
                isPrimaryTouch = finger == 0,
                // isInProgress is read-only (derived from phase) — not settable.
            };
            InputSystem.QueueStateEvent(touchscreen, state);
        }

        internal static void UpdateNow() => UpdateInput();

        internal static void StepFrames(int frames) => ProcessInputFrames(frames);
    }

    [InitializeOnLoad]
    internal static class InputSystemTouchBackendRegistration
    {
        static InputSystemTouchBackendRegistration()
        {
            InputBackends.InputSystem = new InputSystemTouchBackend();
        }
    }

    internal sealed class InputSystemTouchBackend : IInputSystemTouchBackend
    {
        public string PackageVersion
        {
            get
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(InputSystem).Assembly);
                return info != null ? info.version : "present";
            }
        }

        public bool HasTouchscreen => Touchscreen.current != null;

        // The editor input behaviour setting exists from Input System 1.1; before
        // it, pointer input always followed Game view focus.
        public bool RespectsGameViewFocus =>
#if UNITY_OPEN_MCP_EXT_INPUTSYSTEM_1_1
            InputSystem.settings.editorInputBehaviorInPlayMode !=
            InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#else
            true;
#endif

        public void Queue(int finger, DeviceTouchPhase phase, Vector2 position)
        {
            var touchscreen = Touchscreen.current;
            if (touchscreen == null) throw new System.InvalidOperationException("Touchscreen.current is null.");
            InputSystemDeviceTools.QueueTouch(touchscreen, finger,
                InputSystemDeviceTools.ToInputSystemPhase(phase), position, Time.realtimeSinceStartupAsDouble);
        }

        public void UpdateNow() => InputSystemDeviceTools.UpdateNow();

        public void StepFrames(int frames) => InputSystemDeviceTools.StepFrames(frames);
    }


    // Minimal self-contained JSON helper for the InputSystem sub-asmdef (kept
    // separate from the Core/InputSimulationJson so the two sub-asmdefs compile
    // independently — see InputSimulationJson.cs header).
    internal static class DeviceJson
    {
        public static string Ok(string body)
            => "{\"status\":\"ok\"," + (body ?? "") + "}";

        public static string Error(string code, string message)
            => $"{{\"error\":{{\"code\":{Esc(code)},\"message\":{Esc(message)}}}}}";

        public static string Esc(string s)
        {
            if (s == null) return "\"\"";
            var sb = new System.Text.StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
