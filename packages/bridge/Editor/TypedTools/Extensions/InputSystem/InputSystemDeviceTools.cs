// Input simulation embedded domain — Input System device events.
//
// Compile-gated on com.unity.inputsystem (UNITY_OPEN_MCP_EXT_INPUTSIM_IS, set
// by the owning sub-asmdef's versionDefines). Queues keyboard + touch device
// events through the new Input System, covering gameplay code that reads
// Keyboard.current / Touchscreen.current / InputAction (device surface) — i.e.
// code that the uGUI pointer tool (EventSystem + ExecuteEvents) does NOT reach.
//
// State is queued via the public InputSystem.QueueStateEvent API:
//   InputSystem.QueueStateEvent(Keyboard.current, new KeyboardState(Key.Space));
//   InputSystem.Update();   // process queued events this frame
// No unsafe pointers required.
//
// Play-mode only (refuses with play_mode_required otherwise). Gate-free
// (IsMutating=false — input writes no assets).
//
// Honest limits (documented in the domain skill):
//   - `hold` duration is best-effort: a synchronous tool call cannot yield
//     frames, so press+release dispatch in one InputSystem.Update cycle. Hold
//     INTERACTIONS (Input System interactions with a duration threshold) will
//     not fire from `hold`; use `down`, advance frames, then `up` for those.
//   - Touch is the version-sensitive half: Touchscreen.current may be null in
//     a desktop Editor without a touch device added. For uGUI swipe/drag prefer
//     inputsim_pointer (action: drag); reserve inputsim_touch for games reading
//     the Input System Touchscreen directly.
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
                    ".LowLevel.Key enum name ('Space', 'W', 'LeftArrow', 'Digit1', 'F1') " +
                    "or a single character ('a', '1').");

            var mods = new List<InputKey>(3);
            if (shift) mods.Add(InputKey.LeftShift);
            if (ctrl) mods.Add(InputKey.LeftCtrl);
            if (alt) mods.Add(InputKey.LeftAlt);

            if (advance_frames < 0) advance_frames = 0;
            if (advance_frames > 60) advance_frames = 60;

            var dispatched = new List<string>();
            var now = Time.realtimeSinceStartup;

            switch (action)
            {
                case "down":
                    QueueKeyboardState(keyboard, keyEnum.Value, mods, true, now);
                    dispatched.Add("keyDown");
                    break;
                case "up":
                    // K3: `up` queues an EMPTY KeyboardState, releasing EVERY held key
                    // (the named key AND any mods AND any other key held by a prior
                    // `down`), because per-key release would require cross-call state.
                    // This is documented in the schema description — silently dropping
                    // other held keys is not acceptable, so the behavior is explicit.
                    // To release one key among several: prefer `tap` with the key, or
                    // track held keys in game code via `down`/`up` pairs per key.
                    QueueKeyboardState(keyboard, keyEnum.Value, mods, false, now);
                    dispatched.Add("keyUp");
                    break;
                case "tap":
                    // K1 fix: advance N frames BETWEEN down and up so polling game
                    // code (wasPressedThisFrame) can observe the press. Without
                    // advance_frames, both updates run in one synchronous call and no
                    // MonoBehaviour.Update ticks while the key is down — the press is
                    // invisible to polling code (callback-driven InputAction.performed
                    // still fires). Pass advance_frames >= 1 for polling input.
                    QueueKeyboardState(keyboard, keyEnum.Value, mods, true, now);
                    dispatched.Add("keyDown");
                    InputSystem.Update();
                    StepFrames(advance_frames);
                    QueueKeyboardState(keyboard, keyEnum.Value, mods, false, now + 0.001);
                    dispatched.Add("keyUp");
                    break;
                case "hold":
                    // K1 fix: with advance_frames, the press lands, frames advance
                    // (so Hold interactions with a duration threshold can fire and
                    // polling code observes the held state), then release. duration_ms
                    // is recorded for the response; the real held-time gate is the
                    // number of advanced frames, not wall-clock.
                    QueueKeyboardState(keyboard, keyEnum.Value, mods, true, now);
                    dispatched.Add("keyDown");
                    InputSystem.Update();
                    StepFrames(advance_frames);
                    QueueKeyboardState(keyboard, keyEnum.Value, mods, false,
                        now + System.Math.Max(0.001, duration_ms / 1000.0));
                    dispatched.Add("keyUp");
                    break;
                default:
                    return DeviceJson.Error("invalid_action",
                        $"Unknown key action '{action}'. Valid: down, up, tap, hold.");
            }

            InputSystem.Update();

            return DeviceJson.Ok(BuildKeyOk(keyEnum.Value, action, shift, ctrl, alt, duration_ms, advance_frames, dispatched));
        }

        // Pump N play-mode frames via EditorApplication.Step so polling game code
        // runs between input events. Synchronous (Step runs a full frame inline),
        // so this does not deadlock the dispatch queue. No-op (0 frames) is valid.
        // feedback S4 — Step() pauses the editor; capture and restore the as-found
        // pause state so a key/touch call with advance_frames does not leave the
        // game frozen for the next dispatch.
        private static void StepFrames(int frames)
        {
            if (frames <= 0) return;
            bool wasPaused = EditorApplication.isPaused;
            try
            {
                for (int i = 0; i < frames; i++) EditorApplication.Step();
            }
            finally
            {
                EditorApplication.isPaused = wasPaused;
            }
        }

        private static void QueueKeyboardState(
            Keyboard keyboard, InputKey key, List<InputKey> mods, bool pressed, double time)
        {
            // KeyboardState has no Add() — use Press(Key), which sets the bit for
            // the key in the state's bitfield. (Input System API.)
            var state = new KeyboardState();
            if (pressed)
            {
                state.Press(key);
                for (int i = 0; i < mods.Count; i++) state.Press(mods[i]);
            }
            InputSystem.QueueStateEvent(keyboard, state, time);
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
        // Touch — inputsim_touch
        // ===================================================================

        [BridgeTool("unity_open_mcp_inputsim_touch",
            Title = "Input Simulation: Touch / Swipe (Input System)",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = false,
            IdempotentHint = false,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "input-simulation")]
        [System.ComponentModel.Description(
            "Queue a touch / swipe event sequence through the Input System " +
            "(Touchscreen.current) during play mode. Play-mode only; gate-free. " +
            "Requires com.unity.inputsystem. For uGUI drag/swipe prefer " +
            "inputsim_pointer.")]
        public static string Touch(
            string action,
            int finger = 0,
            string target = null,
            float? screen_x = null,
            float? screen_y = null,
            string from_target = null,
            float? from_x = null,
            float? from_y = null,
            string to_target = null,
            float? to_x = null,
            float? to_y = null,
            int duration_ms = 200,
            int steps = 10,
            int advance_frames = 0)
        {
            if (!EditorApplication.isPlaying)
                return DeviceJson.Error("play_mode_required",
                    "Input simulation requires play mode. Call " +
                    "unity_open_mcp_editor_set_state(state=\"play\") first.");

            var touchscreen = Touchscreen.current;
            if (touchscreen == null)
                return DeviceJson.Error("no_device",
                    "Touchscreen.current is null. The Editor has no touch device; " +
                    "for uGUI drag/swipe use unity_open_mcp_inputsim_pointer " +
                    "(action: drag) instead, or add a simulated Touchscreen via " +
                    "UnityEngine.InputSystem.LowLevel.TouchscreenSimulation.");

            if (finger < 0) finger = 0;
            if (finger > 9) finger = 9;
            if (advance_frames < 0) advance_frames = 0;
            if (advance_frames > 60) advance_frames = 60;

            // Resolve endpoints.
            Vector2 point;
            switch (action)
            {
                case "tap":
                case "press":
                case "release":
                    {
                        var resolved = ResolvePoint(target, screen_x, screen_y, out var p, out var err);
                        if (!resolved) return DeviceJson.Error(err.Code, err.Message);
                        point = p;
                        break;
                    }
                case "swipe":
                    return DoSwipe(touchscreen, finger, steps, duration_ms, advance_frames,
                        from_target, from_x, from_y, to_target, to_x, to_y);
                default:
                    return DeviceJson.Error("invalid_action",
                        $"Unknown touch action '{action}'. Valid: tap, swipe, press, release.");
            }

            var dispatched = new List<string>();
            var now = Time.realtimeSinceStartup;

            switch (action)
            {
                case "tap":
                    // K1 fix (same as keyboard tap): advance frames between began and
                    // ended so polling touch code observes the press.
                    QueueTouch(touchscreen, finger, TouchPhase.Began, point, now);
                    dispatched.Add("began");
                    InputSystem.Update();
                    StepFrames(advance_frames);
                    QueueTouch(touchscreen, finger, TouchPhase.Ended, point, now + 0.001);
                    dispatched.Add("ended");
                    break;
                case "press":
                    QueueTouch(touchscreen, finger, TouchPhase.Began, point, now);
                    dispatched.Add("began");
                    break;
                case "release":
                    QueueTouch(touchscreen, finger, TouchPhase.Ended, point, now);
                    dispatched.Add("ended");
                    break;
            }

            InputSystem.Update();

            return DeviceJson.Ok(BuildTouchOk(action, finger, point, dispatched));
        }

        private static string DoSwipe(
            Touchscreen touchscreen, int finger, int steps, int durationMs, int advanceFrames,
            string fromTarget, float? fromX, float? fromY,
            string toTarget, float? toX, float? toY)
        {
            // Resolve FROM.
            Vector2 fromPoint;
            if (!string.IsNullOrEmpty(fromTarget))
            {
                if (!TryResolveTarget(fromTarget, out fromPoint, out var fromErr))
                    return DeviceJson.Error(fromErr.Code, $"Swipe from_target — {fromErr.Message}");
            }
            else if (fromX.HasValue && fromY.HasValue)
            {
                fromPoint = new Vector2(fromX.Value, fromY.Value);
            }
            else
            {
                return DeviceJson.Error("no_swipe_from",
                    "Swipe requires a start: pass from_target, or both from_x and from_y.");
            }

            // Resolve TO.
            Vector2 toPoint;
            if (toX.HasValue && toY.HasValue) toPoint = new Vector2(toX.Value, toY.Value);
            else if (!string.IsNullOrEmpty(toTarget))
            {
                if (!TryResolveTarget(toTarget, out toPoint, out var toErr))
                    return DeviceJson.Error(toErr.Code, $"Swipe to_target — {toErr.Message}");
            }
            else toPoint = fromPoint;

            // feedback S6 — bound swipe steps (each can run advance_frames × Step).
            if (steps < 1) steps = 1;
            if (steps > 100) steps = 100;

            var dispatched = new List<string>();
            var now = Time.realtimeSinceStartup;
            var stepDt = System.Math.Max(0.001, durationMs / 1000.0 / steps);

            // Begin.
            QueueTouch(touchscreen, finger, TouchPhase.Began, fromPoint, now);
            dispatched.Add("began");
            InputSystem.Update();

            // K2 fix: when advance_frames > 0, advance a frame AFTER each Moved
            // phase so the swipe genuinely unfolds across frames for polling code
            // (per-frame primaryTouch.delta, distance/velocity gesture recognizers).
            // Without advance_frames, all Moved phases collapse into one update —
            // documented but previously mis-advertised as "across real frames".
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                var p = Vector2.Lerp(fromPoint, toPoint, t);
                QueueTouch(touchscreen, finger, TouchPhase.Moved, p, now + i * stepDt);
                dispatched.Add("moved");
                InputSystem.Update();
                StepFrames(advanceFrames);
            }

            // End.
            QueueTouch(touchscreen, finger, TouchPhase.Ended, toPoint, now + (steps + 1) * stepDt);
            dispatched.Add("ended");
            InputSystem.Update();

            return DeviceJson.Ok(BuildTouchOk("swipe", finger, toPoint, dispatched));
        }

        // Queue a single-touch state event. Touchscreen state is an array of
        // TouchState slots; writing one slot at `finger` via QueueStateEvent on
        // the whole device requires the full TouchscreenState. We construct the
        // minimal TouchState and queue it; the Input System routes it to the
        // matching touch control. Multi-touch beyond finger 0 is best-effort in
        // v1 — see the domain skill.
        private static void QueueTouch(
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
            InputSystem.QueueStateEvent(touchscreen, state, time);
        }

        // Resolve a screen point from target OR screen_x/screen_y. Target-first:
        // name/path → its screen point (via TryResolveTarget, with ambiguity
        // detection), else explicit screen_x/screen_y.
        private static bool ResolvePoint(
            string target, float? sx, float? sy,
            out Vector2 point, out (string Code, string Message) err)
        {
            point = default;
            err = default;
            if (!string.IsNullOrEmpty(target))
                return TryResolveTarget(target, out point, out err);
            if (sx.HasValue && sy.HasValue)
            {
                point = new Vector2(sx.Value, sy.Value);
                return true;
            }
            err = ("no_target_or_screen_point",
                "Provide either `target` (name/path) or both `screen_x` and `screen_y`.");
            return false;
        }

        // Resolve a target by name or slash-path with ambiguity detection,
        // mirroring inputsim_pointer's PointerTargets.FindByPath (root-anchored
        // precedence, then trailing-segment match). Self-contained so this sub-
        // asmdef stays independent of the uGUI one. Unlike GameObject.Find, never
        // returns an arbitrary first match: 0 → target_not_found, >1 →
        // ambiguous_target with candidate paths.
        private static bool TryResolveTarget(
            string target, out Vector2 point, out (string Code, string Message) err)
        {
            point = default;
            err = default;
            if (string.IsNullOrEmpty(target))
            {
                err = ("no_target_or_screen_point",
                    "Provide either `target` (name/path) or both `screen_x` and `screen_y`.");
                return false;
            }

            var parts = target.Split('/');
            bool isPath = parts.Length > 1;
            var matches = new List<GameObject>();
            List<GameObject> anchored = isPath ? new List<GameObject>() : null;
            foreach (var t in SceneQuery.FindActiveTransforms())
            {
                if (t == null) continue;
                if (IsPathMatch(t, parts, isPath)) matches.Add(t.gameObject);
                if (isPath && IsRootAnchoredMatch(t, parts)) anchored.Add(t.gameObject);
            }
            if (isPath && anchored != null && anchored.Count > 0) matches = anchored;

            if (matches.Count == 0)
            {
                err = ("target_not_found",
                    $"No active GameObject found for target '{target}'.");
                return false;
            }
            if (matches.Count > 1)
            {
                var candidates = new List<string>(System.Math.Min(matches.Count, 12));
                foreach (var m in matches)
                {
                    if (candidates.Count >= 12) break;
                    candidates.Add(BuildPath(m));
                }
                err = ("ambiguous_target",
                    $"Target '{target}' matches {matches.Count} active GameObjects: " +
                    string.Join(", ", candidates) +
                    ". Pass the full path or screen coordinates.");
                return false;
            }
            point = ScreenPointOf(matches[0]);
            return true;
        }

        // Trailing-segment path/name match (matches anywhere in the hierarchy).
        private static bool IsPathMatch(Transform t, string[] parts, bool isPath)
        {
            if (!isPath) return t.gameObject.name == parts[0];
            var cur = t;
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (cur == null) return false;
                if (cur.gameObject.name != parts[i]) return false;
                cur = cur.parent;
            }
            return true;
        }

        // Root-anchored exact match: trailing walk AND the topmost matched node
        // is a scene root (no parent).
        private static bool IsRootAnchoredMatch(Transform t, string[] parts)
        {
            var cur = t;
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (cur == null) return false;
                if (cur.gameObject.name != parts[i]) return false;
                cur = cur.parent;
            }
            return cur == null;
        }

        private static string BuildPath(GameObject go)
        {
            if (go == null) return "";
            var sb = new System.Text.StringBuilder();
            var t = go.transform;
            while (t != null)
            {
                if (sb.Length > 0) sb.Insert(0, '/');
                sb.Insert(0, t.name);
                t = t.parent;
            }
            return sb.ToString();
        }

        private static Vector2 ScreenPointOf(GameObject go)
        {
            var rt = go.transform as RectTransform;
            if (rt != null)
            {
                var canvas = go.GetComponentInParent<Canvas>();
                Camera cam = null;
                if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
                // Rect CENTER, not the pivot (matches inputsim_pointer's P4 fix) —
                // the pivot's world position sits in a corner for off-pivot
                // RectTransforms and can fall outside the visible art.
                return RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.center));
            }
            var mainCam = Camera.main;
            return mainCam != null
                ? mainCam.WorldToScreenPoint(go.transform.position)
                : new Vector2(go.transform.position.x, go.transform.position.y);
        }

        private static string BuildTouchOk(string action, int finger, Vector2 end, List<string> dispatched)
        {
            var sb = new System.Text.StringBuilder(192);
            sb.Append("\"device\":\"touchscreen\"");
            sb.Append(",\"action\":").Append(DeviceJson.Esc(action));
            sb.Append(",\"finger\":").Append(finger);
            sb.Append(",\"endPoint\":[").Append(end.x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(',').Append(end.y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append(']');
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
