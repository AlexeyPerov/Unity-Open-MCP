// Playtest — Game view resolution and Device Simulator (unity_open_mcp_game_view).
//
// Actions:
//   get            — mode (game | simulator | none), the size entry or device,
//                    window size in logical points and pixels (HiDPI), scale,
//                    focus, and what the game sees: Screen width/height, aspect,
//                    safe area + insets, cutouts, dpi, orientation.
//   set_resolution — select a fixed `w×h` Game view size, creating or reusing
//                    the custom entry `Open MCP <w>x<h>`; or select an existing
//                    entry by label (`size_label`, e.g. "Free Aspect" to restore).
//                    Swaps the Simulator tab back to the Game view when needed.
//   set_device     — the only place that opens the Device Simulator: swap the
//                    Game view tab for the Simulator (or open one), select a
//                    device and rotate it to an orientation.
//   list_devices   — the Simulator's device catalog with a safe-area summary,
//                    paged.
//
// Both set actions bring the play-mode view's tab to the front inside Unity
// (EditorWindow.Focus; the Unity application is never raised): a docked tab
// that is not in front does not render, and the game then runs at the Editor's
// fallback 640x480.
//
// Settling: in edit mode a change applies synchronously. In play mode the game
// observes a new size only on a later frame (Editor code reading Screen.width
// sees whichever Editor panel is current, never the game), so set_* starts a
// short operation on the shared play-mode scheduler that watches Screen.width/
// height inside the player loop and the view's render target, and finishes
// when both report the new size for two consecutive frames (or reports
// settled:false at its time limit). MCP polls it, so a set_* call returns once
// the game reports the new size or answers `not_settled`.
//
// Gate-free: an Editor UI change, not a project asset mutation. The only
// persistent effect is the custom size entry in the Editor's Game view size
// list (Editor preferences): reused by label, never deleted. Nothing is
// restored implicitly — `get` before and an explicit `set_*` after is the
// caller's choice.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Playtest
{
    [BridgeToolType]
    public static class GameViewTools
    {
        internal const string ToolName = "unity_open_mcp_game_view";
        internal const string DeviceCursorPrefix = "game_view_devices";
        internal const int DefaultDevicePageSize = 25;
        internal const int MaxDevicePageSize = 100;
        internal const int DefaultSettleTimeoutMs = 5000;
        internal const int MaxSettleTimeoutMs = 20000;
        private static readonly string[] Actions = { "get", "set_resolution", "set_device", "list_devices" };

        // A play-mode view behind another tab does not render: its render target
        // and its configured size describe the last frame it drew, not the game.
        internal const string HiddenViewWarning =
            "The play-mode view is not the front tab of its dock area, so the running game does not render into it: " +
            "render_texture is the last frame it drew and screen is the view's configured size, not what the game sees now. " +
            "game_view set_resolution / set_device bring it to the front.";

        [BridgeTool(ToolName,
            Title = "Playtest: Game View and Device Simulator",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = false,
            IdempotentHint = true,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "playtest")]
        [System.ComponentModel.Description(
            "Read or set the Game view resolution and the Device Simulator device/orientation " +
            "(get, set_resolution, set_device, list_devices). Editor UI only, gate-free; the one " +
            "persistent effect is a reused 'Open MCP <w>x<h>' Game view size entry.")]
        public static string GameView(
            string action = "get",
            int? width = null,
            int? height = null,
            string size_label = null,
            string device = null,
            string orientation = null,
            string query = null,
            int page_size = DefaultDevicePageSize,
            string cursor = null,
            int? settle_timeout_ms = null)
        {
            action = string.IsNullOrEmpty(action) ? "get" : action.Trim().ToLowerInvariant();
            if (Array.IndexOf(Actions, action) < 0)
                return Error("invalid_arguments", "Unknown action '" + action + "'. Valid: " + string.Join(", ", Actions) + ".");

            if (!GameViewAdapter.IsGameViewAvailable(out var code, out var message))
                return Unavailable(code, message);
            if ((action == "set_device" || action == "list_devices") && !GameViewAdapter.IsSimulatorAvailable(out code, out message))
                return Unavailable(code, message);

            int timeout = settle_timeout_ms ?? DefaultSettleTimeoutMs;
            if (timeout < 100 || timeout > MaxSettleTimeoutMs)
                return Error("invalid_arguments", "settle_timeout_ms must be 100.." + MaxSettleTimeoutMs + ".");

            try
            {
                switch (action)
                {
                    case "get": return Get();
                    case "list_devices": return ListDevices(query, page_size, cursor);
                    case "set_resolution": return SetResolution(width, height, size_label, timeout);
                    default: return SetDevice(device, orientation, timeout);
                }
            }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                return Error("execution_error", inner.GetType().Name + ": " + inner.Message);
            }
        }

        // ------------------------------------------------------------------
        // get
        // ------------------------------------------------------------------

        private static string Get()
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"status\":\"ok\",\"action\":\"get\",");
            AppendState(sb, GameViewAdapter.MainView());
            sb.Append('}');
            return sb.ToString();
        }

        // State fields (no surrounding braces). Shared by every action's response.
        internal static void AppendState(StringBuilder sb, EditorWindow view)
        {
            var kind = GameViewAdapter.Kind(view);
            sb.Append("\"mode\":").Append(BridgeJson.EscapeString(GameViewAdapter.KindName(kind)));
            sb.Append(",\"playing\":").Append(EditorApplication.isPlaying ? "true" : "false");
            sb.Append(",\"paused\":").Append(EditorApplication.isPaused ? "true" : "false");
            if (view == null)
            {
                sb.Append(",\"screen\":null");
                return;
            }

            sb.Append(",\"focused\":").Append(view.hasFocus ? "true" : "false");
            var front = GameViewAdapter.IsFrontTab(view);
            if (front.HasValue) sb.Append(",\"visible\":").Append(front.Value ? "true" : "false");
            if (front == false && EditorApplication.isPlaying)
                sb.Append(",\"warning\":").Append(BridgeJson.EscapeString(HiddenViewWarning));
            float backing = GameViewAdapter.BackingScale(view);
            var pos = view.position;
            sb.Append(",\"window\":{\"logical\":[").Append(Num(pos.width)).Append(',').Append(Num(pos.height)).Append(']');
            sb.Append(",\"pixels\":[").Append(Num(pos.width * backing)).Append(',').Append(Num(pos.height * backing)).Append(']');
            sb.Append(",\"backing_scale\":").Append(Num(backing)).Append('}');

            SimulatorSnapshot sim = null;
            if (kind == PlayModeViewKind.Game)
            {
                var size = GameViewAdapter.SelectedSize(view);
                if (size != null)
                {
                    sb.Append(",\"size\":{\"index\":").Append(size.Index);
                    sb.Append(",\"label\":").Append(BridgeJson.EscapeString(size.Label));
                    sb.Append(",\"type\":").Append(BridgeJson.EscapeString(size.Type));
                    sb.Append(",\"custom\":").Append(size.Custom ? "true" : "false");
                    if (size.Type != "free_aspect")
                        sb.Append(",\"width\":").Append(size.Width).Append(",\"height\":").Append(size.Height);
                    sb.Append('}');
                }
                float zoom = GameViewAdapter.Zoom(view);
                if (zoom > 0) sb.Append(",\"scale\":").Append(Num(zoom));
                var lowResolution = GameViewAdapter.LowResolution(view);
                if (lowResolution.HasValue)
                    sb.Append(",\"low_resolution_aspect_ratios\":").Append(lowResolution.Value ? "true" : "false");
            }
            else if (kind == PlayModeViewKind.Simulator && GameViewAdapter.IsSimulatorAvailable(out _, out _))
            {
                sim = GameViewAdapter.Simulator(view);
                if (sim != null)
                {
                    sb.Append(",\"device\":");
                    if (sim.Device == null) sb.Append("null");
                    else
                    {
                        sb.Append("{\"id\":").Append(BridgeJson.EscapeString(sim.Device.Id));
                        sb.Append(",\"physical\":[").Append(sim.Device.Width).Append(',').Append(sim.Device.Height).Append(']');
                        sb.Append(",\"dpi\":").Append(Num(sim.Device.Dpi));
                        sb.Append(",\"rotation\":").Append(sim.Rotation);
                        sb.Append(",\"screens\":").Append(sim.Device.Screens).Append('}');
                    }
                    if (sim.Scale > 0) sb.Append(",\"scale\":").Append(Num(sim.Scale));
                }
            }

            var rt = GameViewAdapter.TargetTexture(view);
            sb.Append(",\"render_texture\":");
            if (rt == null) sb.Append("null");
            else sb.Append('[').Append(rt.width).Append(',').Append(rt.height).Append(']');

            sb.Append(",\"screen\":");
            if (!GameViewAdapter.TryGameScreen(out _, out _, out int w, out int h, out Rect safe, out var simState))
            {
                sb.Append("null");
                return;
            }
            sim = sim ?? simState;
            sb.Append("{\"width\":").Append(w).Append(",\"height\":").Append(h);
            sb.Append(",\"aspect\":").Append(BridgeJson.EscapeString(GameViewAdapter.AspectLabel(w, h)));
            sb.Append(",\"safe_area\":");
            AppendRect(sb, safe);
            GameViewAdapter.Insets(safe, w, h, out int top, out int bottom, out int left, out int right);
            sb.Append(",\"insets\":{\"top\":").Append(top).Append(",\"bottom\":").Append(bottom)
              .Append(",\"left\":").Append(left).Append(",\"right\":").Append(right).Append('}');
            if (sim != null)
            {
                sb.Append(",\"cutouts\":[");
                for (int i = 0; i < sim.Cutouts.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendRect(sb, sim.Cutouts[i]);
                }
                sb.Append(']');
                sb.Append(",\"dpi\":").Append(Num(sim.Dpi));
                sb.Append(",\"orientation\":").Append(BridgeJson.EscapeString(GameViewAdapter.OrientationName(sim.Orientation)));
            }
            else
            {
                sb.Append(",\"dpi\":").Append(Num(Screen.dpi));
                sb.Append(",\"orientation\":").Append(BridgeJson.EscapeString(GameViewAdapter.ShapeOrientation(w, h)));
            }
            sb.Append('}');
        }

        // ------------------------------------------------------------------
        // list_devices
        // ------------------------------------------------------------------

        private static string ListDevices(string query, int pageSize, string cursor)
        {
            if (pageSize < 1) pageSize = 1;
            if (pageSize > MaxDevicePageSize) pageSize = MaxDevicePageSize;
            var view = GameViewAdapter.MainView();
            var simulator = GameViewAdapter.Kind(view) == PlayModeViewKind.Simulator ? view : null;
            var all = GameViewAdapter.Devices(simulator);
            string current = simulator != null ? GameViewAdapter.Simulator(simulator)?.Device?.Id : null;

            var selected = new List<DeviceEntry>();
            var needle = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
            foreach (var d in all)
                if (needle == null || (d.Id != null && d.Id.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
                    selected.Add(d);

            int offset = ParseCursor(cursor);
            if (offset > selected.Count) offset = selected.Count;
            int end = Math.Min(offset + pageSize, selected.Count);

            var sb = new StringBuilder(256 + (end - offset) * 320);
            sb.Append("{\"status\":\"ok\",\"action\":\"list_devices\"");
            sb.Append(",\"total\":").Append(selected.Count);
            sb.Append(",\"current\":").Append(BridgeJson.EscapeString(current));
            sb.Append(",\"devices\":[");
            for (int i = offset; i < end; i++)
            {
                if (i > offset) sb.Append(',');
                AppendDevice(sb, selected[i]);
            }
            sb.Append(']');
            BridgeJson.AppendPaginationBlock(sb, DeviceCursorPrefix, offset, pageSize, selected.Count - end);
            sb.Append('}');
            return sb.ToString();
        }

        internal static void AppendDevice(StringBuilder sb, DeviceEntry d)
        {
            sb.Append("{\"id\":").Append(BridgeJson.EscapeString(d.Id));
            sb.Append(",\"screen\":[").Append(d.Width).Append(',').Append(d.Height).Append(']');
            sb.Append(",\"dpi\":").Append(Num(d.Dpi));
            if (d.Screens > 1) sb.Append(",\"screens\":").Append(d.Screens);
            var portrait = d.Get(ScreenOrientation.Portrait);
            if (portrait != null)
            {
                GameViewAdapter.Insets(portrait.SafeArea, d.Width, d.Height, out int top, out int bottom, out int left, out int right);
                sb.Append(",\"portrait_safe_area\":");
                AppendRect(sb, portrait.SafeArea);
                sb.Append(",\"insets\":{\"top\":").Append(top).Append(",\"bottom\":").Append(bottom)
                  .Append(",\"left\":").Append(left).Append(",\"right\":").Append(right).Append('}');
                sb.Append(",\"cutouts\":").Append(portrait.Cutouts);
                sb.Append(",\"notch\":").Append(top > 0 || portrait.Cutouts > 0 ? "true" : "false");
            }
            sb.Append(",\"orientations\":[");
            for (int i = 0; i < d.Orientations.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(GameViewAdapter.OrientationName(d.Orientations[i].Orientation)));
            }
            sb.Append("]}");
        }

        // ------------------------------------------------------------------
        // set_resolution
        // ------------------------------------------------------------------

        internal static string ValidateResolutionArgs(int? width, int? height, string sizeLabel)
        {
            bool hasLabel = !string.IsNullOrWhiteSpace(sizeLabel);
            if (hasLabel && (width.HasValue || height.HasValue))
                return "Pass width + height or size_label, not both.";
            if (hasLabel) return null;
            if (!width.HasValue || !height.HasValue)
                return "set_resolution needs width and height (pixels), or size_label (an existing Game view size label, e.g. \"Free Aspect\").";
            if (width.Value < GameViewAdapter.MinResolution || width.Value > GameViewAdapter.MaxResolution ||
                height.Value < GameViewAdapter.MinResolution || height.Value > GameViewAdapter.MaxResolution)
                return "width and height must be " + GameViewAdapter.MinResolution + ".." + GameViewAdapter.MaxResolution + " pixels.";
            return null;
        }

        private static string SetResolution(int? width, int? height, string sizeLabel, int timeoutMs)
        {
            var argError = ValidateResolutionArgs(width, height, sizeLabel);
            if (argError != null) return Error("invalid_arguments", argError);

            var view = GameViewAdapter.MainView();
            var kind = GameViewAdapter.Kind(view);
            string switched = null;
            if (kind == PlayModeViewKind.Simulator)
            {
                view = GameViewAdapter.SwapTo(view, PlayModeViewKind.Game);
                switched = "simulator_to_game_view";
            }
            else if (kind == PlayModeViewKind.None || kind == PlayModeViewKind.Other)
            {
                view = GameViewAdapter.Open(PlayModeViewKind.Game);
                switched = "opened_game_view";
            }
            if (GameViewAdapter.Kind(view) != PlayModeViewKind.Game)
                return Error("game_view_unavailable", "No Game view could be found or opened.");

            GameViewSizeEntry entry;
            bool created = false;
            if (!string.IsNullOrWhiteSpace(sizeLabel))
            {
                entry = null;
                var sizes = GameViewAdapter.Sizes();
                foreach (var s in sizes)
                    if (string.Equals(s.Label, sizeLabel.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.BaseText, sizeLabel.Trim(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(s.BaseText))
                    { entry = s; break; }
                if (entry == null)
                {
                    var labels = new List<string>();
                    foreach (var s in sizes) labels.Add(s.Label);
                    return Error("size_not_found", "No Game view size labelled '" + sizeLabel + "' in the current size group.",
                        "\"labels\":" + JsonArray(labels));
                }
            }
            else
            {
                entry = GameViewAdapter.EnsureCustomSize(width.Value, height.Value, out created);
                if (entry == null) return Error("execution_error", "The custom Game view size could not be created.");
            }

            GameViewAdapter.SelectSize(view, entry.Index);
            bool focusedView = BringToFront(view);
            var render = GameViewAdapter.RenderSize(view);
            int ew = (int)Math.Round(render.x, MidpointRounding.AwayFromZero);
            int eh = (int)Math.Round(render.y, MidpointRounding.AwayFromZero);
            bool clamped = entry.Type == "fixed_resolution" && (ew != entry.Width || eh != entry.Height);

            var extra = new StringBuilder(256);
            extra.Append("\"entry\":{\"label\":").Append(BridgeJson.EscapeString(entry.Label));
            extra.Append(",\"index\":").Append(entry.Index);
            extra.Append(",\"type\":").Append(BridgeJson.EscapeString(entry.Type));
            extra.Append(",\"created\":").Append(created ? "true" : "false");
            if (entry.Custom) extra.Append(",\"persistent\":true");
            extra.Append('}');
            if (switched != null) extra.Append(",\"switched\":").Append(BridgeJson.EscapeString(switched));
            extra.Append(",\"brought_to_front\":").Append(focusedView ? "true" : "false");
            // A clamped size is still applied: the game renders (and the settle
            // watches) the clamped render size.
            if (clamped)
                extra.Append(",\"clamped\":{\"requested\":[").Append(entry.Width).Append(',').Append(entry.Height)
                     .Append("],\"render\":[").Append(ew).Append(',').Append(eh).Append("],\"note\":")
                     .Append(BridgeJson.EscapeString("The Editor clamps this size (GPU texture limit or the 8K cap); the game renders at the clamped size."))
                     .Append('}');
            return Finish("set_resolution", view, ew, eh, timeoutMs, extra.ToString());
        }

        // ------------------------------------------------------------------
        // set_device
        // ------------------------------------------------------------------

        private static string SetDevice(string device, string orientation, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(device))
                return Error("invalid_arguments", "set_device needs device (an id from list_devices).");
            var requested = ScreenOrientation.Portrait;
            if (!string.IsNullOrWhiteSpace(orientation) && !GameViewAdapter.TryParseOrientation(orientation, out requested))
                return Error("invalid_arguments", "orientation must be portrait, portrait_upside_down, landscape_left or landscape_right.");

            var view = GameViewAdapter.MainView();
            var kind = GameViewAdapter.Kind(view);
            var catalog = GameViewAdapter.Devices(kind == PlayModeViewKind.Simulator ? view : null);
            var ids = new List<string>(catalog.Count);
            foreach (var d in catalog) ids.Add(d.Id);
            int match = GameViewAdapter.MatchDevice(ids, device, out var candidates);
            if (match == -2)
                return Error("ambiguous_device", "'" + device + "' matches " + candidates.Count + " devices; pass the exact id.",
                    "\"candidates\":" + JsonArray(candidates));
            if (match < 0)
                return Error("device_not_found", "No Device Simulator device matches '" + device + "'. Call list_devices for the ids.",
                    "\"available\":" + catalog.Count);
            var target = catalog[match];
            if (target.Get(requested) == null)
            {
                var supported = new List<string>();
                foreach (var o in target.Orientations) supported.Add(GameViewAdapter.OrientationName(o.Orientation));
                return Error("invalid_arguments", "Device '" + target.Id + "' does not support " + GameViewAdapter.OrientationName(requested) + ".",
                    "\"orientations\":" + JsonArray(supported));
            }
            int degrees = GameViewAdapter.RotationFor(requested);
            if (degrees < 0)
                return Error("editor_internals_unavailable", "The Simulator has no device rotation for " + GameViewAdapter.OrientationName(requested) + ".");

            string switched = null;
            if (kind == PlayModeViewKind.Game)
            {
                view = GameViewAdapter.SwapTo(view, PlayModeViewKind.Simulator);
                switched = "game_view_to_simulator";
            }
            else if (kind != PlayModeViewKind.Simulator)
            {
                view = GameViewAdapter.Open(PlayModeViewKind.Simulator);
                switched = "opened_simulator";
            }
            if (GameViewAdapter.Kind(view) != PlayModeViewKind.Simulator)
                return Error("simulator_unavailable", "The Device Simulator could not be opened.");

            // The Simulator's own catalog may be ordered differently from the loader's.
            int index = -1;
            foreach (var d in GameViewAdapter.Devices(view))
                if (d.Id == target.Id) { index = d.Index; break; }
            if (index < 0) return Error("device_not_found", "Device '" + target.Id + "' is not in the open Simulator's list.");
            GameViewAdapter.SelectDevice(view, index);
            GameViewAdapter.Rotate(view, degrees);
            bool focusedView = BringToFront(view);

            var sim = GameViewAdapter.Simulator(view);
            if (sim == null || sim.Width <= 0 || sim.Height <= 0)
                return Error("simulator_unavailable", "The Device Simulator did not report a screen after selecting the device.");
            bool applied = sim.Orientation == requested;

            var extra = new StringBuilder(256);
            extra.Append("\"requested\":{\"device\":").Append(BridgeJson.EscapeString(target.Id));
            extra.Append(",\"orientation\":").Append(BridgeJson.EscapeString(GameViewAdapter.OrientationName(requested))).Append('}');
            extra.Append(",\"orientation_applied\":").Append(applied ? "true" : "false");
            if (!applied)
                extra.Append(",\"note\":").Append(BridgeJson.EscapeString(
                    "The device is rotated to " + GameViewAdapter.OrientationName(requested) + " but the game stays " +
                    GameViewAdapter.OrientationName(sim.Orientation) + ": the project's orientation settings (Default " +
                    "Orientation / allowed auto-rotation, or Screen.orientation set by game code) decide, as on a device."));
            if (switched != null) extra.Append(",\"switched\":").Append(BridgeJson.EscapeString(switched));
            extra.Append(",\"brought_to_front\":").Append(focusedView ? "true" : "false");
            return Finish("set_device", view, sim.Width, sim.Height, timeoutMs, extra.ToString());
        }

        // A docked tab that is not in front does not render, and the game then
        // runs at the Editor's fallback size (640x480). Bring the play-mode view
        // to the front inside Unity (EditorWindow.Focus — the Unity application
        // itself is never raised). True when focus moved.
        private static bool BringToFront(EditorWindow view)
        {
            if (view == null || view.hasFocus) return false;
            view.Focus();
            return true;
        }

        // ------------------------------------------------------------------
        // Settle
        // ------------------------------------------------------------------

        private static string Finish(string action, EditorWindow view, int expectedW, int expectedH, int timeoutMs, string extra)
        {
            var sb = new StringBuilder(1536);
            sb.Append("{\"status\":\"ok\",\"action\":").Append(BridgeJson.EscapeString(action)).Append(',');
            AppendState(sb, view);
            sb.Append(',').Append(extra);
            sb.Append(",\"expected\":[").Append(expectedW).Append(',').Append(expectedH).Append(']');

            // A newer set_* supersedes a settle still watching the previous size.
            var running = PlayModeScheduler.RunningWithResource(GameViewSettleOperation.Resource);
            if (running != null) PlayModeScheduler.Cancel(running);

            if (!EditorApplication.isPlaying)
            {
                // Edit mode: the view's size is set synchronously; no game is running.
                sb.Append(",\"settled\":true,\"settle\":{\"state\":\"settled\",\"source\":\"edit_mode_view\"}}");
                return sb.ToString();
            }
            if (EditorApplication.isPaused)
            {
                sb.Append(",\"settled\":false,\"settle\":{\"state\":\"paused\",\"reason\":")
                  .Append(BridgeJson.EscapeString("Play mode is paused: the game observes the new size on its next frame. Unpause (editor_set_state play) or advance frames (inputsim_step), then call get."))
                  .Append("}}");
                return sb.ToString();
            }
            var op = new GameViewSettleOperation(expectedW, expectedH, timeoutMs);
            var snapshot = PlayModeScheduler.Start(op, null, out var error);
            if (snapshot == null)
            {
                sb.Append(",\"settled\":false,\"settle\":{\"state\":\"failed\",\"error\":").Append(ExtractError(error)).Append("}}");
                return sb.ToString();
            }
            sb.Append(",\"settled\":null,\"settle\":{\"state\":\"pending\",\"timeout_ms\":").Append(timeoutMs);
            sb.Append(",\"operation\":").Append(snapshot).Append("}}");
            return sb.ToString();
        }

        private static string ExtractError(string errorJson)
        {
            int start = errorJson?.IndexOf("{\"code\"", StringComparison.Ordinal) ?? -1;
            if (start < 0) return BridgeJson.EscapeString(errorJson ?? "");
            return errorJson.Substring(start, errorJson.Length - start - 1);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static int ParseCursor(string cursor)
        {
            if (string.IsNullOrEmpty(cursor)) return 0;
            int colon = cursor.LastIndexOf(':');
            if (colon < 0 || colon >= cursor.Length - 1) return 0;
            if (cursor.Substring(0, colon) != DeviceCursorPrefix) return 0;
            return int.TryParse(cursor.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;
        }

        internal static void AppendRect(StringBuilder sb, Rect r)
        {
            sb.Append('[').Append(Num(r.x)).Append(',').Append(Num(r.y)).Append(',')
              .Append(Num(r.width)).Append(',').Append(Num(r.height)).Append(']');
        }

        internal static string Num(double v) => PlayModeScheduler.Num(v);

        private static string JsonArray(List<string> values)
        {
            var sb = new StringBuilder(64).Append('[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(values[i]));
            }
            return sb.Append(']').ToString();
        }

        internal static string Error(string code, string message, string extraFields = null) =>
            PlayModeScheduler.ErrorJson(code, message, extraFields);

        private static string Unavailable(string code, string message)
        {
            var missing = GameViewAdapter.MissingMembers();
            var detected = new StringBuilder(128);
            detected.Append("\"detected\":{\"unityVersion\":").Append(BridgeJson.EscapeString(GameViewAdapter.UnityVersion));
            if (code == "editor_internals_unavailable") detected.Append(",\"missing\":").Append(JsonArray(missing));
            detected.Append('}');
            var sb = new StringBuilder(PlayModeScheduler.ErrorJson(code, message, detected.ToString()));
            sb.Length -= 1; // reopen the outer object for agentNextSteps
            sb.Append(",\"agentNextSteps\":[").Append(BridgeJson.EscapeString(
                "Set the Game view size or Simulator device by hand in the Editor; captures still work at whatever size the Game view has.")).Append("]}");
            return sb.ToString();
        }
    }

    // Watches the game's Screen size inside the player loop until it — and the
    // view's render target — report the expected size for two consecutive frames.
    internal sealed class GameViewSettleOperation : PlayModeOperation
    {
        internal const string Resource = "game_view";
        internal const int RequiredFrames = 2;

        private readonly int expectedWidth, expectedHeight;
        private readonly double limitMs;
        private int matched;
        private bool settled;
        private int lastWidth, lastHeight, lastFrame;
        private int textureWidth, textureHeight;
        private bool lastVisible = true;

        public GameViewSettleOperation(int width, int height, double limitMs)
        {
            expectedWidth = width;
            expectedHeight = height;
            this.limitMs = limitMs;
            ExpectedDurationMs = 100;
            TimeBase = PlayModeTimeBase.Realtime;
            Phase = "waiting for the game to report the new size";
        }

        public override string Kind => GameViewTools.ToolName;
        public override string ExclusiveResource => Resource;

        protected internal override bool Tick(PlayModeFrame frame)
        {
            var view = GameViewAdapter.MainView();
            var rt = GameViewAdapter.TargetTexture(view);
            return Observe(frame.ScreenWidth, frame.ScreenHeight, rt != null ? rt.width : 0, rt != null ? rt.height : 0,
                frame.RealtimeMs, frame.FrameCount, GameViewAdapter.IsFrontTab(view) != false);
        }

        // Pure step (unit-tested): true when done (settled or out of time). A
        // view behind another tab never counts: its render target is a stale frame.
        internal bool Observe(int screenWidth, int screenHeight, int rtWidth, int rtHeight, double elapsedMs, int frameCount = 0, bool viewVisible = true)
        {
            lastWidth = screenWidth;
            lastHeight = screenHeight;
            lastFrame = frameCount;
            textureWidth = rtWidth;
            textureHeight = rtHeight;
            lastVisible = viewVisible;
            bool ok = viewVisible && screenWidth == expectedWidth && screenHeight == expectedHeight &&
                      rtWidth == expectedWidth && rtHeight == expectedHeight;
            matched = ok ? matched + 1 : 0;
            if (matched >= RequiredFrames)
            {
                settled = true;
                return true;
            }
            return elapsedMs >= limitMs;
        }

        internal bool Settled => settled;

        protected internal override void Cleanup(PlayModeEndReason reason) { }

        protected internal override string BuildResult(PlayModeEndReason reason, string error)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"settled\":").Append(settled ? "true" : "false");
            sb.Append(",\"expected\":[").Append(expectedWidth).Append(',').Append(expectedHeight).Append(']');
            sb.Append(",\"observed\":{\"screen\":[").Append(lastWidth).Append(',').Append(lastHeight).Append(']');
            sb.Append(",\"render_texture\":[").Append(textureWidth).Append(',').Append(textureHeight).Append(']');
            sb.Append(",\"frame\":").Append(lastFrame);
            if (!lastVisible) sb.Append(",\"visible\":false");
            sb.Append('}');
            if (!settled)
                sb.Append(",\"reason\":").Append(BridgeJson.EscapeString(
                    reason == PlayModeEndReason.Completed
                        ? "The game did not report the new size in time. A Game view tab that is not visible does not render; bring it to the front (or close the tab covering it) and call get."
                        : "Settle watch ended: " + PlayModeScheduler.ReasonName(reason) + "."));
            if (error != null) sb.Append(",\"error\":").Append(BridgeJson.EscapeString(error));
            sb.Append('}');
            return sb.ToString();
        }
    }
}
