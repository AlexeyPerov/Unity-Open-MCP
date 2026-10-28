// Playtest — UI screen snapshot (unity_open_mcp_ui_snapshot).
//
// A read-only, gate-free accessibility-tree-like listing of the uGUI on screen,
// in edit and play mode: role, text (UI.Text; TextMeshPro when its reader is
// compiled in), screen rect, visibility (on_screen | clipped | offscreen |
// transparent | zero_size), interactable, occluded_by, sprite, depth — plus a
// diagnostics block (text overflow and zero-size controls always; placeholder
// pattern and minimum touch size on request). The node model is shared with
// label targeting (`target_text`) so what the snapshot shows is what a tap by
// label resolves to. inputsim_probe stays the cheap interactables-only listing.
//
// Profiles: compact (default) = visible nodes that have text or are controls,
// labels folded into their control, null keys omitted; balanced = every visible
// node except folded labels, plus sprite/depth; full = every active node in any
// visibility state with every key. Paging: page_size + cursor (prefix
// "ui_snapshot"). Diagnostics are computed over the whole `under` scope and
// emitted on the first page only.
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    [BridgeToolType]
    public static class UiSnapshotTools
    {
        internal const string ToolName = "unity_open_mcp_ui_snapshot";
        private const string CursorPrefix = "ui_snapshot";
        internal const int DefaultPageSize = 50;
        internal const int MaxPageSize = 200;
        internal const int MaxDiagnosticItems = 20;
        private static readonly string[] Roles = { "button", "toggle", "slider", "input", "scroll", "text", "image", "container" };

        [BridgeTool(ToolName,
            Title = "Playtest: UI Snapshot",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = true,
            IdempotentHint = true,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "playtest")]
        [System.ComponentModel.Description(
            "Snapshot the visible uGUI as a flat accessibility tree: role, text (UI.Text and " +
            "TextMeshPro), screen rect, visibility, interactable, occluded_by, sprite, depth, plus " +
            "overflow / zero-size / placeholder / touch-size diagnostics. Read-only; edit and play mode.")]
        public static string Snapshot(
            string profile = "compact",
            int page_size = DefaultPageSize,
            string cursor = null,
            string text_contains = null,
            string role = null,
            string under = null,
            bool? visible_only = null,
            string placeholder_pattern = null,
            float? min_touch_size = null)
        {
            profile = string.IsNullOrEmpty(profile) ? "compact" : profile;
            if (profile != "compact" && profile != "balanced" && profile != "full")
                return InputSimulationJson.Error("invalid_arguments", "profile must be compact, balanced or full.");
            if (page_size < 1) page_size = 1;
            if (page_size > MaxPageSize) page_size = MaxPageSize;
            int offset = ParseCursor(cursor);

            HashSet<string> roleFilter = null;
            if (!string.IsNullOrEmpty(role))
            {
                roleFilter = new HashSet<string>();
                foreach (var part in role.Split(','))
                {
                    var r = part.Trim();
                    if (r.Length == 0) continue;
                    if (System.Array.IndexOf(Roles, r) < 0)
                        return InputSimulationJson.Error("invalid_arguments",
                            $"Unknown role '{r}'. Valid: {string.Join(", ", Roles)}.");
                    roleFilter.Add(r);
                }
            }

            Regex placeholder = null;
            if (!string.IsNullOrEmpty(placeholder_pattern))
            {
                try
                {
                    placeholder = new Regex(placeholder_pattern, RegexOptions.CultureInvariant,
                        System.TimeSpan.FromMilliseconds(100));
                }
                catch (System.ArgumentException ex)
                {
                    return InputSimulationJson.Error("invalid_arguments", "Invalid placeholder_pattern: " + ex.Message);
                }
            }
            if (min_touch_size.HasValue && !(min_touch_size.Value > 0))
                return InputSimulationJson.Error("invalid_arguments", "min_touch_size must be a positive number of screen pixels.");

            var screen = GameViewWindows.MainGameViewSize();
            var nodes = UiScreenModel.Collect(screen);
            var scope = UiScope.Build(nodes, under);
            if (!string.IsNullOrEmpty(under) && !scope.Any)
                return InputSimulationJson.Error("under_not_found",
                    $"No active uGUI element path matches under '{under}'.");

            bool visibleOnly = visible_only ?? profile != "full";
            string needle = string.IsNullOrEmpty(text_contains) ? null : UiText.Normalize(text_contains).ToLowerInvariant();

            var inScope = new List<UiNode>(nodes.Count);
            var selected = new List<UiNode>();
            foreach (var n in nodes)
            {
                if (!scope.Contains(n)) continue;
                inScope.Add(n);
                if (!Selected(n, profile)) continue;
                if (visibleOnly && !n.Visible) continue;
                if (roleFilter != null && !roleFilter.Contains(n.Role)) continue;
                if (needle != null && (n.Text == null || !n.Text.ToLowerInvariant().Contains(needle))) continue;
                selected.Add(n);
            }

            int total = selected.Count;
            if (offset > total) offset = total;
            int end = Mathf.Min(offset + page_size, total);
            int remaining = total - end;
            bool occlusion = UiOcclusion.Available;

            var sb = new StringBuilder(4096);
            sb.Append("{\"status\":\"ok\"");
            sb.Append(",\"mode\":").Append(EditorApplication.isPlaying ? "\"play\"" : "\"edit\"");
            sb.Append(",\"profile\":").Append(InputSimulationJson.Esc(profile));
            AppendScreen(sb, screen);
            sb.Append(",\"textSources\":[");
            var sources = UiTextReaders.Sources();
            for (int i = 0; i < sources.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(InputSimulationJson.Esc(sources[i]));
            }
            sb.Append(']');
            sb.Append(",\"occlusion\":").Append(occlusion ? "\"play_mode_raycast\"" : "\"unavailable\"");
            sb.Append(",\"visibleOnly\":").Append(visibleOnly ? "true" : "false");
            sb.Append(",\"scanned\":").Append(inScope.Count);
            sb.Append(",\"total\":").Append(total);
            sb.Append(",\"nodes\":[");
            for (int i = offset; i < end; i++)
            {
                if (i > offset) sb.Append(',');
                AppendNode(sb, selected[i], profile, occlusion);
            }
            sb.Append(']');
            if (offset == 0)
            {
                sb.Append(",\"diagnostics\":");
                AppendDiagnostics(sb, inScope, placeholder, placeholder_pattern, min_touch_size);
            }
            AppendPagination(sb, offset, page_size, remaining);
            sb.Append('}');
            return sb.ToString();
        }

        // compact: visible text/control nodes (labels folded into their control);
        // balanced: every node except folded labels; full: every node.
        internal static bool Selected(UiNode n, string profile)
        {
            if (profile == "full") return true;
            if (n.FoldedIntoOwner) return false;
            if (profile == "balanced") return true;
            return n.HasText || n.Interactive;
        }

        private static void AppendScreen(StringBuilder sb, Vector2 screen)
        {
            int w = Mathf.RoundToInt(screen.x), h = Mathf.RoundToInt(screen.y);
            // Screen.safeArea read from Editor code describes the current GUIView;
            // trust it only when it fits the Game view size, else report the full view.
            var safe = Screen.safeArea;
            bool fits = safe.width > 0 && safe.height > 0 && safe.xMin >= 0 && safe.yMin >= 0
                        && safe.xMax <= w + 0.5f && safe.yMax <= h + 0.5f
                        && Screen.width == w && Screen.height == h;
            if (!fits) safe = new Rect(0, 0, w, h);
            sb.Append(",\"screen\":{\"w\":").Append(w).Append(",\"h\":").Append(h);
            sb.Append(",\"safe_area\":");
            AppendRect(sb, safe);
            sb.Append(",\"dpi\":").Append(InputSimulationJson.Num(Screen.dpi));
            sb.Append('}');
        }

        private static void AppendNode(StringBuilder sb, UiNode n, string profile, bool occlusion)
        {
            bool full = profile == "full";
            bool balanced = profile == "balanced";
            sb.Append("{\"id\":").Append(InstanceId.ToJson(n.Go));
            sb.Append(",\"path\":").Append(InputSimulationJson.Esc(n.Path));
            sb.Append(",\"role\":").Append(InputSimulationJson.Esc(n.Role));
            var text = n.Text;
            if (text != null) sb.Append(",\"text\":").Append(InputSimulationJson.Esc(UiText.Clip(text, full ? 2000 : 200)));
            else if (full) sb.Append(",\"text\":null");
            sb.Append(",\"rect\":");
            AppendRect(sb, n.ScreenRect);
            sb.Append(",\"visible\":").Append(n.Visible ? "true" : "false");
            sb.Append(",\"visibility\":").Append(InputSimulationJson.Esc(n.Visibility));
            if (n.Interactive) sb.Append(",\"interactable\":").Append(n.Interactable ? "true" : "false");
            else if (full) sb.Append(",\"interactable\":false");
            string occluder = occlusion ? UiOcclusion.OccluderPath(n) : null;
            if (occluder != null) sb.Append(",\"occluded_by\":").Append(InputSimulationJson.Esc(occluder));
            else if (full) sb.Append(",\"occluded_by\":null");
            if (full || balanced)
            {
                if (n.Sprite != null) sb.Append(",\"sprite\":").Append(InputSimulationJson.Esc(n.Sprite));
                else if (full) sb.Append(",\"sprite\":null");
                sb.Append(",\"depth\":").Append(n.Depth);
            }
            if (full)
            {
                sb.Append(",\"alpha\":").Append(InputSimulationJson.Num(n.Alpha));
                sb.Append(",\"owner\":").Append(n.Owner == null ? "null" : InputSimulationJson.Esc(n.Owner.Path));
                sb.Append(",\"textSource\":").Append(n.OwnText.HasValue ? InputSimulationJson.Esc(n.OwnText.Value.Source) : "null");
            }
            sb.Append('}');
        }

        private static void AppendDiagnostics(StringBuilder sb, List<UiNode> nodes, Regex placeholder,
            string pattern, float? minTouch)
        {
            var overflow = new List<UiNode>();
            var zero = new List<UiNode>();
            var placeholders = new List<UiNode>();
            var small = new List<UiNode>();
            foreach (var n in nodes)
            {
                if (n.OwnText.HasValue && n.OwnText.Value.Overflowing && !string.IsNullOrEmpty(n.OwnTextDisplay)) overflow.Add(n);
                if (n.Control && n.Visibility == "zero_size") zero.Add(n);
                if (placeholder != null && n.OwnText.HasValue && !string.IsNullOrEmpty(n.OwnTextDisplay) && SafeMatch(placeholder, n.OwnTextDisplay))
                    placeholders.Add(n);
                if (minTouch.HasValue && n.Control && n.Visible
                    && (n.ScreenRect.width < minTouch.Value || n.ScreenRect.height < minTouch.Value))
                    small.Add(n);
            }

            sb.Append("{\"overflow\":");
            AppendDiagnosticList(sb, overflow, (b, n) =>
            {
                var t = n.OwnText.Value;
                b.Append(",\"text\":").Append(InputSimulationJson.Esc(UiText.Clip(n.OwnTextDisplay, 80)));
                b.Append(",\"source\":").Append(InputSimulationJson.Esc(t.Source));
                b.Append(",\"size\":[").Append(InputSimulationJson.Num(n.Rt.rect.width)).Append(',').Append(InputSimulationJson.Num(n.Rt.rect.height)).Append(']');
                b.Append(",\"content\":[").Append(InputSimulationJson.Num(t.Content.x)).Append(',').Append(InputSimulationJson.Num(t.Content.y)).Append(']');
            }, null);
            sb.Append(",\"zero_size_interactables\":");
            AppendDiagnosticList(sb, zero, (b, n) =>
            {
                b.Append(",\"role\":").Append(InputSimulationJson.Esc(n.Role));
                b.Append(",\"rect\":");
                AppendRect(b, n.ScreenRect);
            }, null);
            if (placeholder != null)
            {
                sb.Append(",\"placeholder\":");
                AppendDiagnosticList(sb, placeholders, (b, n) =>
                    b.Append(",\"text\":").Append(InputSimulationJson.Esc(UiText.Clip(n.OwnTextDisplay, 80))),
                    b => b.Append(",\"pattern\":").Append(InputSimulationJson.Esc(pattern)));
            }
            if (minTouch.HasValue)
            {
                sb.Append(",\"small_touch_targets\":");
                AppendDiagnosticList(sb, small, (b, n) =>
                {
                    b.Append(",\"role\":").Append(InputSimulationJson.Esc(n.Role));
                    b.Append(",\"size\":[").Append(InputSimulationJson.Num(n.ScreenRect.width)).Append(',').Append(InputSimulationJson.Num(n.ScreenRect.height)).Append(']');
                }, b =>
                {
                    b.Append(",\"min_touch_size\":").Append(InputSimulationJson.Num(minTouch.Value));
                    b.Append(",\"dpi\":").Append(InputSimulationJson.Num(Screen.dpi));
                });
            }
            sb.Append('}');
        }

        private static bool SafeMatch(Regex rx, string s)
        {
            try { return rx.IsMatch(s); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        private static void AppendDiagnosticList(StringBuilder sb, List<UiNode> list,
            System.Action<StringBuilder, UiNode> extra, System.Action<StringBuilder> header)
        {
            sb.Append("{\"count\":").Append(list.Count);
            header?.Invoke(sb);
            sb.Append(",\"items\":[");
            int n = Mathf.Min(list.Count, MaxDiagnosticItems);
            for (int i = 0; i < n; i++)
            {
                var node = list[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(InstanceId.ToJson(node.Go));
                sb.Append(",\"path\":").Append(InputSimulationJson.Esc(node.Path));
                sb.Append(",\"visibility\":").Append(InputSimulationJson.Esc(node.Visibility));
                extra(sb, node);
                sb.Append('}');
            }
            sb.Append("],\"truncated\":").Append(list.Count - n).Append('}');
        }

        internal static void AppendRect(StringBuilder sb, Rect r) => UiSnapshotJson.AppendRect(sb, r);

        private static void AppendPagination(StringBuilder sb, int offset, int pageSize, int remaining)
        {
            sb.Append(",\"pagination\":{\"page_size\":").Append(pageSize);
            sb.Append(",\"cursor\":");
            if (offset > 0) sb.Append('"').Append(CursorPrefix).Append(':').Append(offset.ToString(CultureInfo.InvariantCulture)).Append('"');
            else sb.Append("null");
            sb.Append(",\"next_cursor\":");
            if (remaining > 0) sb.Append('"').Append(CursorPrefix).Append(':').Append((offset + pageSize).ToString(CultureInfo.InvariantCulture)).Append('"');
            else sb.Append("null");
            sb.Append(",\"truncated\":").Append(remaining).Append('}');
        }

        private static int ParseCursor(string cursor)
        {
            if (string.IsNullOrEmpty(cursor)) return 0;
            int colon = cursor.LastIndexOf(':');
            if (colon < 0 || colon >= cursor.Length - 1) return 0;
            if (cursor.Substring(0, colon) != CursorPrefix) return 0;
            return int.TryParse(cursor.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;
        }
    }

    internal static class UiSnapshotJson
    {
        // [x, y, w, h] in Screen pixels, bottom-left origin, rounded to 0.1 px.
        internal static void AppendRect(StringBuilder sb, Rect r)
        {
            sb.Append('[').Append(Px(r.x)).Append(',').Append(Px(r.y)).Append(',')
              .Append(Px(r.width)).Append(',').Append(Px(r.height)).Append(']');
        }

        private static string Px(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "0";
            return v.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
