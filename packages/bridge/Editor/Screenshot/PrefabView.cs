// The `prefab` view of unity_senses_screenshot: a UI prefab (or a canvas from a
// loaded scene) rendered in edit mode the way the game draws it, without
// touching a project scene.
//
// Screen Space - Overlay canvases are drawn by the player loop, not by a
// camera, so in edit mode no capture sees them. Agents used to open a scratch
// scene additively, instantiate the prefab under a canvas, render a camera and
// unload the scene again — dirtying or unloading project scenes on the way.
// This view instead:
//
//   1. clones the prefab (or the scene object) into a preview scene
//      (EditorSceneManager.NewPreviewScene — never listed by SceneManager,
//      never saved);
//   2. renders it through a Screen Space - Camera canvas bound to a temporary
//      camera of that preview scene: the prefab's own root canvas switched to
//      that mode, or — for a panel without a Canvas — a temporary canvas around
//      it whose CanvasScaler matches the project (see ResolveScaler);
//   3. forces the canvas update and a layout rebuild before the one render,
//      so layout groups, content size fitters and TextMesh Pro text are right
//      on the first frame;
//   4. destroys every object and closes the preview scene in a finally block,
//      also when the render throws.
//
// No project scene is loaded, unloaded, dirtied or changed and no asset is
// written (the clone is never saved), so the dirty-scene guard and the gate do
// not apply (lifecycle None, like every screenshot). Scripts marked
// [ExecuteAlways] / [ExecuteInEditMode] still run their edit-mode callbacks on
// the clone, as they would in any scene. Play mode is refused: there every
// script of the clone would wake up (Awake / OnEnable) inside the running game.
//
// CanvasScaler / LayoutRebuilder live in uGUI (com.unity.ugui), which the
// bridge root assembly cannot reference: the uGUI sub-assembly registers
// IUguiPreviewSupport (PrefabView.Ugui) at load, as it does for the playtest
// `ui` condition. Without uGUI the view answers ugui_unavailable.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityOpenMcpBridge.Playtest;
using UnityOpenMcpBridge.TypedTools;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Screenshot
{
    // Registered by the uGUI sub-assembly (com.unity.ugui present).
    internal interface IUguiPreviewSupport
    {
        /// <summary>The canvas's CanvasScaler settings; null when it has none.</summary>
        CanvasScalerSettings ReadScaler(Canvas canvas);

        /// <summary>Configure the canvas's CanvasScaler (added when missing).</summary>
        void ApplyScaler(Canvas canvas, CanvasScalerSettings settings);

        /// <summary>Rebuild every layout under <paramref name="root"/> now.</summary>
        void RebuildLayout(RectTransform root);
    }

    // The CanvasScaler fields that decide a canvas's scale, as plain data (the
    // bridge root assembly cannot reference CanvasScaler). Enum values use the
    // snake_case names of CanvasScaler's enums.
    internal sealed class CanvasScalerSettings
    {
        internal static readonly string[] ScaleModes = { "constant_pixel_size", "scale_with_screen_size", "constant_physical_size" };
        internal static readonly string[] MatchModes = { "match_width_or_height", "expand", "shrink" };
        internal static readonly string[] PhysicalUnits = { "centimeters", "millimeters", "inches", "points", "picas" };
        internal static readonly string[] Keys =
        {
            "ui_scale_mode", "scale_factor", "reference_resolution", "screen_match_mode", "match_width_or_height",
            "reference_pixels_per_unit", "physical_unit", "fallback_screen_dpi", "default_sprite_dpi",
        };

        // Defaults: a CanvasScaler as Unity adds it.
        public string UiScaleMode = "constant_pixel_size";
        public float ScaleFactor = 1f;
        public Vector2 ReferenceResolution = new Vector2(800f, 600f);
        public string ScreenMatchMode = "match_width_or_height";
        public float MatchWidthOrHeight;
        public float ReferencePixelsPerUnit = 100f;
        public string PhysicalUnit = "points";
        public float FallbackScreenDpi = 96f;
        public float DefaultSpriteDpi = 96f;

        public CanvasScalerSettings Clone() => (CanvasScalerSettings)MemberwiseClone();

        /// <summary>
        /// Apply the members of a canvas_scaler JSON object onto this instance;
        /// `applied` collects the keys set. Null when valid, else the message.
        /// Every value is checked before any is applied.
        /// </summary>
        internal string ApplyJson(string json, List<string> applied)
        {
            var members = JsonBody.GetTopLevelMembers(json);
            if (members == null)
                return "canvas_scaler must be an object, e.g. {\"ui_scale_mode\":\"scale_with_screen_size\",\"reference_resolution\":[1080,1920],\"match_width_or_height\":0.5}.";
            var next = Clone();
            foreach (var m in members)
            {
                var raw = m.Value;
                string error = null;
                switch (m.Key)
                {
                    case "ui_scale_mode": error = Enum(raw, ScaleModes, m.Key, out next.UiScaleMode); break;
                    case "screen_match_mode": error = Enum(raw, MatchModes, m.Key, out next.ScreenMatchMode); break;
                    case "physical_unit": error = Enum(raw, PhysicalUnits, m.Key, out next.PhysicalUnit); break;
                    case "scale_factor": error = Positive(raw, m.Key, out next.ScaleFactor); break;
                    case "reference_pixels_per_unit": error = Positive(raw, m.Key, out next.ReferencePixelsPerUnit); break;
                    case "fallback_screen_dpi": error = Positive(raw, m.Key, out next.FallbackScreenDpi); break;
                    case "default_sprite_dpi": error = Positive(raw, m.Key, out next.DefaultSpriteDpi); break;
                    case "match_width_or_height":
                        if (!WaitConditionParser.TryNumber(raw, out var match) || match < 0 || match > 1)
                            error = "canvas_scaler.match_width_or_height must be a number from 0 (width) to 1 (height).";
                        else next.MatchWidthOrHeight = (float)match;
                        break;
                    case "reference_resolution":
                        if (!FrameSchedule.TryNumbers(raw, out var v) || v.Count != 2 || v[0] <= 0 || v[1] <= 0)
                            error = "canvas_scaler.reference_resolution must be [width, height] in pixels, both > 0.";
                        else next.ReferenceResolution = new Vector2((float)v[0], (float)v[1]);
                        break;
                    default:
                        error = "Unknown canvas_scaler key '" + m.Key + "'. Valid: " + string.Join(", ", Keys) + ".";
                        break;
                }
                if (error != null) return error;
                if (!applied.Contains(m.Key)) applied.Add(m.Key);
            }
            CopyFrom(next);
            return null;
        }

        private void CopyFrom(CanvasScalerSettings o)
        {
            UiScaleMode = o.UiScaleMode; ScaleFactor = o.ScaleFactor; ReferenceResolution = o.ReferenceResolution;
            ScreenMatchMode = o.ScreenMatchMode; MatchWidthOrHeight = o.MatchWidthOrHeight;
            ReferencePixelsPerUnit = o.ReferencePixelsPerUnit; PhysicalUnit = o.PhysicalUnit;
            FallbackScreenDpi = o.FallbackScreenDpi; DefaultSpriteDpi = o.DefaultSpriteDpi;
        }

        private static string Enum(string raw, string[] valid, string key, out string value)
        {
            value = raw != null && raw.StartsWith("\"") ? JsonBody.GetString("{\"v\":" + raw + "}", "v") : null;
            if (value != null && Array.IndexOf(valid, value) >= 0) return null;
            value = null;
            return "canvas_scaler." + key + " must be one of: " + string.Join(", ", valid) + ".";
        }

        private static string Positive(string raw, string key, out float value)
        {
            value = 0f;
            if (!WaitConditionParser.TryNumber(raw, out var d) || d <= 0) return "canvas_scaler." + key + " must be a number > 0.";
            value = (float)d;
            return null;
        }

        // The mode's own fields (what a reader needs to reproduce the scale).
        internal void AppendJson(StringBuilder sb)
        {
            sb.Append("\"ui_scale_mode\":").Append(BridgeJson.EscapeString(UiScaleMode));
            if (UiScaleMode == "constant_pixel_size")
            {
                sb.Append(",\"scale_factor\":"); BridgeJson.AppendJsonNumber(sb, ScaleFactor);
            }
            else if (UiScaleMode == "scale_with_screen_size")
            {
                sb.Append(",\"reference_resolution\":["); BridgeJson.AppendJsonNumber(sb, ReferenceResolution.x);
                sb.Append(','); BridgeJson.AppendJsonNumber(sb, ReferenceResolution.y); sb.Append(']');
                sb.Append(",\"screen_match_mode\":").Append(BridgeJson.EscapeString(ScreenMatchMode));
                if (ScreenMatchMode == "match_width_or_height")
                {
                    sb.Append(",\"match_width_or_height\":"); BridgeJson.AppendJsonNumber(sb, MatchWidthOrHeight);
                }
            }
            else
            {
                sb.Append(",\"physical_unit\":").Append(BridgeJson.EscapeString(PhysicalUnit));
                sb.Append(",\"fallback_screen_dpi\":"); BridgeJson.AppendJsonNumber(sb, FallbackScreenDpi);
                sb.Append(",\"default_sprite_dpi\":"); BridgeJson.AppendJsonNumber(sb, DefaultSpriteDpi);
            }
            sb.Append(",\"reference_pixels_per_unit\":"); BridgeJson.AppendJsonNumber(sb, ReferencePixelsPerUnit);
        }
    }

    // The arguments of one prefab-view capture.
    internal sealed class PrefabViewRequest
    {
        public string AssetPath, GameObjectPath;
        public int? Width, Height;
        public string Device, Orientation, SizeLabel;
        public string CanvasScaler, ScalerFrom;
        public bool SafeAreaOverlay, Transparent;
    }

    internal static class PrefabView
    {
        internal const string Source = "preview_scene";
        internal const int MinSize = 16;
        internal const int MaxSize = 8192;
        private const int UiLayer = 5;
        private const float PlaneDistance = 10f;

        /// <summary>Registered by the uGUI sub-assembly; null without com.unity.ugui.</summary>
        internal static IUguiPreviewSupport Ugui;

        /// <summary>Test seam: runs after the preview scene is built, before the
        /// render (a throwing hook exercises the cleanup path).</summary>
        internal static Action<Scene> AfterBuildForTests;

        // Arguments that only the prefab view reads.
        internal static bool HasPrefabArgs(PrefabViewRequest r) =>
            r.AssetPath != null || r.GameObjectPath != null || r.Device != null || r.Orientation != null ||
            r.SizeLabel != null || r.CanvasScaler != null || r.ScalerFrom != null;

        /// <summary>Argument shape rules; null when valid.</summary>
        internal static string ValidateArgs(PrefabViewRequest r, out string code)
        {
            code = "validation_error";
            bool asset = !string.IsNullOrWhiteSpace(r.AssetPath), scene = !string.IsNullOrWhiteSpace(r.GameObjectPath);
            if (!asset && !scene)
            {
                code = "missing_parameter";
                return "view \"prefab\" needs asset_path (a UI prefab, e.g. \"Assets/UI/Shop.prefab\") or game_object_path (a canvas or panel in a loaded scene).";
            }
            if (asset && scene) return "Pass asset_path or game_object_path, not both.";
            bool size = r.Width.HasValue || r.Height.HasValue;
            if (r.Width.HasValue != r.Height.HasValue) return "Pass width and height together (or neither).";
            int given = (size ? 1 : 0) + (!string.IsNullOrWhiteSpace(r.Device) ? 1 : 0) + (!string.IsNullOrWhiteSpace(r.SizeLabel) ? 1 : 0);
            if (given > 1) return "Pass one screen size: width + height, device, or size_label.";
            if (size && (r.Width.Value < MinSize || r.Width.Value > MaxSize || r.Height.Value < MinSize || r.Height.Value > MaxSize))
                return "width and height must be " + MinSize + ".." + MaxSize + " pixels.";
            if (!string.IsNullOrWhiteSpace(r.Orientation))
            {
                if (string.IsNullOrWhiteSpace(r.Device)) return "orientation applies with device (a Device Simulator device); for a plain size swap width and height.";
                if (!GameViewAdapter.TryParseOrientation(r.Orientation, out _))
                    return "orientation must be portrait, portrait_upside_down, landscape_left or landscape_right.";
            }
            if (r.CanvasScaler != null)
            {
                var error = new CanvasScalerSettings().ApplyJson(r.CanvasScaler, new List<string>());
                if (error != null) return error;
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Capture
        // ------------------------------------------------------------------

        internal static FrameCapture Capture(PrefabViewRequest r)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new CaptureRefusedException("edit_mode_required",
                    "The prefab view renders in edit mode only: in play mode every script of the clone would wake up inside the running game. " +
                    "Capture the running screen with view \"composed\", or stop play mode first.");
            if (Ugui == null)
                throw new CaptureRefusedException("ugui_unavailable",
                    "The prefab view needs uGUI (com.unity.ugui) for CanvasScaler and layout: install it, or render the screen in play mode with view \"composed\".");

            var source = ResolveSource(r);
            var plan = PlanCanvases(source.Template, source.Label);
            var screen = ResolveScreen(r);
            var scaler = ResolveScaler(r, source, plan);
            return Render(source, plan, screen, scaler, r);
        }

        // ---- what to render ----

        internal sealed class SourceInfo
        {
            public GameObject Template;
            public string Label;          // asset path, or the game_object_path
            public bool FromScene;
            public string ScenePath;
            public string ObjectPath;
        }

        private static SourceInfo ResolveSource(PrefabViewRequest r)
        {
            if (!string.IsNullOrWhiteSpace(r.AssetPath))
            {
                var path = r.AssetPath.Trim().Replace('\\', '/');
                var main = AssetDatabase.LoadMainAssetAtPath(path);
                if (main == null)
                    throw new CaptureRefusedException("asset_not_found", "No asset at '" + path + "'. Pass the prefab's asset path, e.g. \"Assets/UI/Shop.prefab\".");
                if (!(main is GameObject go) || PrefabUtility.GetPrefabAssetType(go) == PrefabAssetType.NotAPrefab)
                    throw new CaptureRefusedException("not_a_prefab",
                        "'" + path + "' is a " + main.GetType().Name + ", not a prefab. The prefab view renders a UI prefab (.prefab).");
                return new SourceInfo { Template = go, Label = path };
            }

            var objectPath = r.GameObjectPath.Trim();
            var found = TypedTargets.Resolve(0, objectPath, null, TargetRequest.None);
            if (!found.Ok)
            {
                var f = found.Failure ?? new ResolveFailure(ResolveFailure.GameObjectNotFound, "No GameObject at '" + objectPath + "'.");
                throw new CaptureRefusedException(f.Code, f.Message, f.DetailJson);
            }
            var container = found.Container;
            return new SourceInfo
            {
                Template = found.GameObject,
                FromScene = true,
                ScenePath = container != null && container.IsPrefab ? container.AssetPath : found.GameObject.scene.path,
                ObjectPath = objectPath,
                Label = objectPath,
            };
        }

        // How the clone reaches a Screen Space - Camera canvas.
        internal sealed class CanvasPlan
        {
            public bool Wrap;                    // a panel without a Canvas: wrap it in a temporary one
            public List<string> RootPaths = new List<string>();  // own root canvases, relative to the template root ("" = the root)
            public string OriginalRenderMode;    // of the first own root canvas
            public int SkippedWorldSpace;
        }

        // A Canvas on the root, or root canvases below a plain Transform: own
        // canvases, switched to Screen Space - Camera. A RectTransform root
        // without one: a panel, wrapped. Anything else is not UI.
        internal static CanvasPlan PlanCanvases(GameObject template, string label)
        {
            var plan = new CanvasPlan();
            var rootCanvas = template.GetComponent<Canvas>();
            var candidates = new List<Canvas>();
            if (rootCanvas != null) candidates.Add(rootCanvas);
            else if (template.transform is RectTransform)
            {
                plan.Wrap = true;
                return plan;
            }
            else
            {
                foreach (var c in template.GetComponentsInChildren<Canvas>(true))
                    if (!HasCanvasAbove(c.transform, template.transform)) candidates.Add(c);
            }
            foreach (var c in candidates)
            {
                if (c.renderMode == RenderMode.WorldSpace) { plan.SkippedWorldSpace++; continue; }
                if (plan.OriginalRenderMode == null) plan.OriginalRenderMode = RenderModeName(c.renderMode);
                plan.RootPaths.Add(RelativePath(template.transform, c.transform));
            }
            if (plan.RootPaths.Count > 0) return plan;
            if (plan.SkippedWorldSpace > 0)
                throw new CaptureRefusedException("not_a_ui_prefab",
                    "'" + label + "' has only World Space canvases: they render in a 3D scene, not as a screen. Place it in a scene and use view \"scene\" or unity_senses_screenshot_camera.");
            throw new CaptureRefusedException("not_a_ui_prefab",
                "'" + label + "' is not UI: its root has no Canvas or RectTransform and no Canvas below it. Place it in a scene and use view \"isolated\" or unity_senses_screenshot_camera.");
        }

        // A Canvas between `t` (exclusive) and `root` (inclusive)?
        private static bool HasCanvasAbove(Transform t, Transform root)
        {
            for (var cur = t.parent; cur != null; cur = cur.parent)
            {
                if (cur.GetComponent<Canvas>() != null) return true;
                if (cur == root) break;
            }
            return false;
        }

        private static string RelativePath(Transform root, Transform t)
        {
            if (t == root) return "";
            var parts = new List<string>();
            for (var cur = t; cur != null && cur != root; cur = cur.parent) parts.Insert(0, cur.name);
            return string.Join("/", parts);
        }

        private static Transform Find(Transform root, string relative) =>
            relative.Length == 0 ? root : root.Find(relative);

        internal static string RenderModeName(RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.ScreenSpaceOverlay: return "screen_space_overlay";
                case RenderMode.ScreenSpaceCamera: return "screen_space_camera";
                default: return "world_space";
            }
        }

        // ---- the screen ----

        internal sealed class PreviewScreen
        {
            public int Width, Height;
            public Rect SafeArea;
            public bool Native;              // the game's own screen (no size argument)
            public ScreenshotService.GameViewContext Context;
        }

        private static PreviewScreen ResolveScreen(PrefabViewRequest r)
        {
            var ctx = new ScreenshotService.GameViewContext { Mode = "preview", ReportSafeArea = true };
            var screen = new PreviewScreen { Context = ctx };
            if (r.Width.HasValue)
            {
                screen.Width = r.Width.Value;
                screen.Height = r.Height.Value;
                ctx.Orientation = GameViewAdapter.ShapeOrientation(screen.Width, screen.Height);
            }
            else if (!string.IsNullOrWhiteSpace(r.Device))
            {
                if (!GameViewAdapter.IsSimulatorAvailable(out var code, out var message)) throw new CaptureRefusedException(code, message);
                var catalog = GameViewAdapter.Devices();
                var ids = new List<string>(catalog.Count);
                foreach (var d in catalog) ids.Add(d.Id);
                int match = GameViewAdapter.MatchDevice(ids, r.Device, out var candidates);
                if (match == -2)
                    throw new CaptureRefusedException("ambiguous_device", "'" + r.Device + "' matches " + candidates.Count + " devices; pass the exact id.",
                        "{\"candidates\":" + JsonArray(candidates) + "}");
                if (match < 0)
                    throw new CaptureRefusedException("device_not_found",
                        "No Device Simulator device matches '" + r.Device + "'. unity_open_mcp_game_view action list_devices lists the ids.",
                        "{\"available\":" + catalog.Count + "}");
                var device = catalog[match];
                var orientation = ScreenOrientation.Portrait;
                if (!string.IsNullOrWhiteSpace(r.Orientation)) GameViewAdapter.TryParseOrientation(r.Orientation, out orientation);
                var entry = device.Get(orientation);
                if (entry == null)
                    throw new CaptureRefusedException("validation_error", "Device '" + device.Id + "' does not support " + GameViewAdapter.OrientationName(orientation) + ".");
                bool landscape = orientation == ScreenOrientation.LandscapeLeft || orientation == ScreenOrientation.LandscapeRight;
                screen.Width = landscape ? device.Height : device.Width;
                screen.Height = landscape ? device.Width : device.Height;
                screen.SafeArea = entry.SafeArea;
                ctx.Device = device.Id;
                ctx.Orientation = GameViewAdapter.OrientationName(orientation);
            }
            else if (!string.IsNullOrWhiteSpace(r.SizeLabel))
            {
                if (!GameViewAdapter.IsGameViewAvailable(out var code, out var message)) throw new CaptureRefusedException(code, message);
                var label = r.SizeLabel.Trim();
                var sizes = GameViewAdapter.Sizes();
                GameViewSizeEntry entry = null;
                foreach (var s in sizes)
                    if (string.Equals(s.Label, label, StringComparison.OrdinalIgnoreCase) ||
                        !string.IsNullOrEmpty(s.BaseText) && string.Equals(s.BaseText, label, StringComparison.OrdinalIgnoreCase))
                    { entry = s; break; }
                if (entry == null)
                {
                    var labels = new List<string>();
                    foreach (var s in sizes) labels.Add(s.Label);
                    throw new CaptureRefusedException("size_not_found", "No Game view size labelled '" + label + "' in the current size group.",
                        "{\"labels\":" + JsonArray(labels) + "}");
                }
                if (entry.Type != "fixed_resolution")
                    throw new CaptureRefusedException("validation_error",
                        "'" + entry.Label + "' is " + (entry.Type == "free_aspect" ? "the free aspect entry" : "an aspect ratio") + ", not a resolution; pass a fixed-resolution label, width + height, or device.");
                screen.Width = entry.Width;
                screen.Height = entry.Height;
                ctx.SizeLabel = entry.Label;
                ctx.Orientation = GameViewAdapter.ShapeOrientation(screen.Width, screen.Height);
            }
            else
            {
                // The game's own screen: the Game view size or the simulated device.
                var game = ScreenshotService.DescribeGameView();
                if (game.ScreenWidth <= 0 || game.ScreenHeight <= 0)
                    throw new CaptureRefusedException("native_size_unavailable",
                        "No Game view or Device Simulator reports a screen size; pass width + height, device or size_label.");
                screen.Width = game.ScreenWidth;
                screen.Height = game.ScreenHeight;
                screen.SafeArea = game.SafeArea;
                screen.Native = true;
                ctx.Device = game.Device;
                ctx.SizeLabel = game.SizeLabel;
                ctx.Orientation = game.Orientation;
                ctx.Note = "The game's screen (" + game.Mode + "): pass width + height, device or size_label for another size.";
            }
            if (screen.SafeArea.width <= 0 || screen.SafeArea.height <= 0) screen.SafeArea = new Rect(0, 0, screen.Width, screen.Height);
            int max = Mathf.Min(MaxSize, SystemInfo.maxTextureSize);
            if (screen.Width > max || screen.Height > max)
                throw new CaptureRefusedException("validation_error", "The screen " + screen.Width + "x" + screen.Height + " exceeds the " + max + " px render limit.");
            ctx.ScreenWidth = screen.Width;
            ctx.ScreenHeight = screen.Height;
            ctx.SafeArea = screen.SafeArea;
            return screen;
        }

        // ---- the scaler ----

        internal sealed class ScalerChoice
        {
            public CanvasScalerSettings Settings;   // null: leave the canvas as authored (no CanvasScaler)
            public string From;                    // scaler_from | own | parent_canvas | scene | default | none
            public string FromPath;                // the canvas the settings came from
            public readonly List<string> Overrides = new List<string>();
            public bool Changed;                    // the canvas's scaler is replaced or edited for the render
        }

        // The base, in order: scaler_from; the rendered root canvas's own; for a
        // scene object, the root canvas above it; for a panel, the first screen
        // canvas with a CanvasScaler in the loaded scenes (active scene first,
        // hierarchy order); Unity's CanvasScaler defaults. canvas_scaler fields
        // then override single values.
        internal static ScalerChoice ChooseScaler(CanvasScalerSettings fromArg, string fromArgPath,
            CanvasScalerSettings own, bool wrap, CanvasScalerSettings parent, string parentPath,
            CanvasScalerSettings scene, string scenePath)
        {
            var choice = new ScalerChoice();
            if (fromArg != null) { choice.Settings = fromArg.Clone(); choice.From = "scaler_from"; choice.FromPath = fromArgPath; choice.Changed = true; }
            else if (!wrap && own != null) { choice.Settings = own.Clone(); choice.From = "own"; }
            else if (parent != null) { choice.Settings = parent.Clone(); choice.From = "parent_canvas"; choice.FromPath = parentPath; choice.Changed = true; }
            else if (wrap && scene != null) { choice.Settings = scene.Clone(); choice.From = "scene"; choice.FromPath = scenePath; choice.Changed = true; }
            else if (wrap) { choice.Settings = new CanvasScalerSettings(); choice.From = "default"; choice.Changed = true; }
            else choice.From = "none";
            return choice;
        }

        private static ScalerChoice ResolveScaler(PrefabViewRequest r, SourceInfo source, CanvasPlan plan)
        {
            CanvasScalerSettings fromArg = null, own = null, parent = null, scene = null;
            string fromArgPath = null, parentPath = null, scenePath = null;
            if (!string.IsNullOrWhiteSpace(r.ScalerFrom))
                fromArg = ReadScalerFrom(r.ScalerFrom.Trim(), out fromArgPath);
            if (!plan.Wrap)
            {
                var first = Find(source.Template.transform, plan.RootPaths[0]);
                own = first != null ? Ugui.ReadScaler(first.GetComponent<Canvas>()) : null;
            }
            // A scene object below a canvas keeps the scale of the canvas it sits in.
            if (source.FromScene && (plan.Wrap || own == null) && source.Template.transform.parent != null)
            {
                var root = TopCanvas(source.Template.transform.parent);
                if (root != null && root.renderMode != RenderMode.WorldSpace)
                {
                    parent = Ugui.ReadScaler(root);
                    if (parent != null) parentPath = ScenePathOf(root.gameObject);
                }
            }
            if (plan.Wrap && fromArg == null && parent == null) scene = FirstSceneScaler(out scenePath);

            var choice = ChooseScaler(fromArg, fromArgPath, own, plan.Wrap, parent, parentPath, scene, scenePath);
            if (r.CanvasScaler != null)
            {
                if (choice.Settings == null) { choice.Settings = new CanvasScalerSettings(); choice.From = "default"; }
                var error = choice.Settings.ApplyJson(r.CanvasScaler, choice.Overrides);
                if (error != null) throw new CaptureRefusedException("validation_error", error);
                if (choice.Overrides.Count > 0) choice.Changed = true;
            }
            return choice;
        }

        // scaler_from: a prefab asset (its root canvas, else its first canvas
        // with a CanvasScaler) or a canvas GameObject in a loaded scene / the
        // open prefab stage (its root canvas).
        private static CanvasScalerSettings ReadScalerFrom(string value, out string fromPath)
        {
            fromPath = value;
            Canvas canvas = null;
            var asset = value.StartsWith("Assets/", StringComparison.Ordinal) || value.StartsWith("Packages/", StringComparison.Ordinal)
                ? AssetDatabase.LoadMainAssetAtPath(value) : null;
            if (asset != null)
            {
                if (!(asset is GameObject prefab) || PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.NotAPrefab)
                    throw new CaptureRefusedException("scaler_not_found",
                        "scaler_from '" + value + "' is a " + asset.GetType().Name + ": pass a prefab with a canvas, or a canvas in a loaded scene.");
                canvas = prefab.GetComponent<Canvas>();
                if (canvas == null || Ugui.ReadScaler(canvas) == null)
                    foreach (var c in prefab.GetComponentsInChildren<Canvas>(true))
                        if (Ugui.ReadScaler(c) != null) { canvas = c; break; }
            }
            else
            {
                var found = TypedTargets.Resolve(0, value, null, TargetRequest.None);
                if (!found.Ok)
                {
                    var f = found.Failure;
                    if (f != null && !f.IsNotFound) throw new CaptureRefusedException(f.Code, "scaler_from: " + f.Message, f.DetailJson);
                    throw new CaptureRefusedException("scaler_not_found",
                        "scaler_from '" + value + "' is neither a prefab asset nor a GameObject in a loaded scene or the open prefab stage.");
                }
                var at = found.GameObject.GetComponent<Canvas>();
                if (at == null) at = found.GameObject.GetComponentInParent<Canvas>(true);
                canvas = at != null ? TopCanvas(at.transform) : null;
                if (canvas != null) fromPath = ScenePathOf(canvas.gameObject);
            }
            var settings = canvas != null ? Ugui.ReadScaler(canvas) : null;
            if (settings == null)
                throw new CaptureRefusedException("scaler_not_found", "scaler_from '" + value + "' has no canvas with a CanvasScaler.");
            return settings;
        }

        private static CanvasScalerSettings FirstSceneScaler(out string path)
        {
            path = null;
            var scenes = new List<Scene> { SceneManager.GetActiveScene() };
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s != scenes[0]) scenes.Add(s);
            }
            // An active canvas first: an inactive one may be a debug or legacy screen.
            Canvas fallback = null;
            foreach (var s in scenes)
            {
                if (!s.IsValid() || !s.isLoaded) continue;
                foreach (var root in s.GetRootGameObjects())
                    foreach (var c in root.GetComponentsInChildren<Canvas>(true))
                    {
                        if (c.renderMode == RenderMode.WorldSpace || HasCanvasAbove(c.transform, null)) continue;
                        if (Ugui.ReadScaler(c) == null) continue;
                        if (c.isActiveAndEnabled) return Take(c, out path);
                        if (fallback == null) fallback = c;
                    }
            }
            return fallback != null ? Take(fallback, out path) : null;
        }

        private static CanvasScalerSettings Take(Canvas canvas, out string path)
        {
            path = ScenePathOf(canvas.gameObject);
            return Ugui.ReadScaler(canvas);
        }

        // The outermost Canvas at or above `t` (the one whose scaler counts).
        private static Canvas TopCanvas(Transform t)
        {
            Canvas top = null;
            for (var cur = t; cur != null; cur = cur.parent)
            {
                var c = cur.GetComponent<Canvas>();
                if (c != null) top = c;
            }
            return top;
        }

        private static string ScenePathOf(GameObject go)
        {
            var parts = new List<string>();
            for (var t = go.transform; t != null; t = t.parent) parts.Insert(0, t.name);
            var scene = go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.path) ? go.scene.path : go.scene.name;
            return (string.IsNullOrEmpty(scene) ? "" : scene + ":") + string.Join("/", parts);
        }

        // ---- the render ----

        private static FrameCapture Render(SourceInfo source, CanvasPlan plan, PreviewScreen screen, ScalerChoice scaler, PrefabViewRequest r)
        {
            int w = screen.Width, h = screen.Height;
            var previousActive = RenderTexture.active;
            var created = new List<GameObject>();
            var scene = EditorSceneManager.NewPreviewScene();
            RenderTexture rt = null;
            Texture2D frame = null;
            try
            {
                var camera = Create("Prefab View Camera", scene, created, typeof(Camera)).GetComponent<Camera>();
                camera.enabled = false;
                camera.scene = scene;
                camera.orthographic = true;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 1000f;
                camera.cullingMask = ~0;
                camera.allowHDR = false;
                camera.allowMSAA = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = r.Transparent ? new Color(0f, 0f, 0f, 0f) : new Color32(64, 64, 64, 255);
                rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = rt;

                var holder = Create("Prefab View", scene, created);
                var roots = new List<Canvas>();
                GameObject clone;
                if (plan.Wrap)
                {
                    var wrapper = Create("Canvas (Prefab View)", scene, created, typeof(RectTransform), typeof(Canvas));
                    wrapper.layer = UiLayer;
                    var canvas = wrapper.GetComponent<Canvas>();
                    canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 |
                        AdditionalCanvasShaderChannels.Normal | AdditionalCanvasShaderChannels.Tangent;
                    clone = Object.Instantiate(source.Template, wrapper.transform, false);
                    roots.Add(canvas);
                }
                else
                {
                    clone = Object.Instantiate(source.Template, holder.transform, false);
                    foreach (var relative in plan.RootPaths)
                    {
                        var t = Find(clone.transform, relative);
                        var c = t != null ? t.GetComponent<Canvas>() : null;
                        if (c != null) roots.Add(c);
                    }
                }
                clone.name = source.Template.name;
                bool activated = !clone.activeSelf;
                if (activated) clone.SetActive(true);

                foreach (var canvas in roots)
                {
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = camera;
                    canvas.planeDistance = PlaneDistance;
                    if (scaler.Settings != null && (scaler.Changed || plan.Wrap)) Ugui.ApplyScaler(canvas, scaler.Settings);
                }
                AfterBuildForTests?.Invoke(scene);

                // Scaler + layout + text: update, rebuild every layout, update
                // again (a fitter that depends on text needs the text first).
                Canvas.ForceUpdateCanvases();
                foreach (var canvas in roots) Ugui.RebuildLayout((RectTransform)canvas.transform);
                Canvas.ForceUpdateCanvases();
                float scaleFactor = roots.Count > 0 ? roots[0].scaleFactor : 1f;

                camera.Render();
                frame = new Texture2D(w, h, TextureFormat.RGBA32, false);
                RenderTexture.active = rt;
                frame.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                frame.Apply();
                RenderTexture.active = previousActive;

                var capture = new FrameCapture
                {
                    Source = Source,
                    Width = w,
                    Height = h,
                    NativeWidth = w,
                    NativeHeight = h,
                    Native = screen.Native,
                    Requested = !screen.Native,
                    Context = screen.Context,
                };
                if (r.SafeAreaOverlay)
                {
                    ScreenshotService.DrawSafeAreaOverlay(frame, screen.SafeArea);
                    capture.SafeAreaOverlay = true;
                    capture.SafeArea = screen.SafeArea;
                }
                capture.Png = ImageConversion.EncodeToPNG(frame);
                capture.DetailJson = DescribeJson(source, plan, scaler, roots.Count, activated, scaleFactor);
                return capture;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (frame != null) Object.DestroyImmediate(frame);
                // Helpers are DontSave: a scene close would not destroy them.
                for (int i = created.Count - 1; i >= 0; i--)
                    if (created[i] != null) Object.DestroyImmediate(created[i]);
                if (scene.IsValid())
                {
                    foreach (var leftover in scene.GetRootGameObjects()) Object.DestroyImmediate(leftover);
                    EditorSceneManager.ClosePreviewScene(scene);
                }
                if (rt != null)
                {
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
        }

        // Created in the active scene by Unity and moved at once; hidden and
        // never saved (the PreviewRenderUtility pattern).
        private static GameObject Create(string name, Scene scene, List<GameObject> created, params Type[] components)
        {
            var go = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave, components);
            created.Add(go);
            SceneManager.MoveGameObjectToScene(go, scene);
            return go;
        }

        private static string DescribeJson(SourceInfo source, CanvasPlan plan, ScalerChoice scaler, int canvases, bool activated, float scaleFactor)
        {
            var sb = new StringBuilder(384);
            sb.Append('{');
            if (source.FromScene)
            {
                sb.Append("\"game_object_path\":").Append(BridgeJson.EscapeString(source.ObjectPath));
                sb.Append(",\"scene\":").Append(BridgeJson.EscapeString(source.ScenePath ?? ""));
            }
            else sb.Append("\"asset_path\":").Append(BridgeJson.EscapeString(source.Label));
            sb.Append(",\"canvas\":").Append(BridgeJson.EscapeString(plan.Wrap ? "wrapped" : "own"));
            if (!plan.Wrap)
            {
                sb.Append(",\"render_mode\":").Append(BridgeJson.EscapeString(plan.OriginalRenderMode));
                if (canvases > 1) sb.Append(",\"root_canvases\":").Append(canvases);
                if (plan.SkippedWorldSpace > 0) sb.Append(",\"skipped_world_space\":").Append(plan.SkippedWorldSpace);
            }
            if (activated) sb.Append(",\"activated\":true");
            sb.Append(",\"scale_factor\":"); BridgeJson.AppendJsonNumber(sb, scaleFactor);
            sb.Append(",\"scaler\":{\"from\":").Append(BridgeJson.EscapeString(scaler.From));
            if (scaler.FromPath != null) sb.Append(",\"from_path\":").Append(BridgeJson.EscapeString(scaler.FromPath));
            if (scaler.Overrides.Count > 0) sb.Append(",\"overrides\":").Append(JsonArray(scaler.Overrides));
            if (scaler.Settings != null)
            {
                sb.Append(',');
                scaler.Settings.AppendJson(sb);
            }
            sb.Append("}}");
            return sb.ToString();
        }

        private static string JsonArray(List<string> items)
        {
            var sb = new StringBuilder(64);
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(items[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
