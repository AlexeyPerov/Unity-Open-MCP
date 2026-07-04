// M20 Plan 3 / T20.3.2 — UI (uGUI) embedded domain tools.
//
// Four typed tools covering the Canvas / element / layout-group / element-modify
// layer.
//
//   ui_canvas_add           — add a Canvas (+ CanvasScaler + GraphicRaycaster)
//                             to a host GameObject or as a new scene root, and
//                             ensure an EventSystem exists in the open scene(s).
//   ui_element_add          — add a uGUI element (Text / TMP_Text if TMP is
//                             present / Image / Button / Slider / Toggle /
//                             InputField) as a child of a parent RectTransform.
//   ui_layout_group_add     — add a HorizontalLayoutGroup / VerticalLayoutGroup
//                             / GridLayoutGroup to a parent.
//   ui_element_modify       — typed patch on a uGUI component (Selectables,
//                             Graphic color/raycastTarget, LayoutElement
//                             preferred sizes).
//
// Unity 6 decoupled uGUI from the engine into the optional com.unity.ugui
// package, so this domain is GATED on that package's presence. The owning
// sub-asmdef carries BOTH a `versionDefines` entry (matches `com.unity.ugui`
// → defines `UNITY_OPEN_MCP_EXT_UI`) AND a `defineConstraints` entry on the
// same symbol, so the gate is fully self-contained per-sub-asmdef (Unity 6
// versionDefines only satisfy the defineConstraints on the SAME asmdef, not
// across asmdefs). When ugui is absent the file compiles to an empty
// namespace and the tools are simply unavailable (returns tool_not_found at
// call time). TextMesh Pro (TMP_Text) is OPTIONAL and detected at call time
// via reflection; when an agent requests element_type=TMP_Text and TMP is
// absent, the tool returns a structured `tmp_package_required` error instead
// of a silent legacy-Text fallback.
//
// The `ui` tool group is still hidden from ListTools until the session
// activates it via unity_open_mcp_manage_tools (group visibility is a session
// concern, independent of compile-gating).
//
// Naming: `unity_open_mcp_ui_<action>` (snake_case domain prefix).
#if UNITY_OPEN_MCP_EXT_UI
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.UI
{
    // M20 Plan 3 / T20.3.2 — UI tools. Registry-discovered via [BridgeToolType]
    // + [BridgeTool]. All four tools are mutating (canvas / element / layout
    // group creation + element modify all write scene state) and declare
    // IsMutating = true with a snake_case paths_hint (bound to the C# pathsHint
    // parameter by name) so the gate can scope the verify checkpoint.
    [BridgeToolType]
    public static class UITools
    {
        // =====================================================================
        // Canvas — add (+ CanvasScaler + GraphicRaycaster + ensure EventSystem)
        // =====================================================================

        // Add a Canvas to a host GameObject (or as a new scene root when no host
        // is addressed) and ensure the canvas has a CanvasScaler +
        // GraphicRaycaster, plus an EventSystem somewhere in the open scene(s).
        // renderMode overlay/camera/world + EventSystem. Idempotent: re-using
        // an existing Canvas is reported with added:false (the scaler /
        // raycaster / EventSystem are still ensured).
        [BridgeTool("unity_open_mcp_ui_canvas_add",
            Title = "UI: Add Canvas",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "ui")]
        [System.ComponentModel.Description(
            "Add a Canvas to a GameObject (or as a new scene root when no host is " +
            "addressed). Ensures the canvas has a CanvasScaler + GraphicRaycaster, and " +
            "ensures an EventSystem exists in the open scene(s). Set render_mode " +
            "(ScreenSpaceOverlay | ScreenSpaceCamera | WorldSpace, default " +
            "ScreenSpaceOverlay) and sorting_order (default 0). Idempotent — re-using " +
            "an existing Canvas reports added:false. Mutating: runs the gate path; " +
            "paths_hint is the host / new-root scene path.")]
        public static string CanvasAdd(
            int instance_id = 0,
            string path = null,
            string name = null,
            string render_mode = "ScreenSpaceOverlay",
            int sorting_order = 0,
            string new_root_name = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return UIJson.Error("paths_hint_required",
                    "ui_canvas_add is mutating; pass a non-empty paths_hint scoped " +
                    "to the host's (or new root's) scene path.");

            // Resolve the host. When no host is addressed, create a new scene
            // root GameObject to hold the canvas (agents pass new_root_name to
            // name it; otherwise it defaults to "Canvas").
            GameObject host = UITargets.Resolve(instance_id, path, name);
            bool createdRoot = false;
            if (host == null)
            {
                host = new GameObject(string.IsNullOrEmpty(new_root_name) ? "Canvas" : new_root_name);
                Undo.RegisterCreatedObjectUndo(host, "Create UI Canvas root");
                createdRoot = true;
            }

            Undo.RecordObject(host, "Add Canvas");

            var canvas = host.GetComponent<Canvas>();
            bool addedCanvas = false;
            if (canvas == null)
            {
                canvas = Undo.AddComponent<Canvas>(host);
                addedCanvas = true;
            }

            if (ParseRenderMode(render_mode, out var mode))
                canvas.renderMode = mode;
            canvas.sortingOrder = sorting_order;

            // Ensure CanvasScaler (the standard companion on every screen-space
            // canvas; harmless on world-space ones).
            bool addedScaler = false;
            if (host.GetComponent<CanvasScaler>() == null)
            {
                Undo.AddComponent<CanvasScaler>(host);
                addedScaler = true;
            }

            // Ensure GraphicRaycaster (so the canvas receives pointer events).
            bool addedRaycaster = false;
            if (host.GetComponent<GraphicRaycaster>() == null)
            {
                Undo.AddComponent<GraphicRaycaster>(host);
                addedRaycaster = true;
            }

            // Ensure an EventSystem exists in the open scene(s). uGUI input
            // (pointer / drag / keyboard on Selectables) does not work without
            // an EventSystem + a StandaloneInputModule / InputSystemUIInputModule.
            bool addedEventSystem = EnsureEventSystem();

            EditorUtility.SetDirty(host);

            var sb = new StringBuilder(320);
            sb.Append("\"canvas\":{");
            sb.Append("\"added\":").Append(addedCanvas ? "true" : "false").Append(',');
            sb.Append("\"createdRoot\":").Append(createdRoot ? "true" : "false").Append(',');
            sb.Append("\"instanceId\":").Append(InstanceId.ToJson(canvas)).Append(',');
            sb.Append("\"name\":").Append(UIJson.Esc(host.name)).Append(',');
            sb.Append("\"path\":").Append(UIJson.Esc(UITargets.BuildPath(host))).Append(',');
            sb.Append("\"renderMode\":").Append(UIJson.Esc(canvas.renderMode.ToString())).Append(',');
            sb.Append("\"sortingOrder\":").Append(canvas.sortingOrder).Append(',');
            sb.Append("\"addedCanvasScaler\":").Append(addedScaler ? "true" : "false").Append(',');
            sb.Append("\"addedGraphicRaycaster\":").Append(addedRaycaster ? "true" : "false").Append(',');
            sb.Append("\"addedEventSystem\":").Append(addedEventSystem ? "true" : "false");
            sb.Append('}');
            return UIJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Element — add (Text / TMP_Text / Image / Button / Slider / Toggle / InputField)
        // =====================================================================

        // Add a uGUI element as a child of a parent RectTransform. The parent
        // MUST exist — every uGUI element lives under a Canvas in the hierarchy,
        // so this tool does not create a new root (element types + parent +
        // anchoredPosition + sizeDelta). When element_type=TMP_Text and TMP is
        // absent, returns `tmp_package_required` — no silent Text fallback.
        [BridgeTool("unity_open_mcp_ui_element_add",
            Title = "UI: Add Element",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "ui")]
        [System.ComponentModel.Description(
            "Add a uGUI element as a child of a parent RectTransform. element_type is " +
            "one of Text | TMP_Text | Image | Button | Slider | Toggle | InputField. " +
            "Common fields: text (Text/ TMP_Text / InputField), color (Graphic.color, " +
            "r,g,b,a 0-1), sprite_path (Image/ Button/ Toggle sprite, Assets/-rooted). " +
            "TMP_Text requires the TextMesh Pro package — when absent, returns " +
            "`tmp_package_required` (no silent legacy-Text fallback). Mutating: runs " +
            "the gate path; paths_hint is the parent scene path.")]
        public static string ElementAdd(
            string parent_path = null,
            int parent_instance_id = 0,
            string parent_name = null,
            string element_type = null,
            string element_name = null,
            string text = null,
            string color = null,
            string sprite_path = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return UIJson.Error("paths_hint_required",
                    "ui_element_add is mutating; pass a non-empty paths_hint scoped " +
                    "to the parent's scene path.");

            if (string.IsNullOrEmpty(parent_path) && parent_instance_id == 0 &&
                string.IsNullOrEmpty(parent_name))
                return UIJson.Error("missing_parameter",
                    "A parent target is required (parent_instance_id > parent_path > " +
                    "parent_name). uGUI elements live under a Canvas in the hierarchy.");

            if (string.IsNullOrEmpty(element_type))
                return UIJson.Error("missing_parameter",
                    "'element_type' is required (Text | TMP_Text | Image | Button | " +
                    "Slider | Toggle | InputField).");

            var parent = UITargets.Resolve(parent_instance_id, parent_path, parent_name);
            if (parent == null)
                return UIJson.Error("parent_not_found",
                    "Parent GameObject not resolved. Address by parent_instance_id > " +
                    "parent_path > parent_name.");

            Sprite sprite = null;
            if (!string.IsNullOrEmpty(sprite_path))
            {
                sprite = AssetDatabase.LoadAssetAtPath<Sprite>(sprite_path);
                if (sprite == null)
                    return UIJson.Error("asset_not_found",
                        "Sprite not found at '" + sprite_path + "'.");
            }

            Color? graphicColor = null;
            if (!string.IsNullOrEmpty(color))
            {
                if (!TryParseColor(color, out var parsed))
                    return UIJson.Error("invalid_color",
                        "color must be 'r,g,b,a' (0-1) — got '" + color + "'.");
                graphicColor = parsed;
            }

            // Create the element GameObject. The child name defaults to the
            // element_type when element_name is omitted.
            var goName = string.IsNullOrEmpty(element_name) ? element_type : element_name;
            var go = new GameObject(goName, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, "Create UI element");
            go.transform.SetParent(parent.transform, worldPositionStays: false);

            var addedComponents = new StringBuilder(160);
            addedComponents.Append('[');
            bool first = true;

            // Add the requested component by type. TMP_Text is detected at call
            // time via reflection so the assembly does not take a compile-time
            // TMP reference.
            string addError = AddElementComponent(go, element_type, text, graphicColor,
                sprite, addedComponents, ref first);
            if (addError != null)
            {
                Undo.DestroyObjectImmediate(go);
                return addError;
            }

            addedComponents.Append(']');
            EditorUtility.SetDirty(parent);

            var sb = new StringBuilder(320);
            sb.Append("\"element\":{");
            sb.Append("\"name\":").Append(UIJson.Esc(go.name)).Append(',');
            sb.Append("\"type\":").Append(UIJson.Esc(element_type)).Append(',');
            sb.Append("\"instanceId\":").Append(InstanceId.ToJson(go)).Append(',');
            sb.Append("\"path\":").Append(UIJson.Esc(UITargets.BuildPath(go))).Append(',');
            sb.Append("\"parentPath\":").Append(UIJson.Esc(UITargets.BuildPath(parent))).Append(',');
            sb.Append("\"addedComponents\":").Append(addedComponents.ToString());
            sb.Append('}');
            return UIJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Layout group — add (Horizontal / Vertical / Grid)
        // =====================================================================

        // Add a layout group to a parent RectTransform. The parent must exist.
        // padding defaults to 0 on all sides; spacing defaults to 0; child
        // alignment defaults to UpperLeft.
        [BridgeTool("unity_open_mcp_ui_layout_group_add",
            Title = "UI: Add Layout Group",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "ui")]
        [System.ComponentModel.Description(
            "Add a layout group to a parent RectTransform. layout_type is " +
            "HorizontalLayoutGroup | VerticalLayoutGroup | GridLayoutGroup. Optional: " +
            "padding (left,right,top,bottom — defaults 0), spacing (x,y — defaults 0), " +
            "child_alignment (TextAnchor name, default UpperLeft), " +
            "child_control_width / child_control_height (default true), " +
            "child_force_expand_width / child_force_expand_height (default true). " +
            "Idempotent — re-using an existing group of the same type reports " +
            "added:false. Mutating: runs the gate path; paths_hint is the parent " +
            "scene path.")]
        public static string LayoutGroupAdd(
            int instance_id = 0,
            string path = null,
            string name = null,
            string layout_type = null,
            string padding = null,
            string spacing = null,
            string child_alignment = null,
            bool child_control_width = true,
            bool child_control_height = true,
            bool child_force_expand_width = true,
            bool child_force_expand_height = true,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return UIJson.Error("paths_hint_required",
                    "ui_layout_group_add is mutating; pass a non-empty paths_hint.");

            if (string.IsNullOrEmpty(layout_type))
                return UIJson.Error("missing_parameter",
                    "'layout_type' is required (HorizontalLayoutGroup | " +
                    "VerticalLayoutGroup | GridLayoutGroup).");

            var host = UITargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            // Resolve the layout group type by name. HorizontalLayoutGroup /
            // VerticalLayoutGroup / GridLayoutGroup all live in UnityEngine.UI.
            var groupType = ResolveLayoutGroupType(layout_type);
            if (groupType == null)
                return UIJson.Error("invalid_layout_type",
                    "Unknown layout_type '" + layout_type + "'. Use " +
                    "HorizontalLayoutGroup | VerticalLayoutGroup | GridLayoutGroup.");

            Undo.RecordObject(host, "Add UI layout group");

            // Idempotent: re-use an existing group of the same type.
            var existing = host.GetComponent(groupType);
            bool added = false;
            HorizontalOrVerticalLayoutGroup hvGroup = null;
            GridLayoutGroup gridGroup = null;
            if (existing == null)
            {
                if (groupType == typeof(GridLayoutGroup))
                    gridGroup = Undo.AddComponent(host, typeof(GridLayoutGroup)) as GridLayoutGroup;
                else
                    hvGroup = Undo.AddComponent(host, groupType) as HorizontalOrVerticalLayoutGroup;
                added = true;
            }
            else
            {
                if (existing is GridLayoutGroup g) gridGroup = g;
                else if (existing is HorizontalOrVerticalLayoutGroup h) hvGroup = h;
            }

            // Apply padding.
            if (!string.IsNullOrEmpty(padding))
            {
                if (TryParseRectOffset(padding, out var rect))
                {
                    if (gridGroup != null) gridGroup.padding = rect;
                    if (hvGroup != null) hvGroup.padding = rect;
                }
            }

            // Apply spacing (grid uses Vector2; HV uses a single float — we take
            // spacing.x when only HV is in play).
            if (!string.IsNullOrEmpty(spacing))
            {
                if (TryParseVector2(spacing, out var sp))
                {
                    if (gridGroup != null) gridGroup.spacing = sp;
                    if (hvGroup != null) hvGroup.spacing = sp.x;
                }
            }

            // Apply child alignment (TextAnchor name).
            if (!string.IsNullOrEmpty(child_alignment))
            {
                if (System.Enum.TryParse<TextAnchor>(child_alignment, true, out var anchor))
                {
                    if (gridGroup != null) gridGroup.childAlignment = anchor;
                    if (hvGroup != null) hvGroup.childAlignment = anchor;
                }
            }

            // Apply child control / force-expand (GridLayoutGroup ignores these;
            // they're HorizontalOrVerticalLayoutGroup / LayoutGroup contracts).
            if (hvGroup != null)
            {
                hvGroup.childControlWidth = child_control_width;
                hvGroup.childControlHeight = child_control_height;
                hvGroup.childForceExpandWidth = child_force_expand_width;
                hvGroup.childForceExpandHeight = child_force_expand_height;
            }

            EditorUtility.SetDirty(host);

            var sb = new StringBuilder(280);
            sb.Append("\"layoutGroup\":{");
            sb.Append("\"added\":").Append(added ? "true" : "false").Append(',');
            sb.Append("\"type\":").Append(UIJson.Esc(groupType.Name)).Append(',');
            // Either the grid group or the HV group is non-null (the other
            // branch assigned it). Resolve the instanceId from whichever is set.
            long instanceId = 0;
            if (gridGroup != null) instanceId =InstanceId.Of(gridGroup);
            else if (hvGroup != null) instanceId =InstanceId.Of(hvGroup);
            sb.Append("\"instanceId\":").Append(instanceId).Append(',');
            sb.Append("\"path\":").Append(UIJson.Esc(UITargets.BuildPath(host)));
            sb.Append('}');
            return UIJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Element — typed modify (Selectables / Graphic / LayoutElement)
        // =====================================================================

        // Typed patch on a uGUI component. Mirrors component_modify's shape but
        // is scoped to uGUI types so the value conversion is purpose-built for
        // the common fields (Graphic.color, Graphic.raycastTarget, Selectable.
        // interactable, LayoutElement.preferredWidth / preferredHeight /
        // minWidth / minHeight, Text / TMP_Text text). Each entry is
        // { field, value, type? } where type is 'int' | 'float' | 'bool' | 'string'
        // | 'color' | 'vector' (default inferred from the current value). Unknown
        // fields are reported as errors; the tool fails atomically (no partial
        // writes) when any field fails to convert.
        [BridgeTool("unity_open_mcp_ui_element_modify",
            Title = "UI: Modify Element",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "ui")]
        [System.ComponentModel.Description(
            "Set typed fields on a uGUI component attached to a target GameObject. " +
            "Select the component by 'component_type' (Text | TMP_Text | Image | Button | " +
            "Slider | Toggle | InputField | Canvas | CanvasScaler | GraphicRaycaster | " +
            "HorizontalLayoutGroup | VerticalLayoutGroup | GridLayoutGroup | " +
            "LayoutElement | Selectable). Each entry is { field, value, type? } where " +
            "type is 'int' | 'float' | 'bool' | 'string' | 'color' | 'vector' (default " +
            "inferred from the current value). Mutating: runs the gate path; paths_hint " +
            "is the host scene path.")]
        public static string ElementModify(
            int instance_id = 0,
            string path = null,
            string name = null,
            string component_type = null,
            string fields_json = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return UIJson.Error("paths_hint_required",
                    "ui_element_modify is mutating; pass a non-empty paths_hint.");

            if (string.IsNullOrEmpty(component_type))
                return UIJson.Error("missing_parameter",
                    "'component_type' is required (Text | TMP_Text | Image | Button | " +
                    "Slider | Toggle | InputField | Canvas | CanvasScaler | " +
                    "GraphicRaycaster | HorizontalLayoutGroup | VerticalLayoutGroup | " +
                    "GridLayoutGroup | LayoutElement | Selectable).");

            var host = UITargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var comp = ResolveComponent(host, component_type);
            if (comp == null)
                return UIJson.Error("component_not_found",
                    $"Target has no component of type '{component_type}'.");

            var entries = ParseFieldArray(fields_json);
            if (entries == null)
                return UIJson.Error("missing_parameter",
                    "'fields_json' must be a JSON array of {field, value, type?} objects.");

            Undo.RecordObject(comp, "Modify UI element");
            var applied = new StringBuilder(256);
            var errors = new StringBuilder(256);
            applied.Append('[');
            errors.Append('[');
            bool firstApplied = true;
            bool firstError = true;

            foreach (var entry in entries)
            {
                var fieldResult = SetField(comp, entry);
                if (fieldResult.Ok)
                {
                    if (!firstApplied) applied.Append(',');
                    firstApplied = false;
                    applied.Append('{');
                    applied.Append("\"field\":").Append(UIJson.Esc(entry.Field)).Append(',');
                    applied.Append("\"applied\":true");
                    applied.Append('}');
                }
                else
                {
                    if (!firstError) errors.Append(',');
                    firstError = false;
                    errors.Append('{');
                    errors.Append("\"field\":").Append(UIJson.Esc(entry.Field)).Append(',');
                    errors.Append("\"error\":").Append(UIJson.Esc(fieldResult.Message));
                    errors.Append('}');
                }
            }
            applied.Append(']');
            errors.Append(']');

            EditorUtility.SetDirty(comp);
            return UIJson.Ok(
                "\"applied\":" + applied.ToString() + ',' +
                "\"errors\":" + errors.ToString());
        }

        // =====================================================================
        // Helpers — component resolution
        // =====================================================================

        private static System.Type ResolveLayoutGroupType(string layoutType)
        {
            switch (layoutType)
            {
                case "HorizontalLayoutGroup": return typeof(HorizontalLayoutGroup);
                case "VerticalLayoutGroup": return typeof(VerticalLayoutGroup);
                case "GridLayoutGroup": return typeof(GridLayoutGroup);
                default: return null;
            }
        }

        // Resolve a uGUI component by friendly name. Selectable is the shared
        // base for Button / Slider / Toggle — agents can address the base type
        // for shared fields (interactable, navigation). TMP_Text resolves via
        // reflection (the assembly may not take a compile-time TMP reference).
        private static Component ResolveComponent(GameObject host, string typeName)
        {
            switch (typeName)
            {
                case "Text": return host.GetComponent<Text>();
                case "Image": return host.GetComponent<Image>();
                case "Button": return host.GetComponent<Button>();
                case "Slider": return host.GetComponent<Slider>();
                case "Toggle": return host.GetComponent<Toggle>();
                case "InputField": return host.GetComponent<InputField>();
                case "Canvas": return host.GetComponent<Canvas>();
                case "CanvasScaler": return host.GetComponent<CanvasScaler>();
                case "GraphicRaycaster": return host.GetComponent<GraphicRaycaster>();
                case "HorizontalLayoutGroup": return host.GetComponent<HorizontalLayoutGroup>();
                case "VerticalLayoutGroup": return host.GetComponent<VerticalLayoutGroup>();
                case "GridLayoutGroup": return host.GetComponent<GridLayoutGroup>();
                case "LayoutElement": return host.GetComponent<LayoutElement>();
                case "Selectable": return host.GetComponent<Selectable>();
                case "TMP_Text":
                    return host.GetComponent(GetTmpTextType() ?? typeof(Text));
                default: return null;
            }
        }

        // Add the requested element component to the new child GameObject.
        // Returns a non-null error string when the element type is unknown or
        // TMP is absent (the caller undoes the GameObject creation). The
        // addedComponents builder is appended to so the response can report
        // exactly which components were attached.
        private static string AddElementComponent(GameObject go, string elementType,
            string text, Color? color, Sprite sprite,
            StringBuilder addedComponents, ref bool first)
        {
            switch (elementType)
            {
                case "Text":
                {
                    var t = Undo.AddComponent<Text>(go);
                    if (text != null) t.text = text;
                    if (color.HasValue) t.color = color.Value;
                    AppendAdded(addedComponents, ref first, "Text");
                    return null;
                }
                case "TMP_Text":
                {
                    var tmpTextType = GetTmpTextType();
                    if (tmpTextType == null)
                        return UIJson.Error("tmp_package_required",
                            "TextMesh Pro package is required for element_type=TMP_Text. " +
                            "Install com.unity.textmeshpro (Unity 5.x-2023) or " +
                            "unity.textmeshpro (Unity 6+) and retry. The tools do NOT " +
                            "silently fall back to legacy UnityEngine.UI.Text.");
                    // Create a TextMeshPro (UI) instance via the concrete type —
                    // the public TMP_Text type is abstract.
                    var concreteType = GetConcreteTmpType();
                    if (concreteType == null)
                        return UIJson.Error("tmp_package_required",
                            "TextMesh Pro package is present but the concrete TMP UI " +
                            "component type could not be resolved. Ensure TMP is " +
                            "installed correctly and retry.");
                    var comp = Undo.AddComponent(go, concreteType);
                    if (text != null && comp is MonoBehaviour mb)
                    {
                        var textProp = concreteType.GetProperty("text");
                        if (textProp != null && textProp.CanWrite)
                            textProp.SetValue(mb, text);
                    }
                    if (color.HasValue && comp is MonoBehaviour mb2)
                    {
                        var colorProp = concreteType.GetProperty("color");
                        if (colorProp != null && colorProp.CanWrite)
                            colorProp.SetValue(mb2, color.Value);
                    }
                    AppendAdded(addedComponents, ref first, "TMP_Text");
                    return null;
                }
                case "Image":
                {
                    var img = Undo.AddComponent<Image>(go);
                    if (sprite != null) img.sprite = sprite;
                    if (color.HasValue) img.color = color.Value;
                    AppendAdded(addedComponents, ref first, "Image");
                    return null;
                }
                case "Button":
                {
                    var img = Undo.AddComponent<Image>(go);
                    if (sprite != null) img.sprite = sprite;
                    if (color.HasValue) img.color = color.Value;
                    Undo.AddComponent<Button>(go);
                    AppendAdded(addedComponents, ref first, "Image");
                    AppendAdded(addedComponents, ref first, "Button");
                    return null;
                }
                case "Slider":
                {
                    // Slider auto-creates its handle/fill hierarchy lazily, but a
                    // single Slider component on an empty RectTransform still
                    // functions once a Graphic is assigned by the agent. We add
                    // the bare component — agents wire the sub-targets via
                    // ui_element_modify (Slider.fillRect / handleRect / etc.).
                    Undo.AddComponent<Slider>(go);
                    AppendAdded(addedComponents, ref first, "Slider");
                    return null;
                }
                case "Toggle":
                {
                    var img = Undo.AddComponent<Image>(go);
                    if (sprite != null) img.sprite = sprite;
                    if (color.HasValue) img.color = color.Value;
                    var toggle = Undo.AddComponent<Toggle>(go);
                    toggle.isOn = true;
                    AppendAdded(addedComponents, ref first, "Image");
                    AppendAdded(addedComponents, ref first, "Toggle");
                    return null;
                }
                case "InputField":
                {
                    // A working InputField needs a Text child to render the
                    // entered text and a caret. The bare InputField component is
                    // enough for the field-patch surface — agents wire the
                    // textComponent / placeholder via ui_element_modify.
                    Undo.AddComponent<InputField>(go);
                    AppendAdded(addedComponents, ref first, "InputField");
                    return null;
                }
                default:
                    return UIJson.Error("invalid_element_type",
                        "Unknown element_type '" + elementType + "'. Use Text | " +
                        "TMP_Text | Image | Button | Slider | Toggle | InputField.");
            }
        }

        // =====================================================================
        // Helpers — TMP presence detection (reflection; no compile-time ref)
        // =====================================================================

        // Append one component-type name to the addedComponents JSON array,
        // inserting a leading comma when this is not the first entry. The
        // `first` flag is passed by ref so the caller tracks comma insertion
        // across multiple AppendAdded calls. Declared as a regular static
        // method (not a local function) because C# forbids capturing a ref
        // parameter in a lambda / local function.
        private static void AppendAdded(StringBuilder addedComponents, ref bool first,
            string componentType)
        {
            if (!first) addedComponents.Append(',');
            first = false;
            addedComponents.Append('"').Append(componentType).Append('"');
        }

        // Resolve the abstract TMP_Text type (TMPro.TMP_Text). Returns null when
        // TMP is not installed. The base type is abstract — we resolve the
        // concrete TextMeshProUGUI subtype separately via GetConcreteTmpType().
        // Cached after first resolution so the per-call reflection cost is one
        // assembly walk, not N.
        private static System.Type _tmpTextType;
        private static bool _tmpTextTypeResolved;
        private static System.Type GetTmpTextType()
        {
            if (_tmpTextTypeResolved) return _tmpTextType;
            _tmpTextTypeResolved = true;
            _tmpTextType = System.AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(a => a.GetType("TMPro.TMP_Text"))
                .FirstOrDefault(t => t != null);
            return _tmpTextType;
        }

        // Resolve the concrete TMP UI component type (TextMeshProUGUI). Used to
        // actually add the component — TMP_Text itself is abstract. Cached.
        private static System.Type _concreteTmpType;
        private static bool _concreteTmpTypeResolved;
        private static System.Type GetConcreteTmpType()
        {
            if (_concreteTmpTypeResolved) return _concreteTmpType;
            _concreteTmpTypeResolved = true;
            _concreteTmpType = System.AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(a => a.GetType("TMPro.TextMeshProUGUI"))
                .FirstOrDefault(t => t != null);
            return _concreteTmpType;
        }

        // =====================================================================
        // Helpers — EventSystem ensure
        // =====================================================================

        // Ensure the open scene(s) have an EventSystem. uGUI pointer / keyboard
        // input requires one EventSystem with a StandaloneInputModule (or the
        // InputSystemUIInputModule when the Input System package is present).
        // Returns true when this call created one.
        private static bool EnsureEventSystem()
        {
            var existing = Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include);
            if (existing != null && existing.Length > 0) return false;

            var esGo = new GameObject("EventSystem");
            Undo.RegisterCreatedObjectUndo(esGo, "Create EventSystem");
            Undo.AddComponent<EventSystem>(esGo);

            // Add StandaloneInputModule (the legacy input module). When the
            // Input System package is installed, the agent can swap this for
            // InputSystemUIInputModule via ui_element_modify.
            if (esGo.GetComponent<StandaloneInputModule>() == null)
                Undo.AddComponent<StandaloneInputModule>(esGo);
            return true;
        }

        // =====================================================================
        // Helpers — parsing
        // =====================================================================

        private static bool ParseRenderMode(string s, out RenderMode mode)
        {
            // RenderMode is an enum (ScreenSpaceOverlay / ScreenSpaceCamera /
            // WorldSpace). Allow case-insensitive name OR int parse.
            mode = RenderMode.ScreenSpaceOverlay;
            if (string.IsNullOrEmpty(s)) return false;
            if (System.Enum.TryParse<RenderMode>(s, true, out var parsed))
            {
                mode = parsed;
                return true;
            }
            if (int.TryParse(s, out var i) && i >= 0 && i <= 2)
            {
                mode = (RenderMode)i;
                return true;
            }
            return false;
        }

        private static bool TryParseColor(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split(',');
            if (parts.Length != 3 && parts.Length != 4) return false;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var r)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var g)) return false;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var b)) return false;
            float a = 1f;
            if (parts.Length == 4 &&
                !float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out a)) return false;
            c = new Color(r, g, b, a);
            return true;
        }

        private static bool TryParseRectOffset(string s, out RectOffset rect)
        {
            rect = new RectOffset(0, 0, 0, 0);
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split(',');
            if (parts.Length != 4) return false;
            if (!int.TryParse(parts[0].Trim(), out var left)) return false;
            if (!int.TryParse(parts[1].Trim(), out var right)) return false;
            if (!int.TryParse(parts[2].Trim(), out var top)) return false;
            if (!int.TryParse(parts[3].Trim(), out var bottom)) return false;
            rect = new RectOffset(left, right, top, bottom);
            return true;
        }

        private static bool TryParseVector2(string s, out Vector2 v)
        {
            v = Vector2.zero;
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split(',');
            if (parts.Length != 2) return false;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return false;
            v = new Vector2(x, y);
            return true;
        }

        // =====================================================================
        // Helpers — reflective field setter (mirrors Navigation pattern)
        // =====================================================================

        struct FieldEntry
        {
            public string Field;
            public string RawValue;
            public string TypeHint;
        }

        private static System.Collections.Generic.List<FieldEntry> ParseFieldArray(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]")) return null;

            var entries = new System.Collections.Generic.List<FieldEntry>();
            int depth = 0;
            int objStart = -1;
            for (int i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                if (c == '{')
                {
                    if (depth == 0) objStart = i + 1;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        var objBody = trimmed.Substring(objStart, i - objStart);
                        entries.Add(ParseFieldEntry(objBody));
                        objStart = -1;
                    }
                }
            }
            return entries;
        }

        private static FieldEntry ParseFieldEntry(string objBody)
        {
            var entry = new FieldEntry();
            entry.Field = ExtractStringValue(objBody, "field");
            entry.TypeHint = ExtractStringValue(objBody, "type");
            entry.RawValue = ExtractRawValue(objBody, "value");
            return entry;
        }

        private static string ExtractStringValue(string objBody, string key)
        {
            var raw = ExtractRawValue(objBody, key);
            if (string.IsNullOrEmpty(raw)) return null;
            if (raw.StartsWith("\"") && raw.EndsWith("\"") && raw.Length >= 2)
                return raw.Substring(1, raw.Length - 2);
            return raw;
        }

        private static string ExtractRawValue(string objBody, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = objBody.IndexOf(pattern, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            var colon = objBody.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            var start = colon + 1;
            while (start < objBody.Length && char.IsWhiteSpace(objBody[start])) start++;
            if (start >= objBody.Length) return null;

            if (objBody[start] == '"')
            {
                var end = start + 1;
                while (end < objBody.Length)
                {
                    if (objBody[end] == '\\' && end + 1 < objBody.Length) { end += 2; continue; }
                    if (objBody[end] == '"') break;
                    end++;
                }
                return objBody.Substring(start, System.Math.Min(end + 1, objBody.Length) - start);
            }

            if (objBody[start] == '{' || objBody[start] == '[')
            {
                var open = objBody[start];
                var close = open == '{' ? '}' : ']';
                int d = 0;
                var end = start;
                while (end < objBody.Length)
                {
                    if (objBody[end] == open) d++;
                    else if (objBody[end] == close)
                    {
                        d--;
                        if (d == 0) { end++; break; }
                    }
                    end++;
                }
                return objBody.Substring(start, end - start);
            }

            var primitiveEnd = start;
            while (primitiveEnd < objBody.Length &&
                   objBody[primitiveEnd] != ',' &&
                   objBody[primitiveEnd] != '}')
                primitiveEnd++;
            return objBody.Substring(start, primitiveEnd - start).Trim();
        }

        struct FieldResult
        {
            public bool Ok;
            public string Message;
        }

        private static FieldResult SetField(Component comp, FieldEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Field))
                return new FieldResult { Ok = false, Message = "field is required" };

            var t = comp.GetType();
            // Prefer a public property (most uGUI fields are properties), then
            // fall back to a public field. Mirrors the Navigation modify
            // approach but widens the search to properties since uGUI exposes
            // color / interactable / preferredWidth as properties.
            var prop = t.GetProperty(entry.Field,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    object converted = ConvertValue(prop.PropertyType, entry.RawValue, entry.TypeHint);
                    prop.SetValue(comp, converted);
                    return new FieldResult { Ok = true };
                }
                catch (System.Exception e)
                {
                    return new FieldResult { Ok = false, Message = e.Message };
                }
            }

            var field = t.GetField(entry.Field,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                try
                {
                    object converted = ConvertValue(field.FieldType, entry.RawValue, entry.TypeHint);
                    field.SetValue(comp, converted);
                    return new FieldResult { Ok = true };
                }
                catch (System.Exception e)
                {
                    return new FieldResult { Ok = false, Message = e.Message };
                }
            }

            return new FieldResult { Ok = false, Message = $"Unknown field '{entry.Field}' on {t.Name}." };
        }

        private static object ConvertValue(System.Type targetType, string raw, string typeHint)
        {
            if (targetType == typeof(string))
            {
                if (raw == null) return null;
                if (raw.StartsWith("\"") && raw.EndsWith("\"") && raw.Length >= 2)
                    return raw.Substring(1, raw.Length - 2);
                return raw;
            }
            if (targetType == typeof(int)) return int.Parse(raw);
            if (targetType == typeof(float)) return float.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return raw == "true";
            if (targetType == typeof(Color))
            {
                if (!TryParseColor(raw.Trim('"'), out var c))
                    throw new System.FormatException($"Cannot parse '{raw}' as Color (r,g,b,a 0-1).");
                return c;
            }
            if (targetType == typeof(Vector2))
            {
                if (!TryParseVector2(raw.Trim('"'), out var v))
                    throw new System.FormatException($"Cannot parse '{raw}' as Vector2 (x,y).");
                return v;
            }
            if (targetType == typeof(Vector3))
            {
                var trimmed = raw.Trim('"');
                var parts = trimmed.Split(',');
                if (parts.Length != 3)
                    throw new System.FormatException($"Cannot parse '{raw}' as Vector3 (x,y,z).");
                if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var x) ||
                    !float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var y) ||
                    !float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var z))
                    throw new System.FormatException($"Cannot parse '{raw}' as Vector3 (x,y,z).");
                return new Vector3(x, y, z);
            }
            if (targetType.IsEnum)
            {
                var cleaned = raw.Trim('"');
                if (System.Enum.IsDefined(targetType, cleaned))
                    return System.Enum.Parse(targetType, cleaned);
                if (int.TryParse(cleaned, out var intVal))
                    return System.Enum.ToObject(targetType, intVal);
                throw new System.FormatException($"Cannot parse '{raw}' as {targetType.Name} enum.");
            }
            throw new System.NotSupportedException($"Unsupported field type {targetType.Name}.");
        }

        // =====================================================================
        // Helpers — common
        // =====================================================================

        private static string TargetNotFound()
            => UIJson.Error("target_not_found",
                "No GameObject resolved. Address by instance_id > path > name.");
    }
}
#else // !UNITY_OPEN_MCP_EXT_UI — uGUI package not installed; the UI tools
      // compile to an empty namespace and are reported as tool_not_found at
      // call time. (The asmdef's defineConstraints excludes this assembly
      // entirely when com.unity.ugui is absent, so this #else is a backstop.)
namespace UnityOpenMcpBridge.Extensions.UI { }
#endif
