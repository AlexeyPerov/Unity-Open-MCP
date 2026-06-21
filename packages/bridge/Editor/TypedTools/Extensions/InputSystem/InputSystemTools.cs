// M18 Plan 3 — Input System (com.unity.inputsystem) embedded domain tools.
//
// Compile-gated by UNITY_OPEN_MCP_EXT_INPUTSYSTEM. The owning sub-asmdef
// (com.alexeyperov.unity-open-mcp-bridge.InputSystem.Editor) carries
// `defineConstraints: ["UNITY_OPEN_MCP_EXT_INPUTSYSTEM"]` and references
// Unity.InputSystem; the bridge root asmdef sets the define via
// `versionDefines` when the package resolves. Ported verbatim (logic, tool
// ids, JSON schema, gate contracts) from the former standalone extension
// pack at packages/extensions/inputsystem — only the namespace changed.
#if UNITY_OPEN_MCP_EXT_INPUTSYSTEM
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Extensions.InputSystem
{
    // M16 Plan 10 / T6.6.4 → M18 Plan 3 — Input System embedded tools.
    //
    // Eight typed tools for authoring InputActionAsset graphs (.inputactions):
    // asset create + map/action/binding/composite/control-scheme add + get.
    //
    // All tools target a `.inputactions` asset path (not a scene GameObject)
    // — every mutator runs the gate path with `paths_hint` scoped to the asset
    // so the verify checkpoint can detect broken asset references. Naming:
    // `unity_open_mcp_inputsystem_<action>` (snake_case domain prefix — mirrors
    // the kebab `inputsystem-*` ids in the upstream Unity-MCP reference pack).
    //
    // Reference: IvanMurzak/Unity-AI-InputSystem (MIT).
    [BridgeToolType]
    public static class InputSystemTools
    {
        // =====================================================================
        // Asset create
        // =====================================================================

        // Create a new `.inputactions` InputActionAsset at an Assets/-rooted
        // path. Optionally seed an initial ActionMap. The asset is written to
        // disk as JSON and imported so AssetDatabase picks it up.
        [BridgeTool("unity_open_mcp_inputsystem_asset_create",
            Title = "InputSystem: Create InputActionAsset",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "input-system")]
        [System.ComponentModel.Description(
            "Create a new InputActionAsset at an 'Assets/'-rooted path ending in " +
            "'.inputactions'. Optionally seed an initial ActionMap (initial_action_map). " +
            "Mutating: runs the gate path; paths_hint is the new .inputactions asset path. " +
            "Intermediate folders must already exist (use assets_create_folder first).")]
        public static string AssetCreate(
            string asset_path,
            string initial_action_map = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return InputSystemJson.Error("paths_hint_required",
                    "inputsystem_asset_create is mutating; pass a non-empty paths_hint " +
                    "scoped to the new .inputactions asset path.");

            if (!InputSystemJson.ValidateAssetPath(asset_path, out var normalized, out var pathError))
                return InputSystemJson.Error("invalid_asset_path", pathError);

            if (AssetDatabase.LoadAssetAtPath<InputActionAsset>(normalized) != null)
                return InputSystemJson.Error("asset_already_exists",
                    $"An InputActionAsset already exists at '{normalized}'.");

            // ScriptableObject.CreateInstance + ToJson — same pattern as the
            // upstream pack. We bypass AssetDatabase.CreateAsset because the
            // InputSystem importer expects the JSON form on disk first.
            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            asset.name = System.IO.Path.GetFileNameWithoutExtension(normalized);

            bool seededMap = false;
            if (!string.IsNullOrEmpty(initial_action_map))
            {
                asset.AddActionMap(initial_action_map);
                seededMap = true;
            }

            System.IO.File.WriteAllText(normalized, SafeJson(asset));
            AssetDatabase.ImportAsset(normalized, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();

            var sb = new StringBuilder(160);
            sb.Append("\"assetPath\":").Append(InputSystemJson.Esc(normalized)).Append(',');
            sb.Append("\"initialActionMap\":").Append(seededMap ? InputSystemJson.Esc(initial_action_map) : "null").Append(',');
            sb.Append("\"actionMapCount\":").Append(asset.actionMaps.Count);
            return InputSystemJson.Ok(sb.ToString());
        }

        // =====================================================================
        // ActionMap add
        // =====================================================================

        [BridgeTool("unity_open_mcp_inputsystem_actionmap_add",
            Title = "InputSystem: Add ActionMap",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "input-system")]
        [System.ComponentModel.Description(
            "Add a new InputActionMap to an existing .inputactions asset. A map groups " +
            "related actions (e.g. 'Player', 'UI'). Fails if a map of that name already " +
            "exists. Mutating: runs the gate path; paths_hint is the .inputactions asset path.")]
        public static string ActionMapAdd(
            string asset_path,
            string map_name,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return PathRequired("inputsystem_actionmap_add");

            if (string.IsNullOrWhiteSpace(map_name))
                return InputSystemJson.Error("missing_parameter", "'map_name' is required.");

            var asset = InputSystemJson.LoadAsset(asset_path, out var loadError);
            if (asset == null) return InputSystemJson.Error("asset_not_found", loadError);

            if (asset.FindActionMap(map_name, throwIfNotFound: false) != null)
                return InputSystemJson.Error("actionmap_already_exists",
                    $"An ActionMap named '{map_name}' already exists in '{asset_path}'.");

            asset.AddActionMap(map_name);
            InputSystemJson.SaveAsset(asset);

            var sb = new StringBuilder(128);
            sb.Append("\"assetPath\":").Append(InputSystemJson.Esc(InputSystemJson.Normalize(asset_path))).Append(',');
            sb.Append("\"mapName\":").Append(InputSystemJson.Esc(map_name)).Append(',');
            sb.Append("\"actionMapCount\":").Append(asset.actionMaps.Count);
            return InputSystemJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Action add
        // =====================================================================

        [BridgeTool("unity_open_mcp_inputsystem_action_add",
            Title = "InputSystem: Add Action",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "input-system")]
        [System.ComponentModel.Description(
            "Add an InputAction to an ActionMap in an existing .inputactions asset. " +
            "action_type is 'Button' (default), 'Value', or 'PassThrough'. " +
            "expected_control_type is optional (e.g. 'Button', 'Vector2', 'Axis'). " +
            "binding is an optional initial binding control path (e.g. '<Gamepad>/buttonSouth'). " +
            "groups / interactions / processors apply to the initial binding. Mutating: runs " +
            "the gate path; paths_hint is the .inputactions asset path.")]
        public static string ActionAdd(
            string asset_path,
            string map_name,
            string action_name,
            string action_type = "Button",
            string expected_control_type = null,
            string binding = null,
            string groups = null,
            string interactions = null,
            string processors = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return PathRequired("inputsystem_action_add");

            if (string.IsNullOrWhiteSpace(map_name))
                return InputSystemJson.Error("missing_parameter", "'map_name' is required.");
            if (string.IsNullOrWhiteSpace(action_name))
                return InputSystemJson.Error("missing_parameter", "'action_name' is required.");

            if (!TryParseActionType(action_type, out var parsedType, out var typeError))
                return InputSystemJson.Error("invalid_action_type", typeError);

            var asset = InputSystemJson.LoadAsset(asset_path, out var loadError);
            if (asset == null) return InputSystemJson.Error("asset_not_found", loadError);

            var map = asset.FindActionMap(map_name, throwIfNotFound: false);
            if (map == null)
                return InputSystemJson.Error("actionmap_not_found",
                    $"No ActionMap named '{map_name}' in '{asset_path}'.");

            if (map.FindAction(action_name, throwIfNotFound: false) != null)
                return InputSystemJson.Error("action_already_exists",
                    $"An Action named '{action_name}' already exists in map '{map_name}'.");

            var action = map.AddAction(
                name: action_name,
                type: parsedType,
                binding: string.IsNullOrEmpty(binding) ? null : binding,
                interactions: string.IsNullOrEmpty(interactions) ? null : interactions,
                processors: string.IsNullOrEmpty(processors) ? null : processors,
                groups: string.IsNullOrEmpty(groups) ? null : groups);

            if (!string.IsNullOrEmpty(expected_control_type))
                action.expectedControlType = expected_control_type;

            InputSystemJson.SaveAsset(asset);

            var sb = new StringBuilder(192);
            sb.Append("\"assetPath\":").Append(InputSystemJson.Esc(InputSystemJson.Normalize(asset_path))).Append(',');
            sb.Append("\"mapName\":").Append(InputSystemJson.Esc(map_name)).Append(',');
            sb.Append("\"actionName\":").Append(InputSystemJson.Esc(action_name)).Append(',');
            sb.Append("\"actionType\":").Append(InputSystemJson.Esc(parsedType.ToString())).Append(',');
            sb.Append("\"expectedControlType\":").Append(
                string.IsNullOrEmpty(action.expectedControlType) ? "null" : InputSystemJson.Esc(action.expectedControlType)).Append(',');
            sb.Append("\"bindingCount\":").Append(action.bindings.Count);
            return InputSystemJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Binding add (simple)
        // =====================================================================

        [BridgeTool("unity_open_mcp_inputsystem_binding_add",
            Title = "InputSystem: Add Binding",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "input-system")]
        [System.ComponentModel.Description(
            "Add a simple (non-composite) InputBinding to an Action in a .inputactions asset. " +
            "path is a control path (e.g. '<Keyboard>/space'). groups is optional control-scheme " +
            "group(s), semicolon-separated. interactions / processors are optional. For composite " +
            "bindings (2DVector / 1DAxis) use inputsystem_binding_composite_add. Mutating: runs " +
            "the gate path; paths_hint is the .inputactions asset path.")]
        public static string BindingAdd(
            string asset_path,
            string map_name,
            string action_name,
            string path,
            string groups = null,
            string interactions = null,
            string processors = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return PathRequired("inputsystem_binding_add");

            if (string.IsNullOrWhiteSpace(map_name))
                return InputSystemJson.Error("missing_parameter", "'map_name' is required.");
            if (string.IsNullOrWhiteSpace(action_name))
                return InputSystemJson.Error("missing_parameter", "'action_name' is required.");
            if (string.IsNullOrWhiteSpace(path))
                return InputSystemJson.Error("missing_parameter", "'path' is required.");

            var asset = InputSystemJson.LoadAsset(asset_path, out var loadError);
            if (asset == null) return InputSystemJson.Error("asset_not_found", loadError);

            if (!ResolveAction(asset, map_name, action_name, out var action, out var resolveError))
                return resolveError;

            action.AddBinding(
                path: path,
                interactions: string.IsNullOrEmpty(interactions) ? null : interactions,
                processors: string.IsNullOrEmpty(processors) ? null : processors,
                groups: string.IsNullOrEmpty(groups) ? null : groups);

            InputSystemJson.SaveAsset(asset);

            var sb = new StringBuilder(160);
            sb.Append("\"assetPath\":").Append(InputSystemJson.Esc(InputSystemJson.Normalize(asset_path))).Append(',');
            sb.Append("\"mapName\":").Append(InputSystemJson.Esc(map_name)).Append(',');
            sb.Append("\"actionName\":").Append(InputSystemJson.Esc(action_name)).Append(',');
            sb.Append("\"path\":").Append(InputSystemJson.Esc(path)).Append(',');
            sb.Append("\"bindingIndex\":").Append(action.bindings.Count - 1).Append(',');
            sb.Append("\"bindingCount\":").Append(action.bindings.Count);
            return InputSystemJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Composite binding add
        // =====================================================================

        // Add a composite binding (2DVector / 1DAxis / Dpad / Axis) with named
        // parts. parts_json is a JSON array of { name, path, groups? } entries
        // — e.g. [{"name":"up","path":"<Keyboard>/w"},{"name":"down","path":"<Keyboard>/s"}].
        // The composite root is one binding; each part is appended after it.
        [BridgeTool("unity_open_mcp_inputsystem_binding_composite_add",
            Title = "InputSystem: Add Composite Binding",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "input-system")]
        [System.ComponentModel.Description(
            "Add a composite InputBinding (e.g. '2DVector' WASD, '1DAxis') to an Action. " +
            "parts_json is a JSON array of { name, path, groups? } entries — e.g. " +
            "[{\"name\":\"up\",\"path\":\"<Keyboard>/w\"},{\"name\":\"down\",\"path\":\"<Keyboard>/s\"}]. " +
            "composite is the composite type (default '2DVector'); also '1DAxis', 'Axis', 'Dpad'. " +
            "interactions / processors apply to the composite root. Mutating: runs the gate path; " +
            "paths_hint is the .inputactions asset path.")]
        public static string BindingCompositeAdd(
            string asset_path,
            string map_name,
            string action_name,
            string parts_json,
            string composite = "2DVector",
            string interactions = null,
            string processors = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return PathRequired("inputsystem_binding_composite_add");

            if (string.IsNullOrWhiteSpace(composite))
                return InputSystemJson.Error("missing_parameter", "'composite' is required (e.g. '2DVector').");

            var parts = ParseCompositeParts(parts_json);
            if (parts == null || parts.Count == 0)
                return InputSystemJson.Error("missing_parameter",
                    "'parts_json' must be a JSON array of { name, path, groups? } entries " +
                    "(at least one part is required).");

            var asset = InputSystemJson.LoadAsset(asset_path, out var loadError);
            if (asset == null) return InputSystemJson.Error("asset_not_found", loadError);

            if (!ResolveAction(asset, map_name, action_name, out var action, out var resolveError))
                return resolveError;

            // AddCompositeBinding returns a fluent syntax; chain each part via With(...).
            var syntax = action.AddCompositeBinding(
                composite,
                interactions: string.IsNullOrEmpty(interactions) ? null : interactions,
                processors: string.IsNullOrEmpty(processors) ? null : processors);

            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part.Name) || string.IsNullOrWhiteSpace(part.Path))
                    return InputSystemJson.Error("missing_parameter",
                        "Each composite part requires a non-empty 'name' and 'path'.");
                syntax = syntax.With(part.Name, part.Path,
                    groups: string.IsNullOrEmpty(part.Groups) ? null : part.Groups);
            }

            InputSystemJson.SaveAsset(asset);

            var sb = new StringBuilder(160);
            sb.Append("\"assetPath\":").Append(InputSystemJson.Esc(InputSystemJson.Normalize(asset_path))).Append(',');
            sb.Append("\"mapName\":").Append(InputSystemJson.Esc(map_name)).Append(',');
            sb.Append("\"actionName\":").Append(InputSystemJson.Esc(action_name)).Append(',');
            sb.Append("\"composite\":").Append(InputSystemJson.Esc(composite)).Append(',');
            sb.Append("\"partCount\":").Append(parts.Count).Append(',');
            sb.Append("\"bindingCount\":").Append(action.bindings.Count);
            return InputSystemJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Control scheme add
        // =====================================================================

        [BridgeTool("unity_open_mcp_inputsystem_controlscheme_add",
            Title = "InputSystem: Add Control Scheme",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "input-system")]
        [System.ComponentModel.Description(
            "Add an InputControlScheme to a .inputactions asset. required_devices / " +
            "optional_devices are arrays of device control paths (e.g. '<Gamepad>', '<Keyboard>'). " +
            "Mutating: runs the gate path; paths_hint is the .inputactions asset path.")]
        public static string ControlSchemeAdd(
            string asset_path,
            string scheme_name,
            string[] required_devices = null,
            string[] optional_devices = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return PathRequired("inputsystem_controlscheme_add");

            if (string.IsNullOrWhiteSpace(scheme_name))
                return InputSystemJson.Error("missing_parameter", "'scheme_name' is required.");

            var asset = InputSystemJson.LoadAsset(asset_path, out var loadError);
            if (asset == null) return InputSystemJson.Error("asset_not_found", loadError);

            foreach (var existing in asset.controlSchemes)
            {
                if (string.Equals(existing.name, scheme_name, System.StringComparison.OrdinalIgnoreCase))
                    return InputSystemJson.Error("controlscheme_already_exists",
                        $"A control scheme named '{scheme_name}' already exists.");
            }

            var syntax = asset.AddControlScheme(scheme_name);
            if (required_devices != null)
            {
                foreach (var dev in required_devices)
                    if (!string.IsNullOrWhiteSpace(dev))
                        syntax = syntax.WithRequiredDevice(dev);
            }
            if (optional_devices != null)
            {
                foreach (var dev in optional_devices)
                    if (!string.IsNullOrWhiteSpace(dev))
                        syntax = syntax.WithOptionalDevice(dev);
            }
            syntax.Done();

            InputSystemJson.SaveAsset(asset);

            var sb = new StringBuilder(160);
            sb.Append("\"assetPath\":").Append(InputSystemJson.Esc(InputSystemJson.Normalize(asset_path))).Append(',');
            sb.Append("\"schemeName\":").Append(InputSystemJson.Esc(scheme_name)).Append(',');
            sb.Append("\"requiredDeviceCount\":").Append(required_devices?.Length ?? 0).Append(',');
            sb.Append("\"optionalDeviceCount\":").Append(optional_devices?.Length ?? 0).Append(',');
            sb.Append("\"controlSchemeCount\":").Append(asset.controlSchemes.Count);
            return InputSystemJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Get (read-only)
        // =====================================================================

        // Read the full structure of a `.inputactions` InputActionAsset — its
        // ActionMaps, Actions (type / expectedControlType), Bindings (path /
        // groups / interactions / processors / index / composite flags) and
        // Control Schemes. Read-only.
        [BridgeTool("unity_open_mcp_inputsystem_get",
            Title = "InputSystem: Get Asset Structure",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "input-system")]
        [System.ComponentModel.Description(
            "Read the full structure of a .inputactions InputActionAsset — ActionMaps, " +
            "Actions (type / expectedControlType), Bindings (path / groups / interactions / " +
            "processors / index / composite flags) and Control Schemes. Read-only, gate-free. " +
            "Use this to discover map / action / binding names to drive the other tools.")]
        public static string Get(string asset_path)
        {
            var asset = InputSystemJson.LoadAsset(asset_path, out var loadError);
            if (asset == null) return InputSystemJson.Error("asset_not_found", loadError);

            var sb = new StringBuilder(2048);
            sb.Append("{\"status\":\"ok\",\"assetPath\":")
              .Append(InputSystemJson.Esc(InputSystemJson.Normalize(asset_path))).Append(',');
            sb.Append("\"actionMaps\":[");

            bool firstMap = true;
            foreach (var map in asset.actionMaps)
            {
                if (!firstMap) sb.Append(',');
                firstMap = false;
                sb.Append("{\"name\":").Append(InputSystemJson.Esc(map.name)).Append(",\"actions\":[");
                bool firstAction = true;
                foreach (var action in map.actions)
                {
                    if (!firstAction) sb.Append(',');
                    firstAction = false;
                    sb.Append("{\"name\":").Append(InputSystemJson.Esc(action.name)).Append(',');
                    sb.Append("\"type\":").Append(InputSystemJson.Esc(action.type.ToString())).Append(',');
                    sb.Append("\"expectedControlType\":").Append(
                        string.IsNullOrEmpty(action.expectedControlType) ? "null" : InputSystemJson.Esc(action.expectedControlType)).Append(',');
                    sb.Append("\"bindings\":[");
                    for (int i = 0; i < action.bindings.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        var b = action.bindings[i];
                        sb.Append('{');
                        sb.Append("\"index\":").Append(i).Append(',');
                        sb.Append("\"path\":").Append(b.path == null ? "null" : InputSystemJson.Esc(b.path)).Append(',');
                        sb.Append("\"name\":").Append(string.IsNullOrEmpty(b.name) ? "null" : InputSystemJson.Esc(b.name)).Append(',');
                        sb.Append("\"groups\":").Append(string.IsNullOrEmpty(b.groups) ? "null" : InputSystemJson.Esc(b.groups)).Append(',');
                        sb.Append("\"interactions\":").Append(string.IsNullOrEmpty(b.interactions) ? "null" : InputSystemJson.Esc(b.interactions)).Append(',');
                        sb.Append("\"processors\":").Append(string.IsNullOrEmpty(b.processors) ? "null" : InputSystemJson.Esc(b.processors)).Append(',');
                        sb.Append("\"isComposite\":").Append(b.isComposite ? "true" : "false").Append(',');
                        sb.Append("\"isPartOfComposite\":").Append(b.isPartOfComposite ? "true" : "false");
                        sb.Append('}');
                    }
                    sb.Append("]}");
                }
                sb.Append("]}");
            }
            sb.Append("],\"controlSchemes\":[");
            bool firstScheme = true;
            foreach (var scheme in asset.controlSchemes)
            {
                if (!firstScheme) sb.Append(',');
                firstScheme = false;
                sb.Append("{\"name\":").Append(InputSystemJson.Esc(scheme.name)).Append(',');
                sb.Append("\"bindingGroup\":").Append(
                    string.IsNullOrEmpty(scheme.bindingGroup) ? "null" : InputSystemJson.Esc(scheme.bindingGroup)).Append(',');
                sb.Append("\"devices\":[");
                bool firstDev = true;
                foreach (var req in scheme.deviceRequirements)
                {
                    if (!firstDev) sb.Append(',');
                    firstDev = false;
                    sb.Append(InputSystemJson.Esc((req.isOptional ? "(optional) " : "") + req.controlPath));
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static string PathRequired(string tool)
            => InputSystemJson.Error("paths_hint_required",
                $"{tool} is mutating; pass a non-empty paths_hint scoped to the .inputactions asset path.");

        private static bool TryParseActionType(string s, out InputActionType type, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(s))
            {
                type = InputActionType.Button;
                return true;
            }
            if (System.Enum.TryParse<InputActionType>(s, true, out type))
                return true;
            error = $"Unknown action_type '{s}'. Use 'Button', 'Value', or 'PassThrough'.";
            return false;
        }

        // Resolve an action within an asset's map. Returns false + sets outError
        // (a structured JSON error envelope) when the map or action is missing.
        private static bool ResolveAction(InputActionAsset asset, string mapName, string actionName,
            out InputAction action, out string errorEnvelope)
        {
            action = null;
            errorEnvelope = null;
            var map = asset.FindActionMap(mapName, throwIfNotFound: false);
            if (map == null)
            {
                errorEnvelope = InputSystemJson.Error("actionmap_not_found",
                    $"No ActionMap named '{mapName}'.");
                return false;
            }
            action = map.FindAction(actionName, throwIfNotFound: false);
            if (action == null)
            {
                errorEnvelope = InputSystemJson.Error("action_not_found",
                    $"No Action named '{actionName}' in map '{mapName}'.");
                return false;
            }
            return true;
        }

        struct CompositePart
        {
            public string Name;
            public string Path;
            public string Groups;
        }

        // Parse a JSON array of { name, path, groups? } entries. Hand-rolled —
        // the bridge's JsonBody helpers are not visible outside the bridge
        // assembly. Returns null on a malformed array.
        private static List<CompositePart> ParseCompositeParts(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]")) return null;

            var parts = new List<CompositePart>();
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
                        var body = trimmed.Substring(objStart, i - objStart);
                        parts.Add(new CompositePart
                        {
                            Name = ExtractStringValue(body, "name"),
                            Path = ExtractStringValue(body, "path"),
                            Groups = ExtractStringValue(body, "groups"),
                        });
                        objStart = -1;
                    }
                }
            }
            return parts;
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
            var primitiveEnd = start;
            while (primitiveEnd < objBody.Length &&
                   objBody[primitiveEnd] != ',' &&
                   objBody[primitiveEnd] != '}')
                primitiveEnd++;
            return objBody.Substring(start, primitiveEnd - start).Trim();
        }

        // Serialize an InputActionAsset to JSON, guarding the InputSystem 1.x
        // ToJson() crash when the asset has no maps. Mirrors the helper on
        // InputSystemJson — duplicated here only because it is static-private
        // over there; the Create path calls this form before the asset is on
        // disk (SaveAsset would throw on GetAssetPath for a fresh instance).
        // actionMaps is a ReadOnlyArray struct — check Count, not == null.
        private static string SafeJson(InputActionAsset asset)
        {
            if (asset.actionMaps.Count == 0)
                return "{\n    \"name\": \"" + asset.name + "\",\n    \"maps\": [],\n    \"controlSchemes\": []\n}";
            return asset.ToJson();
        }
    }
}
#endif
