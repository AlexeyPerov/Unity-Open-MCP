using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpExtensions.Animation
{
    // M16 Plan 10 / T6.6.10 — AnimatorController half of the Animation
    // extension pack.
    //
    // Three typed tools on `.controller` assets:
    //   - create:  empty AnimatorController at an Assets/-rooted path.
    //   - get_data: read controller name + parameters + layers + states +
    //               transitions (enough to drive a follow-up modify call).
    //   - modify:  apply a batch of modifications dispatched by `type`:
    //               AddParameter / RemoveParameter / AddLayer / RemoveLayer /
    //               AddState / RemoveState / SetDefaultState / AddTransition /
    //               RemoveTransition / AddAnyStateTransition / SetStateMotion /
    //               SetStateSpeed.
    //
    // Per-entry errors are accumulated in the response's `errors` array (no
    // thrown exceptions to MCP). Naming: `unity_open_mcp_animator_<action>`
    // (snake_case domain prefix).
    [BridgeToolType]
    public static class AnimatorTools
    {
        // =====================================================================
        // Create
        // =====================================================================

        // Create empty AnimatorController assets at one or more `.controller`
        // paths. Intermediate folders are created. Each path is validated
        // independently.
        [BridgeTool("unity_open_mcp_animator_create",
            Title = "Animator: Create AnimatorController",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle)]
        [System.ComponentModel.Description(
            "Create empty AnimatorController assets at one or more 'Assets/'-rooted " +
            ".controller paths. Intermediate folders are created. Each path is " +
            "validated independently — bad entries land in `errors`, the rest still " +
            "create. Pair with animator_modify to add layers/states/parameters " +
            "afterwards. Mutating: runs the gate path; paths_hint is the list of " +
            ".controller paths being created.")]
        public static string Create(
            string[] asset_paths,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return PathRequired();

            if (asset_paths == null || asset_paths.Length == 0)
                return AnimationJson.Error("missing_parameter",
                    "'asset_paths' is required (one or more .controller paths).");

            var errors = new List<string>();
            var created = new List<string>();
            foreach (var raw in asset_paths)
            {
                if (!AnimationJson.ValidateAssetPath(raw, AnimationJson.ControllerExtension,
                        out var path, out var pathError))
                {
                    errors.Add($"{raw}: {pathError}");
                    continue;
                }

                if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null)
                {
                    errors.Add($"{path}: an AnimatorController already exists at this path.");
                    continue;
                }

                AnimationJson.EnsureFolders(path);
                var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
                controller.name = Path.GetFileNameWithoutExtension(path);
                created.Add(path);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var sb = new StringBuilder(256);
            sb.Append("\"create\":{");
            sb.Append("\"createdPaths\":").Append(JsonStringArray(created)).Append(',');
            sb.Append("\"errorCount\":").Append(errors.Count);
            if (errors.Count > 0)
                sb.Append(',').Append("\"errors\":").Append(JsonStringArray(errors));
            sb.Append('}');
            return AnimationJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Get data (read-only)
        // =====================================================================

        // Read controller name + parameters + layers + states + transitions.
        // Read-only, gate-free.
        [BridgeTool("unity_open_mcp_animator_get_data",
            Title = "Animator: Get AnimatorController Data",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None)]
        [System.ComponentModel.Description(
            "Inspect an AnimatorController asset (.controller) — name, parameters " +
            "(name / type / defaults), layers, and per-layer state machines " +
            "(states, default state, state-to-state transitions, any-state " +
            "transitions, sub-state machines). Read-only, gate-free. Use this to " +
            "discover valid layer / state / parameter names for animator_modify.")]
        public static string GetData(string asset_path)
        {
            var controller = LoadController(asset_path, out var loadError);
            if (controller == null) return loadError;

            var sb = new StringBuilder(2048);
            sb.Append("{\"status\":\"ok\",\"controller\":{");
            sb.Append("\"name\":").Append(AnimationJson.Esc(controller.name)).Append(',');
            sb.Append("\"assetPath\":").Append(AnimationJson.Esc(AnimationJson.Normalize(asset_path))).Append(',');
            sb.Append("\"instanceId\":").Append(InstanceId.ToJson(controller)).Append(',');

            // Parameters.
            sb.Append("\"parameters\":[");
            for (int i = 0; i < controller.parameters.Length; i++)
            {
                if (i > 0) sb.Append(',');
                AppendParameter(sb, controller.parameters[i]);
            }
            sb.Append("],");

            // Layers (with their state machines).
            sb.Append("\"layers\":[");
            for (int i = 0; i < controller.layers.Length; i++)
            {
                if (i > 0) sb.Append(',');
                AppendLayer(sb, controller.layers[i]);
            }
            sb.Append("]}}");
            return sb.ToString();
        }

        // =====================================================================
        // Modify (batch)
        // =====================================================================

        // Apply a batch of modifications to an AnimatorController.
        // modifications_json is a JSON array of { type, ... } entries
        // dispatched by `type`. Per-entry errors accumulate in `errors`.
        [BridgeTool("unity_open_mcp_animator_modify",
            Title = "Animator: Modify AnimatorController",
            IsMutating = true,
            DestructiveHint = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle)]
        [System.ComponentModel.Description(
            "Apply a batch of modifications to an AnimatorController asset " +
            "(.controller). modifications_json is a JSON array of entries " +
            "dispatched by `type`: AddParameter / RemoveParameter / AddLayer / " +
            "RemoveLayer / AddState / RemoveState / SetDefaultState / " +
            "AddTransition / RemoveTransition / AddAnyStateTransition / " +
            "SetStateMotion / SetStateSpeed. Per-entry errors are accumulated in " +
            "`errors` and do not abort the batch. Use animator_get_data first to " +
            "discover valid layer / state / parameter names. Mutating: runs the " +
            "gate path; paths_hint is the .controller asset path.")]
        public static string Modify(
            string asset_path,
            string modifications_json,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return PathRequired();

            if (string.IsNullOrWhiteSpace(modifications_json))
                return AnimationJson.Error("missing_parameter",
                    "'modifications_json' is required (a JSON array of modification entries).");

            var controller = LoadController(asset_path, out var loadError);
            if (controller == null) return loadError;

            var mods = ModificationParser.ParseArray(modifications_json);
            if (mods == null)
                return AnimationJson.Error("invalid_modifications_json",
                    "'modifications_json' must be a JSON array of modification entries.");

            var applied = new List<string>();
            var errors = new List<string>();
            for (int i = 0; i < mods.Count; i++)
            {
                var mod = mods[i];
                try
                {
                    ApplyModification(controller, mod, applied);
                }
                catch (System.Exception e)
                {
                    errors.Add($"[{i}] {mod.Type}: {e.Message}");
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var sb = new StringBuilder(256);
            sb.Append("\"modify\":{");
            sb.Append("\"assetPath\":").Append(AnimationJson.Esc(AnimationJson.Normalize(asset_path))).Append(',');
            sb.Append("\"applied\":").Append(JsonStringArray(applied)).Append(',');
            sb.Append("\"errorCount\":").Append(errors.Count);
            if (errors.Count > 0)
                sb.Append(',').Append("\"errors\":").Append(JsonStringArray(errors));
            sb.Append('}');
            return AnimationJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Helpers — load + per-type application
        // =====================================================================

        private static string PathRequired()
            => AnimationJson.Error("paths_hint_required",
                "animator tool is mutating; pass a non-empty paths_hint scoped to the .controller asset path.");

        private static AnimatorController LoadController(string assetPath, out string errorEnvelope)
        {
            errorEnvelope = null;
            if (!AnimationJson.ValidateAssetPath(assetPath, AnimationJson.ControllerExtension,
                    out var path, out var pathError))
            {
                errorEnvelope = AnimationJson.Error("invalid_asset_path", pathError);
                return null;
            }

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
            {
                errorEnvelope = AnimationJson.Error("asset_not_found",
                    $"No AnimatorController at '{path}'. Create it with animator_create first.");
                return null;
            }
            return controller;
        }

        private static void ApplyModification(AnimatorController controller, Modification mod,
            List<string> applied)
        {
            switch (mod.Type)
            {
                case "AddParameter":
                    AddParameter(controller, mod);
                    applied.Add($"AddParameter({mod.ParameterName})");
                    break;
                case "RemoveParameter":
                    RemoveParameter(controller, mod);
                    applied.Add($"RemoveParameter({mod.ParameterName})");
                    break;
                case "AddLayer":
                    if (string.IsNullOrEmpty(mod.LayerName))
                        throw new System.Exception("layerName is required for AddLayer.");
                    controller.AddLayer(mod.LayerName);
                    applied.Add($"AddLayer({mod.LayerName})");
                    break;
                case "RemoveLayer":
                    if (string.IsNullOrEmpty(mod.LayerName))
                        throw new System.Exception("layerName is required for RemoveLayer.");
                    controller.RemoveLayer(GetLayerIndex(controller, mod.LayerName));
                    applied.Add($"RemoveLayer({mod.LayerName})");
                    break;
                case "AddState":
                {
                    var state = AddState(controller, mod);
                    if (!string.IsNullOrEmpty(mod.MotionAssetPath))
                    {
                        var motion = AssetDatabase.LoadAssetAtPath<Motion>(mod.MotionAssetPath);
                        if (motion != null) state.motion = motion;
                    }
                    applied.Add($"AddState({mod.LayerName}, {mod.StateName})");
                    break;
                }
                case "RemoveState":
                {
                    var layer = GetLayer(controller, mod.LayerName);
                    var state = GetState(layer.stateMachine, mod.StateName);
                    layer.stateMachine.RemoveState(state);
                    applied.Add($"RemoveState({mod.LayerName}, {mod.StateName})");
                    break;
                }
                case "SetDefaultState":
                {
                    var layer = GetLayer(controller, mod.LayerName);
                    layer.stateMachine.defaultState = GetState(layer.stateMachine, mod.StateName);
                    applied.Add($"SetDefaultState({mod.LayerName}, {mod.StateName})");
                    break;
                }
                case "AddTransition":
                {
                    var layer = GetLayer(controller, mod.LayerName);
                    var source = GetState(layer.stateMachine, mod.SourceStateName);
                    var dest = GetState(layer.stateMachine, mod.DestinationStateName);
                    var tr = source.AddTransition(dest);
                    ConfigureTransition(tr, mod);
                    applied.Add($"AddTransition({mod.SourceStateName}->{mod.DestinationStateName})");
                    break;
                }
                case "RemoveTransition":
                {
                    var layer = GetLayer(controller, mod.LayerName);
                    var source = GetState(layer.stateMachine, mod.SourceStateName);
                    var tr = source.transitions.FirstOrDefault(t => t.destinationState?.name == mod.DestinationStateName);
                    if (tr == null)
                        throw new System.Exception($"No transition {mod.SourceStateName}->{mod.DestinationStateName}.");
                    source.RemoveTransition(tr);
                    applied.Add($"RemoveTransition({mod.SourceStateName}->{mod.DestinationStateName})");
                    break;
                }
                case "AddAnyStateTransition":
                {
                    var layer = GetLayer(controller, mod.LayerName);
                    var dest = GetState(layer.stateMachine, mod.DestinationStateName);
                    var tr = layer.stateMachine.AddAnyStateTransition(dest);
                    ConfigureTransition(tr, mod);
                    applied.Add($"AddAnyStateTransition(->{mod.DestinationStateName})");
                    break;
                }
                case "SetStateMotion":
                {
                    var layer = GetLayer(controller, mod.LayerName);
                    var state = GetState(layer.stateMachine, mod.StateName);
                    if (string.IsNullOrEmpty(mod.MotionAssetPath))
                        throw new System.Exception("motionAssetPath is required for SetStateMotion.");
                    var motion = AssetDatabase.LoadAssetAtPath<Motion>(mod.MotionAssetPath);
                    if (motion == null)
                        throw new System.Exception($"Motion not found at '{mod.MotionAssetPath}'.");
                    state.motion = motion;
                    applied.Add($"SetStateMotion({mod.LayerName}, {mod.StateName})");
                    break;
                }
                case "SetStateSpeed":
                {
                    var layer = GetLayer(controller, mod.LayerName);
                    var state = GetState(layer.stateMachine, mod.StateName);
                    if (!mod.Speed.HasValue)
                        throw new System.Exception("speed is required for SetStateSpeed.");
                    state.speed = mod.Speed.Value;
                    applied.Add($"SetStateSpeed({mod.LayerName}, {mod.StateName}, {mod.Speed.Value})");
                    break;
                }
                default:
                    throw new System.Exception($"Unknown modification type '{mod.Type}'.");
            }
        }

        private static void AddParameter(AnimatorController controller, Modification mod)
        {
            if (string.IsNullOrEmpty(mod.ParameterName))
                throw new System.Exception("parameterName is required for AddParameter.");
            if (string.IsNullOrEmpty(mod.ParameterType))
                throw new System.Exception("parameterType is required for AddParameter (Float / Int / Bool / Trigger).");
            if (!System.Enum.TryParse(mod.ParameterType, true, out AnimatorControllerParameterType type))
                throw new System.Exception($"Invalid parameterType '{mod.ParameterType}'. Valid: Float, Int, Bool, Trigger.");

            controller.AddParameter(mod.ParameterName, type);

            // Apply the default value if provided.
            var parameters = controller.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name != mod.ParameterName) continue;
                switch (type)
                {
                    case AnimatorControllerParameterType.Float:
                        parameters[i].defaultFloat = mod.DefaultFloat ?? 0f;
                        break;
                    case AnimatorControllerParameterType.Int:
                        parameters[i].defaultInt = mod.DefaultInt ?? 0;
                        break;
                    case AnimatorControllerParameterType.Bool:
                        parameters[i].defaultBool = mod.DefaultBool ?? false;
                        break;
                }
                break;
            }
            controller.parameters = parameters;
        }

        private static void RemoveParameter(AnimatorController controller, Modification mod)
        {
            if (string.IsNullOrEmpty(mod.ParameterName))
                throw new System.Exception("parameterName is required for RemoveParameter.");
            var param = controller.parameters.FirstOrDefault(p => p.name == mod.ParameterName);
            if (param == null)
                throw new System.Exception($"Parameter '{mod.ParameterName}' not found.");
            controller.RemoveParameter(param);
        }

        private static AnimatorState AddState(AnimatorController controller, Modification mod)
        {
            if (string.IsNullOrEmpty(mod.LayerName))
                throw new System.Exception("layerName is required for AddState.");
            if (string.IsNullOrEmpty(mod.StateName))
                throw new System.Exception("stateName is required for AddState.");
            var layer = GetLayer(controller, mod.LayerName);
            return layer.stateMachine.AddState(mod.StateName);
        }

        private static void ConfigureTransition(AnimatorStateTransition transition, Modification mod)
        {
            if (mod.HasExitTime.HasValue) transition.hasExitTime = mod.HasExitTime.Value;
            if (mod.ExitTime.HasValue) transition.exitTime = mod.ExitTime.Value;
            if (mod.Duration.HasValue) transition.duration = mod.Duration.Value;
            if (mod.HasFixedDuration.HasValue) transition.hasFixedDuration = mod.HasFixedDuration.Value;

            if (mod.Conditions == null) return;
            foreach (var cond in mod.Conditions)
            {
                if (string.IsNullOrEmpty(cond.Parameter)) continue;
                var mode = AnimatorConditionMode.If;
                if (!string.IsNullOrEmpty(cond.Mode) &&
                    !System.Enum.TryParse(cond.Mode, true, out mode))
                {
                    throw new System.Exception($"Invalid condition mode '{cond.Mode}'. Valid: If, IfNot, Greater, Less, Equals, NotEqual.");
                }
                transition.AddCondition(mode, cond.Threshold ?? 0f, cond.Parameter);
            }
        }

        private static int GetLayerIndex(AnimatorController controller, string layerName)
        {
            for (int i = 0; i < controller.layers.Length; i++)
                if (controller.layers[i].name == layerName) return i;
            throw new System.Exception($"Layer '{layerName}' not found.");
        }

        private static AnimatorControllerLayer GetLayer(AnimatorController controller, string layerName)
        {
            var layer = controller.layers.FirstOrDefault(l => l.name == layerName);
            if (layer == null || layer.stateMachine == null)
                throw new System.Exception($"Layer '{layerName}' not found.");
            return layer;
        }

        private static AnimatorState GetState(AnimatorStateMachine stateMachine, string stateName)
        {
            var child = stateMachine.states.FirstOrDefault(s => s.state.name == stateName);
            if (child.state == null)
                throw new System.Exception($"State '{stateName}' not found in this layer.");
            return child.state;
        }

        // =====================================================================
        // Serializers
        // =====================================================================

        private static void AppendParameter(StringBuilder sb, AnimatorControllerParameter p)
        {
            sb.Append('{');
            sb.Append("\"name\":").Append(AnimationJson.Esc(p.name)).Append(',');
            sb.Append("\"type\":").Append(AnimationJson.Esc(p.type.ToString())).Append(',');
            sb.Append("\"defaultFloat\":").Append(p.defaultFloat.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"defaultInt\":").Append(p.defaultInt).Append(',');
            sb.Append("\"defaultBool\":").Append(p.defaultBool ? "true" : "false");
            sb.Append('}');
        }

        private static void AppendLayer(StringBuilder sb, AnimatorControllerLayer layer)
        {
            sb.Append('{');
            sb.Append("\"name\":").Append(AnimationJson.Esc(layer.name)).Append(',');
            sb.Append("\"defaultWeight\":").Append(layer.defaultWeight).Append(',');
            sb.Append("\"blendingMode\":").Append(AnimationJson.Esc(layer.blendingMode.ToString())).Append(',');
            sb.Append("\"iKPass\":").Append(layer.iKPass ? "true" : "false").Append(',');

            var sm = layer.stateMachine;
            sb.Append("\"defaultStateName\":").Append(
                sm?.defaultState == null ? "null" : AnimationJson.Esc(sm.defaultState.name)).Append(',');

            // States.
            sb.Append("\"states\":[");
            if (sm != null)
            {
                for (int i = 0; i < sm.states.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendState(sb, sm.states[i].state);
                }
            }
            sb.Append("],");

            // Sub-state machines.
            sb.Append("\"subStateMachines\":[");
            if (sm != null)
            {
                for (int i = 0; i < sm.stateMachines.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(AnimationJson.Esc(sm.stateMachines[i].stateMachine.name));
                }
            }
            sb.Append("],");

            // Any-state transitions.
            sb.Append("\"anyStateTransitions\":[");
            if (sm != null)
            {
                for (int i = 0; i < sm.anyStateTransitions.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendTransition(sb, sm.anyStateTransitions[i]);
                }
            }
            sb.Append("]}");
        }

        private static void AppendState(StringBuilder sb, AnimatorState state)
        {
            sb.Append('{');
            sb.Append("\"name\":").Append(AnimationJson.Esc(state.name)).Append(',');
            sb.Append("\"tag\":").Append(AnimationJson.Esc(state.tag)).Append(',');
            sb.Append("\"speed\":").Append(state.speed.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"motionName\":").Append(
                state.motion == null ? "null" : AnimationJson.Esc(state.motion.name)).Append(',');
            sb.Append("\"writeDefaultValues\":").Append(state.writeDefaultValues ? "true" : "false").Append(',');

            // Transitions out of this state.
            sb.Append("\"transitions\":[");
            for (int i = 0; i < state.transitions.Length; i++)
            {
                if (i > 0) sb.Append(',');
                AppendTransition(sb, state.transitions[i]);
            }
            sb.Append("]}");
        }

        private static void AppendTransition(StringBuilder sb, AnimatorStateTransition tr)
        {
            sb.Append('{');
            sb.Append("\"destinationStateName\":").Append(
                tr.destinationState == null ? "null" : AnimationJson.Esc(tr.destinationState.name)).Append(',');
            sb.Append("\"hasExitTime\":").Append(tr.hasExitTime ? "true" : "false").Append(',');
            sb.Append("\"exitTime\":").Append(tr.exitTime.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"duration\":").Append(tr.duration.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"hasFixedDuration\":").Append(tr.hasFixedDuration ? "true" : "false").Append(',');

            sb.Append("\"conditions\":[");
            var conds = tr.conditions;
            for (int i = 0; i < conds.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append("\"parameter\":").Append(AnimationJson.Esc(conds[i].parameter)).Append(',');
                sb.Append("\"mode\":").Append(AnimationJson.Esc(conds[i].mode.ToString())).Append(',');
                sb.Append("\"threshold\":").Append(conds[i].threshold.ToString("R", CultureInfo.InvariantCulture));
                sb.Append('}');
            }
            sb.Append("]}");
        }

        private static string JsonStringArray(List<string> items)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(AnimationJson.Esc(items[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
