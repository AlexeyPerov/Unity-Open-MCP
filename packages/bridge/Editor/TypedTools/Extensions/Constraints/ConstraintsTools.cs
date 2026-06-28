// M20 Plan 3 / T20.3.3 — Constraints & LOD embedded domain tools.
//
// Three typed tools covering the animation-constraint + LODGroup layer the
// competitor (AnkleBreaker) ships as its Constraints & LOD category.
//
//   constraint_add        — add a constraint component (PositionConstraint /
//                           RotationConstraint / AimConstraint /
//                           ParentConstraint / ScaleConstraint) to a host, with
//                           an optional source Transform + weight +
//                           constraintActive.
//   lod_group_configure   — configure a LODGroup on a host (fade mode,
//                           animate cross-fading, allocate the LOD array).
//   lod_add_level         — add a LOD entry to a LODGroup at an index, with a
//                           screen-relative transition height + renderers
//                           (resolved from GameObject paths under the host).
//
// The PositionConstraint / RotationConstraint / AimConstraint /
// ParentConstraint / ScaleConstraint types live in the built-in
// UnityEngine.AnimationModule (UnityEngine.Animations namespace); LODGroup
// lives in UnityEngine.CoreModule. Both are present in every Unity install, so
// this domain ships UNGATED — no UNITY_OPEN_MCP_EXT_CONSTRAINTS define. The
// `constraints` tool group is still hidden from ListTools until the session
// activates it via unity_open_mcp_manage_tools (group visibility is a session
// concern, independent of compile-gating).
//
// Naming: `unity_open_mcp_constraint_add` (singular domain prefix — the
// catalog minimum is one constraint tool) and `unity_open_mcp_lod_*` (LOD
// tools). Both prefixes fold into one `constraints` group because the two
// concerns are small and closely related (level-of-detail is a rendering-side
// "constraint" on which meshes draw).
#pragma warning disable CS0618
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Extensions.Constraints
{
    // M20 Plan 3 / T20.3.3 — Constraints & LOD tools. Registry-discovered via
    // [BridgeToolType] + [BridgeTool]. All three tools are mutating (constraint
    // + LODGroup creation / configuration all write scene state) and declare
    // IsMutating = true with a snake_case paths_hint (bound to the C# pathsHint
    // parameter by name) so the gate can scope the verify checkpoint.
    [BridgeToolType]
    public static class ConstraintsTools
    {
        // =====================================================================
        // Constraint — add (Position / Rotation / Aim / Parent / Scale)
        // =====================================================================

        // Add a constraint component to a host GameObject and optionally seed a
        // source Transform + weight + activation state. Mirrors AnkleBreaker's
        // unity_constraint_add param shape (type + source + activate). Idempotent
        // — re-using an existing constraint of the same type reports added:false
        // (the source / weight / activation are still applied).
        [BridgeTool("unity_open_mcp_constraint_add",
            Title = "Constraints: Add Constraint",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "constraints")]
        [System.ComponentModel.Description(
            "Add an animation constraint component to a GameObject. " +
            "constraint_type is one of PositionConstraint | RotationConstraint | " +
            "AimConstraint | ParentConstraint | ScaleConstraint. Optional: " +
            "source_path (the constrained-to Transform, resolved to a GameObject " +
            "and its Transform taken), weight (0-1, default 1), constraint_active " +
            "(default true). Idempotent — re-using an existing constraint of the " +
            "same type reports added:false (source / weight / activation still " +
            "applied). Mutating: runs the gate path; paths_hint is the host scene " +
            "path.")]
        public static string ConstraintAdd(
            int instance_id = 0,
            string path = null,
            string name = null,
            string constraint_type = null,
            string source_path = null,
            int source_instance_id = 0,
            string source_name = null,
            float weight = 1f,
            bool constraint_active = true,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return ConstraintsJson.Error("paths_hint_required",
                    "constraint_add is mutating; pass a non-empty paths_hint scoped " +
                    "to the host's scene path.");

            if (string.IsNullOrEmpty(constraint_type))
                return ConstraintsJson.Error("missing_parameter",
                    "'constraint_type' is required (PositionConstraint | " +
                    "RotationConstraint | AimConstraint | ParentConstraint | " +
                    "ScaleConstraint).");

            var host = ConstraintsTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            // Resolve the constraint type by friendly name. All five live in
            // UnityEngine.Animations (UnityEngine.AnimationModule).
            var type = ResolveConstraintType(constraint_type);
            if (type == null)
                return ConstraintsJson.Error("invalid_constraint_type",
                    "Unknown constraint_type '" + constraint_type + "'. Use " +
                    "PositionConstraint | RotationConstraint | AimConstraint | " +
                    "ParentConstraint | ScaleConstraint.");

            // Resolve the optional source Transform. The ConstraintSource struct
            // takes a Transform (not a GameObject); we resolve the source as a
            // GameObject and take its transform. Missing source is NOT an error —
            // an agent may add the component first and wire the source via
            // component_modify later.
            Transform sourceTransform = null;
            if (!string.IsNullOrEmpty(source_path) || source_instance_id != 0 ||
                !string.IsNullOrEmpty(source_name))
            {
                var sourceGo = ConstraintsTargets.Resolve(
                    source_instance_id, source_path, source_name);
                if (sourceGo == null)
                    return ConstraintsJson.Error("source_not_found",
                        "Source GameObject not resolved. Address by " +
                        "source_instance_id > source_path > source_name.");
                sourceTransform = sourceGo.transform;
            }

            Undo.RecordObject(host, "Add constraint");

            // Idempotent: re-use an existing constraint of the same type.
            var existing = host.GetComponent(type);
            bool added = false;
            IConstraint constraint;
            if (existing == null)
            {
                constraint = Undo.AddComponent(host, type) as IConstraint;
                if (constraint == null)
                    return ConstraintsJson.Error("constraint_add_failed",
                        "Failed to add constraint of type '" + constraint_type + "'.");
                added = true;
            }
            else
            {
                constraint = existing as IConstraint;
                if (constraint == null)
                    return ConstraintsJson.Error("constraint_add_failed",
                        "Existing component of type '" + constraint_type +
                        "' is not an IConstraint.");
            }

            // Seed the source when provided. ConstraintSource is a struct with a
            // sourceTransform + weight. AddSource returns the source index.
            bool sourceAdded = false;
            int sourceIndex = -1;
            if (sourceTransform != null)
            {
                var src = new ConstraintSource
                {
                    sourceTransform = sourceTransform,
                    weight = Mathf.Clamp01(weight),
                };
                sourceIndex = constraint.AddSource(src);
                sourceAdded = true;
            }

            // Unity's IConstraint exposes weight (the blended constraint weight)
            // + constraintActive + enabled. Set weight only when a source was
            // provided (otherwise the per-source weight is the meaningful one);
            // always honor constraint_active since the agent asked for it.
            constraint.constraintActive = constraint_active;

            EditorUtility.SetDirty(host);

            var sb = new StringBuilder(320);
            sb.Append("\"constraint\":{");
            sb.Append("\"added\":").Append(added ? "true" : "false").Append(',');
            sb.Append("\"type\":").Append(ConstraintsJson.Esc(type.Name)).Append(',');
            sb.Append("\"instanceId\":").Append((constraint as Component).GetInstanceID()).Append(',');
            sb.Append("\"path\":").Append(ConstraintsJson.Esc(ConstraintsTargets.BuildPath(host))).Append(',');
            sb.Append("\"constraintActive\":").Append(constraint.constraintActive ? "true" : "false").Append(',');
            sb.Append("\"sourceCount\":").Append(constraint.sourceCount).Append(',');
            sb.Append("\"sourceAdded\":").Append(sourceAdded ? "true" : "false");
            if (sourceAdded)
            {
                sb.Append(',').Append("\"sourceIndex\":").Append(sourceIndex);
                sb.Append(',').Append("\"sourceWeight\":").Append(weight.ToString("0.###",
                    System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append('}');
            return ConstraintsJson.Ok(sb.ToString());
        }

        // =====================================================================
        // LODGroup — configure (fade mode / cross-fade / allocate LOD array)
        // =====================================================================

        // Configure a LODGroup on a host GameObject. Idempotent — re-using an
        // existing LODGroup reports added:false (the fade mode / cross-fade /
        // LOD array are still applied). Mirrors AnkleBreaker's unity_lod_create
        // param shape (number of LOD levels). When lod_count is provided, the
        // LOD array is (re)allocated with placeholder LOD entries whose renderers
        // are empty — agents wire the renderers per level via lod_add_level.
        [BridgeTool("unity_open_mcp_lod_group_configure",
            Title = "Constraints: Configure LOD Group",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "constraints")]
        [System.ComponentModel.Description(
            "Configure a LODGroup on a GameObject. Optional: fade_mode " +
            "(None | SpeedTree | CrossFade, default None — leaves existing when " +
            "omitted), animate_cross_fading (default false), lod_count (allocates " +
            "the LOD array with that many levels; renderers start empty — wire " +
            "them via lod_add_level). Idempotent — re-using an existing LODGroup " +
            "reports added:false (configuration still applied). Mutating: runs the " +
            "gate path; paths_hint is the host scene path.")]
        public static string LodGroupConfigure(
            int instance_id = 0,
            string path = null,
            string name = null,
            string fade_mode = null,
            bool animate_cross_fading = false,
            int lod_count = -1,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return ConstraintsJson.Error("paths_hint_required",
                    "lod_group_configure is mutating; pass a non-empty paths_hint.");

            var host = ConstraintsTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            Undo.RecordObject(host, "Configure LODGroup");

            // Idempotent: re-use an existing LODGroup.
            var group = host.GetComponent<LODGroup>();
            bool added = false;
            if (group == null)
            {
                group = Undo.AddComponent<LODGroup>(host);
                added = true;
            }

            // Apply fade mode when provided. LODFadeMode is an enum
            // (None / SpeedTree / CrossFade). Leave the existing value untouched
            // when fade_mode is omitted (the agent may want to change only the
            // cross-fade flag or the LOD count).
            bool fadeModeApplied = false;
            if (!string.IsNullOrEmpty(fade_mode))
            {
                if (System.Enum.TryParse<LODFadeMode>(fade_mode, true, out var mode))
                {
                    group.fadeMode = mode;
                    fadeModeApplied = true;
                }
                else
                {
                    return ConstraintsJson.Error("invalid_fade_mode",
                        "Unknown fade_mode '" + fade_mode + "'. Use None | " +
                        "SpeedTree | CrossFade.");
                }
            }

            group.animateCrossFading = animate_cross_fading;

            // Allocate the LOD array when lod_count is requested. Unity's LOD
            // array is monotonically decreasing in screenRelativeTransitionHeight
            // (the last LOD is implicitly 0). We seed descending placeholder
            // heights so the array is valid for SetLODs; agents then replace each
            // entry's renderers + height via lod_add_level.
            bool lodCountApplied = false;
            if (lod_count >= 0)
            {
                if (lod_count == 0)
                {
                    return ConstraintsJson.Error("invalid_lod_count",
                        "lod_count must be >= 1 (a LODGroup needs at least one " +
                        "level).");
                }
                if (lod_count > 8)
                {
                    return ConstraintsJson.Error("invalid_lod_count",
                        "lod_count must be <= 8 (Unity caps LODGroup at 8 levels).");
                }

                var lods = new LOD[lod_count];
                // Seed descending heights: e.g. for lod_count=3 → 0.6, 0.3, 0.0.
                // The last entry is always 0 (Unity's "culled" band). This keeps
                // SetLODs happy (the array must be sorted descending) while
                // leaving the renderers empty for the agent to fill.
                for (int i = 0; i < lod_count; i++)
                {
                    float h = lod_count == 1
                        ? 0f
                        : Mathf.Max(0f, 1f - (float)(i + 1) / lod_count);
                    lods[i] = new LOD { screenRelativeTransitionHeight = h, renderers = new Renderer[0] };
                }
                group.SetLODs(lods);
                lodCountApplied = true;
            }

            EditorUtility.SetDirty(host);

            var sb = new StringBuilder(280);
            sb.Append("\"lodGroup\":{");
            sb.Append("\"added\":").Append(added ? "true" : "false").Append(',');
            sb.Append("\"instanceId\":").Append(group.GetInstanceID()).Append(',');
            sb.Append("\"path\":").Append(ConstraintsJson.Esc(ConstraintsTargets.BuildPath(host))).Append(',');
            sb.Append("\"fadeMode\":").Append(ConstraintsJson.Esc(group.fadeMode.ToString())).Append(',');
            sb.Append("\"animateCrossFading\":").Append(group.animateCrossFading ? "true" : "false").Append(',');
            sb.Append("\"lodCount\":").Append(group.lodCount).Append(',');
            sb.Append("\"fadeModeApplied\":").Append(fadeModeApplied ? "true" : "false").Append(',');
            sb.Append("\"lodCountApplied\":").Append(lodCountApplied ? "true" : "false");
            sb.Append('}');
            return ConstraintsJson.Ok(sb.ToString());
        }

        // =====================================================================
        // LOD — add level (entry at an index with renderers)
        // =====================================================================

        // Add a LOD entry to a LODGroup at an index. The renderers are resolved
        // from an array of GameObject paths (each GameObject must carry a
        // Renderer — usually a MeshRenderer on a child mesh). When the index is
        // within the existing LOD array, the entry is replaced in place; when it
        // equals the array length, a new level is appended. Mirrors the
        // per-level wiring AnkleBreaker's unity_lod_create leaves to the agent.
        [BridgeTool("unity_open_mcp_lod_add_level",
            Title = "Constraints: Add LOD Level",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "constraints")]
        [System.ComponentModel.Description(
            "Add or replace a LOD entry on a LODGroup at an index. Resolves the " +
            "renderers from an array of GameObject paths (each GameObject must " +
            "carry a Renderer — usually a MeshRenderer on a child mesh). When the " +
            "index is within the existing LOD array, the entry is replaced in " +
            "place; when it equals the array length, a new level is appended. The " +
            "host must already carry a LODGroup (use lod_group_configure first). " +
            "Mutating: runs the gate path; paths_hint is the host scene path.")]
        public static string LodAddLevel(
            int instance_id = 0,
            string path = null,
            string name = null,
            int index = 0,
            float screen_relative_transition_height = 0.5f,
            string[] renderers = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return ConstraintsJson.Error("paths_hint_required",
                    "lod_add_level is mutating; pass a non-empty paths_hint.");

            var host = ConstraintsTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var group = host.GetComponent<LODGroup>();
            if (group == null)
                return ConstraintsJson.Error("component_not_found",
                    "Host has no LODGroup. Call lod_group_configure first to add " +
                    "one (and allocate the LOD array).");

            // Validate the index. Append is allowed only at index == lodCount.
            var current = group.GetLODs();
            if (index < 0 || index > current.Length)
                return ConstraintsJson.Error("invalid_index",
                    $"index {index} is out of range. The LODGroup has " +
                    $"{current.Length} level(s); valid indices are 0..{current.Length} " +
                    "(index == lodCount appends).");

            // Resolve the renderers. Each entry is a GameObject path / instance id
            // hint; we resolve to a GameObject and require a Renderer on it. An
            // empty renderers array is allowed (the agent may wire them later via
            // component_modify) — but a LOD with no renderers renders nothing, so
            // we surface the count honestly.
            var resolvedRenderers = new List<Renderer>();
            var unresolved = new List<string>();
            if (renderers != null)
            {
                foreach (var r in renderers)
                {
                    if (string.IsNullOrEmpty(r)) continue;
                    var go = ResolveRendererTarget(r);
                    if (go == null) { unresolved.Add(r); continue; }
                    var rend = go.GetComponent<Renderer>();
                    if (rend == null) { unresolved.Add(r); continue; }
                    resolvedRenderers.Add(rend);
                }
            }

            Undo.RecordObject(group, "Add LOD level");

            // Build the new LOD array. When index < length, replace in place;
            // when index == length, append. We keep the rest of the array intact.
            var newLods = new LOD[index < current.Length ? current.Length : current.Length + 1];
            for (int i = 0; i < newLods.Length; i++)
            {
                if (i == index)
                {
                    newLods[i] = new LOD
                    {
                        screenRelativeTransitionHeight = Mathf.Clamp01(screen_relative_transition_height),
                        renderers = resolvedRenderers.ToArray(),
                    };
                }
                else if (i < current.Length)
                {
                    newLods[i] = current[i];
                }
            }
            group.SetLODs(newLods);

            EditorUtility.SetDirty(group);

            var sb = new StringBuilder(320);
            sb.Append("\"lodLevel\":{");
            sb.Append("\"index\":").Append(index).Append(',');
            sb.Append("\"action\":").Append(ConstraintsJson.Esc(index < current.Length ? "replaced" : "appended")).Append(',');
            sb.Append("\"screenRelativeTransitionHeight\":").Append(screen_relative_transition_height.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"rendererCount\":").Append(resolvedRenderers.Count).Append(',');
            sb.Append("\"lodCount\":").Append(group.lodCount).Append(',');
            sb.Append("\"unresolvedRenderers\":").Append(unresolved.Count);
            sb.Append('}');
            return ConstraintsJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Helpers — type resolution
        // =====================================================================

        private static System.Type ResolveConstraintType(string constraintType)
        {
            switch (constraintType)
            {
                case "PositionConstraint": return typeof(PositionConstraint);
                case "RotationConstraint": return typeof(RotationConstraint);
                case "AimConstraint": return typeof(AimConstraint);
                case "ParentConstraint": return typeof(ParentConstraint);
                case "ScaleConstraint": return typeof(ScaleConstraint);
                default: return null;
            }
        }

        // Resolve a renderer target. Accepts an instance-id hint ("iid:12345") or
        // a hierarchy path. Mirrors the host resolver but is per-entry (the LOD
        // renderers array is a list, not a single target).
        private static GameObject ResolveRendererTarget(string hint)
        {
            if (string.IsNullOrEmpty(hint)) return null;

            // "iid:<n>" form — explicit instance id.
            if (hint.StartsWith("iid:") &&
                int.TryParse(hint.Substring(4), out var iid) && iid != 0)
            {
                var obj = EditorUtility.InstanceIDToObject(iid);
                if (obj is GameObject go) return go;
                return null;
            }

            // Otherwise treat as a hierarchy path (slash-separated from a root).
            return ConstraintsTargets.FindByPath(hint);
        }

        // =====================================================================
        // Helpers — common
        // =====================================================================

        private static string TargetNotFound()
            => ConstraintsJson.Error("target_not_found",
                "No GameObject resolved. Address by instance_id > path > name.");
    }
}
