// Playtest — UI screen snapshot (unity_open_mcp_ui_snapshot).
//
// A read-only, gate-free accessibility-tree-like listing of the uGUI on screen,
// in edit and play mode: role, text (UI.Text; TextMeshPro when its reader is
// compiled in), screen rect, visibility (on_screen | clipped | offscreen |
// transparent | zero_size), interactable, occluded_by, sprite, depth — plus a
// diagnostics block (text overflow, zero-size controls, controls and texts
// outside the safe area and overlapping controls always; placeholder pattern
// and minimum touch size on request). The node model is shared with
// label targeting (`target_text`) so what the snapshot shows is what a tap by
// label resolves to. inputsim_probe stays the cheap interactables-only listing.
//
// Profiles: compact (default) = visible nodes that have text or are controls,
// labels folded into their control, null keys omitted; balanced = every visible
// node except folded labels, plus sprite/depth; full = every active node in any
// visibility state with every key. Paging: page_size + cursor (prefix
// "ui_snapshot"). Diagnostics are computed over the whole `under` scope and
// emitted on the first page only; a placeholder_pattern that times out refuses
// the call with regex_timeout. `reason` accompanies a visibility geometry does
// not explain (no_camera).
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
            "overflow / zero-size / safe-area / overlap / placeholder / touch-size diagnostics. Read-only; edit and play mode.")]
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
                    placeholder = new Regex(placeholder_pattern, RegexOptions.CultureInvariant, UiRegexRun.PerText);
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

            var safeArea = ScreenSafeArea(screen);
            Diagnostics diagnostics = null;
            if (offset == 0)
            {
                diagnostics = Diagnose(inScope, placeholder, min_touch_size, screen, safeArea);
                if (diagnostics.Placeholder != null && diagnostics.Placeholder.TimedOut)
                    return diagnostics.Placeholder.ErrorJson("", "placeholder_pattern");
            }

            var sb = new StringBuilder(4096);
            sb.Append("{\"status\":\"ok\"");
            sb.Append(",\"mode\":").Append(EditorApplication.isPlaying ? "\"play\"" : "\"edit\"");
            sb.Append(",\"profile\":").Append(InputSimulationJson.Esc(profile));
            AppendScreen(sb, screen, safeArea);
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
            if (diagnostics != null)
            {
                sb.Append(",\"diagnostics\":");
                AppendDiagnostics(sb, diagnostics, placeholder_pattern, min_touch_size);
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

        // The safe area the game sees, in Screen pixels (bottom-left origin).
        internal static Rect ScreenSafeArea(Vector2 screen)
        {
            int w = Mathf.RoundToInt(screen.x), h = Mathf.RoundToInt(screen.y);
            Rect safe;
            // The Game view / Simulator adapter knows the safe area the game sees
            // (the Device Simulator's, or the whole view in the Game view).
            if (UnityOpenMcpBridge.Playtest.GameViewAdapter.TryGameScreen(out _, out _, out int gw, out int gh, out var gameSafe, out _)
                && gw == w && gh == h)
                safe = gameSafe;
            else
            {
                // Screen.safeArea read from Editor code describes the current GUIView;
                // trust it only when it fits the Game view size, else report the full view.
                safe = Screen.safeArea;
                bool fits = safe.width > 0 && safe.height > 0 && safe.xMin >= 0 && safe.yMin >= 0
                            && safe.xMax <= w + 0.5f && safe.yMax <= h + 0.5f
                            && Screen.width == w && Screen.height == h;
                if (!fits) safe = new Rect(0, 0, w, h);
            }
            return safe;
        }

        private static void AppendScreen(StringBuilder sb, Vector2 screen, Rect safe)
        {
            int w = Mathf.RoundToInt(screen.x), h = Mathf.RoundToInt(screen.y);
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
            if (n.Reason != null) sb.Append(",\"reason\":").Append(InputSimulationJson.Esc(n.Reason));
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

        private sealed class Diagnostics
        {
            public readonly List<UiNode> Overflow = new List<UiNode>();
            public readonly List<UiNode> Zero = new List<UiNode>();
            public readonly List<UiNode> OutsideSafe = new List<UiNode>();
            public readonly List<UiOverlap> Overlaps = new List<UiOverlap>();
            public readonly List<UiNode> Placeholders = new List<UiNode>();
            public readonly List<UiNode> Small = new List<UiNode>();
            public UiRegexRun Placeholder;   // null unless placeholder_pattern was passed
            public Rect SafeArea;
            public bool SafeAreaIsScreen;
        }

        internal struct UiOverlap
        {
            public UiNode A, B;
            public float Fraction;           // intersection / the smaller visible rect
        }

        // Partial overlap of two controls a finger cannot tell apart: at least this
        // share of the smaller one's visible rect.
        internal const float OverlapThreshold = 0.25f;
        // Above this share the smaller control sits inside the larger one (a button
        // on a full-screen click catcher, a badge on a card) — layering, not a clash.
        internal const float ContainedFraction = 0.98f;
        private const float SafeEpsilon = 0.5f;
        // A rect reaching this far or less past the safe area is rounding (a text
        // box's padding), not content under the notch.
        internal const float SafeTolerancePx = 2f;
        // An element covering this share of the screen is a surface — a swipe area,
        // a dimmer, a full-screen tap catcher — not content placed under the notch.
        internal const float FullScreenFraction = 0.9f;

        // Stops matching the placeholder pattern at its first timeout
        // (Placeholder.TimedOut); the caller refuses with regex_timeout.
        private static Diagnostics Diagnose(List<UiNode> nodes, Regex placeholder, float? minTouch, Vector2 screen, Rect safe)
        {
            var d = new Diagnostics { Placeholder = placeholder == null ? null : new UiRegexRun(placeholder), SafeArea = safe };
            d.SafeAreaIsScreen = SafeAreaCoversScreen(safe, screen);
            foreach (var n in nodes)
            {
                if (n.OwnText.HasValue && n.OwnText.Value.Overflowing && !string.IsNullOrEmpty(n.OwnTextDisplay)) d.Overflow.Add(n);
                if (n.Control && n.Visibility == "zero_size") d.Zero.Add(n);
                if (!d.SafeAreaIsScreen && OutsideSafeArea(n, safe) && !CoversScreen(n.VisibleRect, screen)) d.OutsideSafe.Add(n);
                if (d.Placeholder != null && n.OwnText.HasValue && !string.IsNullOrEmpty(n.OwnTextDisplay))
                {
                    if (d.Placeholder.IsMatch(n.OwnTextDisplay)) d.Placeholders.Add(n);
                    if (d.Placeholder.TimedOut) return d;
                }
                // A slider without a handle (a progress bar) or one the player cannot use is no touch target.
                if (minTouch.HasValue && n.Control && n.Visible && (n.Role != "slider" || (n.Interactable && n.Draggable))
                    && (n.ScreenRect.width < minTouch.Value || n.ScreenRect.height < minTouch.Value))
                    d.Small.Add(n);
            }
            d.Overlaps.AddRange(Overlaps(nodes, UiOcclusion.Available ? n => UiOcclusion.OccluderPath(n) != null : (System.Func<UiNode, bool>)null));
            return d;
        }

        internal static bool CoversScreen(Rect r, Vector2 screen) =>
            screen.x > 0 && screen.y > 0
            && Mathf.Max(0, Mathf.Min(r.xMax, screen.x) - Mathf.Max(r.xMin, 0)) * Mathf.Max(0, Mathf.Min(r.yMax, screen.y) - Mathf.Max(r.yMin, 0))
               >= FullScreenFraction * screen.x * screen.y;

        internal static bool SafeAreaCoversScreen(Rect safe, Vector2 screen) =>
            safe.xMin <= SafeEpsilon && safe.yMin <= SafeEpsilon
            && safe.xMax >= screen.x - SafeEpsilon && safe.yMax >= screen.y - SafeEpsilon;

        // A visible control, or a visible text that is not part of a control's
        // label, whose on-screen part reaches outside the safe area (under a notch,
        // a rounded corner or the home indicator).
        internal static bool OutsideSafeArea(UiNode n, Rect safe)
        {
            if (!n.Visible || n.Folded) return false;
            bool ownText = n.OwnText.HasValue && !string.IsNullOrEmpty(n.OwnTextDisplay);
            if (!n.Control && !ownText) return false;
            return OutsideEdges(n.VisibleRect, safe) != null;
        }

        // The safe-area edges the rect crosses ("top", "bottom", "left", "right"), or null.
        internal static List<string> OutsideEdges(Rect r, Rect safe)
        {
            if (r.width <= 0 || r.height <= 0) return null;
            List<string> edges = null;
            foreach (var (edge, px) in Overshoot(r, safe))
                if (px > SafeTolerancePx) (edges ??= new List<string>()).Add(edge);
            return edges;
        }

        // How far (px) the rect reaches past each safe-area edge (positive only).
        internal static List<(string edge, float px)> Overshoot(Rect r, Rect safe)
        {
            var list = new List<(string, float)>();
            if (r.yMax - safe.yMax > SafeEpsilon) list.Add(("top", r.yMax - safe.yMax));
            if (safe.yMin - r.yMin > SafeEpsilon) list.Add(("bottom", safe.yMin - r.yMin));
            if (safe.xMin - r.xMin > SafeEpsilon) list.Add(("left", safe.xMin - r.xMin));
            if (r.xMax - safe.xMax > SafeEpsilon) list.Add(("right", r.xMax - safe.xMax));
            return list;
        }

        // Pairs of visible interactable controls, neither inside the other in the
        // hierarchy, whose visible rects partly overlap (OverlapThreshold of the
        // smaller one, short of containing it). In play mode a control covered by
        // something in front of it (a popup's dimmer) is behind the screen the
        // player sees and does not clash with what is on top.
        internal static List<UiOverlap> Overlaps(List<UiNode> nodes, System.Func<UiNode, bool> covered = null)
        {
            var controls = new List<UiNode>();
            foreach (var n in nodes)
                if (n.Control && n.Interactable && n.Visible && n.VisibleRect.width > 0 && n.VisibleRect.height > 0
                    && (covered == null || !covered(n))) controls.Add(n);
            var found = new List<UiOverlap>();
            for (int i = 0; i < controls.Count; i++)
            {
                var a = controls[i];
                float areaA = a.VisibleRect.width * a.VisibleRect.height;
                for (int j = i + 1; j < controls.Count; j++)
                {
                    var b = controls[j];
                    if (a.Rt.IsChildOf(b.Rt) || b.Rt.IsChildOf(a.Rt)) continue;
                    var inter = UiScreenModel.Intersect(a.VisibleRect, b.VisibleRect);
                    if (inter.width <= 0 || inter.height <= 0) continue;
                    float smaller = Mathf.Min(areaA, b.VisibleRect.width * b.VisibleRect.height);
                    if (smaller <= 0) continue;
                    float fraction = inter.width * inter.height / smaller;
                    if (fraction < OverlapThreshold || fraction >= ContainedFraction) continue;
                    found.Add(new UiOverlap { A = a, B = b, Fraction = fraction });
                }
            }
            return found;
        }

        private static void AppendDiagnostics(StringBuilder sb, Diagnostics d, string pattern, float? minTouch)
        {
            var overflow = d.Overflow;
            var zero = d.Zero;
            var placeholders = d.Placeholders;
            var small = d.Small;

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
            sb.Append(",\"outside_safe_area\":");
            AppendDiagnosticList(sb, d.OutsideSafe, (b, n) =>
            {
                b.Append(",\"role\":").Append(InputSimulationJson.Esc(n.Role));
                if (n.Text != null) b.Append(",\"text\":").Append(InputSimulationJson.Esc(UiText.Clip(n.Text, 80)));
                b.Append(",\"rect\":");
                AppendRect(b, n.VisibleRect);
                b.Append(",\"edges\":[");
                var edges = OutsideEdges(n.VisibleRect, d.SafeArea);
                for (int i = 0; edges != null && i < edges.Count; i++)
                {
                    if (i > 0) b.Append(',');
                    b.Append(InputSimulationJson.Esc(edges[i]));
                }
                b.Append(']');
                b.Append(",\"overshoot_px\":{");
                bool first = true;
                foreach (var (edge, px) in Overshoot(n.VisibleRect, d.SafeArea))
                {
                    if (px <= SafeTolerancePx) continue;
                    if (!first) b.Append(',');
                    first = false;
                    b.Append(InputSimulationJson.Esc(edge)).Append(':').Append(InputSimulationJson.Num(Mathf.Round(px)));
                }
                b.Append('}');
            }, b =>
            {
                b.Append(",\"safe_area\":");
                AppendRect(b, d.SafeArea);
                b.Append(",\"tolerance_px\":").Append(InputSimulationJson.Num(SafeTolerancePx));
            });
            sb.Append(",\"overlapping_controls\":");
            AppendOverlapList(sb, d.Overlaps);
            if (d.Placeholder != null)
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

        private static void AppendOverlapList(StringBuilder sb, List<UiOverlap> list)
        {
            sb.Append("{\"count\":").Append(list.Count);
            sb.Append(",\"threshold\":").Append(InputSimulationJson.Num(OverlapThreshold));
            sb.Append(",\"items\":[");
            int n = Mathf.Min(list.Count, MaxDiagnosticItems);
            for (int i = 0; i < n; i++)
            {
                var o = list[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(InstanceId.ToJson(o.A.Go));
                sb.Append(",\"path\":").Append(InputSimulationJson.Esc(o.A.Path));
                sb.Append(",\"visibility\":").Append(InputSimulationJson.Esc(o.A.Visibility));
                if (o.A.Text != null) sb.Append(",\"text\":").Append(InputSimulationJson.Esc(UiText.Clip(o.A.Text, 80)));
                sb.Append(",\"with_id\":").Append(InstanceId.ToJson(o.B.Go));
                sb.Append(",\"with\":").Append(InputSimulationJson.Esc(o.B.Path));
                if (o.B.Text != null) sb.Append(",\"with_text\":").Append(InputSimulationJson.Esc(UiText.Clip(o.B.Text, 80)));
                sb.Append(",\"overlap\":").Append(InputSimulationJson.Num(Mathf.Round(o.Fraction * 100f) / 100f));
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
