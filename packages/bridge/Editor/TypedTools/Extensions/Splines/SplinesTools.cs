// M18 Plan 7 / T18.7.3 — Splines embedded domain tools.
//
// Compile-gated by UNITY_OPEN_MCP_EXT_SPLINES (see SplinesJson.cs for the gate
// rationale). Seven typed tools for in-editor spline authoring:
//   - container_create: GameObject + SplineContainer (primary spline)
//   - add_knot: append a BezierKnot (position / rotation / tangent mode)
//   - set_knot: replace the knot at an index
//   - set_tangent_mode: AutoSmooth / Broken / Mirrored / Linear / BezierSmooth
//   - evaluate: position + tangent at normalized ratio t (read-only)
//   - get_knots: list every knot's position / rotation / tangents (read-only)
//   - modify: reflective field-patch escape hatch for niche Spline fields
//
// Tools that mutate a scene GameObject run the gate path with paths_hint scoped
// to the host's scene path. container_create adds a new GameObject to the
// active scene — its paths_hint is the active scene path.
//
// Splines is compile-gate-only: com.unity.splines has a single stable public
// API across 1.x and 2.x, so there is no version-split reflection layer (the
// Cinemachine 2.x/3.x case). When the package is absent the tools are not
// compiled in and the capability surface reports the domain as
// `available: false (dependency missing: com.unity.splines)`.
//
// Naming: `unity_open_mcp_splines_<action>` (snake_case domain prefix).
#if UNITY_OPEN_MCP_EXT_SPLINES
using System.Text;
using UnityEngine;
using UnityEditor;
using Unity.Mathematics;
using UnityEngine.Splines;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.Splines
{
    [BridgeToolType]
    public static class SplinesTools
    {
        // =====================================================================
        // Container create
        // =====================================================================

        // Create a new GameObject carrying a SplineContainer in the active
        // scene. A fresh container gets one empty primary spline so
        // spline_index 0 is valid immediately. Returns the new GameObject's
        // instance id + path so the next call can address it.
        [BridgeTool("unity_open_mcp_splines_container_create",
            Title = "Splines: Create SplineContainer",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "splines")]
        [System.ComponentModel.Description(
            "Create a new GameObject carrying a SplineContainer component in " +
            "the active scene. The container is initialized with one empty " +
            "primary spline (spline_index 0). Optionally set name, position, " +
            "rotation, parent, and closed state. Mutating: runs the gate path; " +
            "paths_hint is the active scene path (the new GameObject lives " +
            "there). Requires the com.unity.splines package installed in the " +
            "project.")]
        public static string ContainerCreate(
            string name = null,
            string parent_path = null,
            string position = null,
            string rotation = null,
            bool closed = false,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return SplinesJson.Error("paths_hint_required",
                    "splines_container_create is mutating; pass a non-empty " +
                    "paths_hint scoped to the active scene path.");

            var go = new GameObject(string.IsNullOrEmpty(name) ? "Spline" : name);

            // Resolve optional parent. parent_path is a slash-separated hierarchy.
            Transform parent = null;
            if (!string.IsNullOrEmpty(parent_path))
            {
                var parentGo = SplinesTargets.FindByPath(parent_path);
                if (parentGo == null)
                {
                    Object.DestroyImmediate(go);
                    return SplinesJson.Error("parent_not_found",
                        $"No GameObject at parent_path '{parent_path}'.");
                }
                parent = parentGo.transform;
            }

            Undo.RegisterCreatedObjectUndo(go, "Create SplineContainer");
            if (parent != null) go.transform.SetParent(parent, false);

            // Apply transform. position/rotation are world space unless a parent
            // exists (then they're local — matches the ProBuilder pack's
            // with-parent behavior).
            if (!string.IsNullOrEmpty(position))
            {
                var p = ParseVector3(position, Vector3.zero);
                if (parent != null) go.transform.localPosition = p;
                else go.transform.position = p;
            }
            if (!string.IsNullOrEmpty(rotation))
            {
                var r = ParseVector3(rotation, Vector3.zero);
                if (parent != null) go.transform.localEulerAngles = r;
                else go.transform.eulerAngles = r;
            }

            var container = Undo.AddComponent<SplineContainer>(go);
            // A fresh SplineContainer may serialize an empty primary spline. Ensure
            // spline_index 0 is valid immediately so the next add_knot call works
            // without a separate init step.
            if (container.Spline == null)
                container.Spline = new Spline();
            container.Spline.Closed = closed;

            EditorUtility.SetDirty(go);

            var sb = new StringBuilder(160);
            sb.Append("\"container\":{");
            sb.Append("\"instanceId\":").Append(InstanceId.ToJson(go)).Append(',');
            sb.Append("\"path\":").Append(SplinesJson.Esc(SplinesTargets.BuildPath(go))).Append(',');
            sb.Append("\"splineCount\":").Append(container.Splines.Count).Append(',');
            sb.Append("\"closed\":").Append(closed ? "true" : "false");
            sb.Append('}');
            return SplinesJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Knot add / set
        // =====================================================================

        // Append a BezierKnot to a spline on a container. position is required
        // (x,y,z); rotation and tangent_in/tangent_out are optional. When a
        // tangent_mode is given the knot is added with that mode (the spline
        // recomputes tangents for AutoSmooth).
        [BridgeTool("unity_open_mcp_splines_add_knot",
            Title = "Splines: Add Knot",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "splines")]
        [System.ComponentModel.Description(
            "Append a BezierKnot to a spline on a SplineContainer. Requires " +
            "position ('x,y,z'); rotation ('x,y,z' Euler degrees) and " +
            "tangent_in/tangent_out ('x,y,z') are optional. When tangent_mode " +
            "is set (AutoSmooth / Broken / Mirrored / Linear / BezierSmooth) " +
            "the knot adopts it and the spline recomputes tangents. Returns the " +
            "new knot index. Mutating: runs the gate path; paths_hint is the " +
            "host's scene path.")]
        public static string AddKnot(
            int instance_id = 0,
            string path = null,
            string name = null,
            int spline_index = 0,
            string position = null,
            string rotation = null,
            string tangent_in = null,
            string tangent_out = null,
            string tangent_mode = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return SplinesJson.Error("paths_hint_required",
                    "splines_add_knot is mutating; pass a non-empty paths_hint.");

            if (string.IsNullOrEmpty(position))
                return SplinesJson.Error("missing_parameter",
                    "'position' is required ('x,y,z').");

            var host = SplinesTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var container = host.GetComponent<SplineContainer>();
            if (container == null)
                return SplinesJson.Error("component_not_found",
                    "Target has no SplineContainer. Create one with " +
                    "splines_container_create first.");

            var spline = GetSpline(container, spline_index, out var splError);
            if (spline == null)
                return SplinesJson.Error("spline_not_found", splError);

            var pos = ParseFloat3(position, float3.zero);
            var knot = MakeKnot(pos, rotation, tangent_in, tangent_out);
            Undo.RecordObject(container, "Add spline knot");

            if (!string.IsNullOrEmpty(tangent_mode))
            {
                if (!TryParseTangentMode(tangent_mode, out var mode, out var modeErr))
                    return SplinesJson.Error("invalid_tangent_mode", modeErr);
                spline.Add(knot, mode);
            }
            else
            {
                spline.Add(knot);
            }

            EditorUtility.SetDirty(container);
            int index = spline.Count - 1;

            var sb = new StringBuilder(140);
            sb.Append("\"knot\":{");
            sb.Append("\"added\":true,");
            sb.Append("\"index\":").Append(index).Append(',');
            sb.Append("\"position\":").Append(SplinesJson.Vec3(ToVec3(knot.Position)));
            sb.Append('}');
            return SplinesJson.Ok(sb.ToString());
        }

        // Replace the knot at an index. Accepts the same fields as add_knot.
        [BridgeTool("unity_open_mcp_splines_set_knot",
            Title = "Splines: Set Knot",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "splines")]
        [System.ComponentModel.Description(
            "Replace the BezierKnot at knot_index on a spline. Provide any of " +
            "position ('x,y,z'), rotation ('x,y,z' Euler), tangent_in/tangent_out " +
            "('x,y,z'); omitted fields keep the current knot's value. Mutating: " +
            "runs the gate path; paths_hint is the host's scene path.")]
        public static string SetKnot(
            int instance_id = 0,
            string path = null,
            string name = null,
            int spline_index = 0,
            int knot_index = -1,
            string position = null,
            string rotation = null,
            string tangent_in = null,
            string tangent_out = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return SplinesJson.Error("paths_hint_required",
                    "splines_set_knot is mutating; pass a non-empty paths_hint.");

            var host = SplinesTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var container = host.GetComponent<SplineContainer>();
            if (container == null)
                return SplinesJson.Error("component_not_found",
                    "Target has no SplineContainer.");

            var spline = GetSpline(container, spline_index, out var splError);
            if (spline == null)
                return SplinesJson.Error("spline_not_found", splError);

            if (knot_index < 0 || knot_index >= spline.Count)
                return SplinesJson.Error("knot_not_found",
                    $"knot_index {knot_index} out of range (spline has " +
                    $"{spline.Count} knot(s)).");

            // Start from the current knot so omitted fields are preserved.
            var current = spline[knot_index];
            var pos = string.IsNullOrEmpty(position)
                ? current.Position : ParseFloat3(position, current.Position);
            var rot = string.IsNullOrEmpty(rotation)
                ? current.Rotation : quaternion.Euler(ParseVector3(rotation, Vector3.zero) * Mathf.Deg2Rad);
            var tin = string.IsNullOrEmpty(tangent_in)
                ? current.TangentIn : ParseFloat3(tangent_in, current.TangentIn);
            var tout = string.IsNullOrEmpty(tangent_out)
                ? current.TangentOut : ParseFloat3(tangent_out, current.TangentOut);

            var knot = new BezierKnot(pos, tin, tout, rot);

            Undo.RecordObject(container, "Set spline knot");
            spline[knot_index] = knot;
            EditorUtility.SetDirty(container);

            var sb = new StringBuilder(160);
            sb.Append("\"knot\":{");
            sb.Append("\"index\":").Append(knot_index).Append(',');
            sb.Append("\"position\":").Append(SplinesJson.Vec3(ToVec3(knot.Position)));
            sb.Append('}');
            return SplinesJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Tangent mode
        // =====================================================================

        // Set the TangentMode for one knot (knot_index) or the whole spline
        // (knot_index = -1). Names: AutoSmooth / Broken / Mirrored / Linear /
        // BezierSmooth (legacy Continuous / BrokenMirrored also parse).
        [BridgeTool("unity_open_mcp_splines_set_tangent_mode",
            Title = "Splines: Set Tangent Mode",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "splines")]
        [System.ComponentModel.Description(
            "Set the TangentMode on a spline. Pass knot_index to target one " +
            "knot, or knot_index = -1 to set the whole spline. Mode names: " +
            "AutoSmooth, Broken, Mirrored, Linear, BezierSmooth (legacy " +
            "Continuous / BrokenMirrored also accepted). Mutating: runs the " +
            "gate path; paths_hint is the host's scene path.")]
        public static string SetTangentMode(
            int instance_id = 0,
            string path = null,
            string name = null,
            int spline_index = 0,
            int knot_index = -1,
            string tangent_mode = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return SplinesJson.Error("paths_hint_required",
                    "splines_set_tangent_mode is mutating; pass a non-empty paths_hint.");

            if (string.IsNullOrEmpty(tangent_mode))
                return SplinesJson.Error("missing_parameter",
                    "'tangent_mode' is required (AutoSmooth / Broken / Mirrored / " +
                    "Linear / BezierSmooth).");

            if (!TryParseTangentMode(tangent_mode, out var mode, out var modeErr))
                return SplinesJson.Error("invalid_tangent_mode", modeErr);

            var host = SplinesTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var container = host.GetComponent<SplineContainer>();
            if (container == null)
                return SplinesJson.Error("component_not_found",
                    "Target has no SplineContainer.");

            var spline = GetSpline(container, spline_index, out var splError);
            if (spline == null)
                return SplinesJson.Error("spline_not_found", splError);

            Undo.RecordObject(container, "Set spline tangent mode");
            if (knot_index < 0)
            {
                spline.SetTangentMode(mode);
            }
            else
            {
                if (knot_index >= spline.Count)
                {
                    return SplinesJson.Error("knot_not_found",
                        $"knot_index {knot_index} out of range (spline has " +
                        $"{spline.Count} knot(s)).");
                }
                spline.SetTangentMode(knot_index, mode);
            }
            EditorUtility.SetDirty(container);

            var sb = new StringBuilder(120);
            sb.Append("\"mode\":").Append(SplinesJson.Esc(mode.ToString())).Append(',');
            sb.Append("\"scope\":").Append(knot_index < 0
                ? SplinesJson.Esc("spline")
                : SplinesJson.Esc("knot")).Append(',');
            sb.Append("\"knotIndex\":").Append(knot_index);
            return SplinesJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Evaluate (read-only)
        // =====================================================================

        // Evaluate the world-space position + tangent at normalized ratio t
        // (0..1) on the primary spline. Read-only, gate-free — agents use it to
        // verify a path or sample positions for object placement.
        [BridgeTool("unity_open_mcp_splines_evaluate",
            Title = "Splines: Evaluate",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "splines")]
        [System.ComponentModel.Description(
            "Evaluate a SplineContainer's spline at normalized ratio t (0..1). " +
            "Returns world-space position + tangent (direction) + up vector. " +
            "Read-only, gate-free. Address the host by instance_id > path > name. " +
            "Use this to sample positions for object placement along a path.")]
        public static string Evaluate(
            int instance_id = 0,
            string path = null,
            string name = null,
            int spline_index = 0,
            float t = 0.5f)
        {
            var host = SplinesTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var container = host.GetComponent<SplineContainer>();
            if (container == null)
                return SplinesJson.Error("component_not_found",
                    "Target has no SplineContainer.");

            var spline = GetSpline(container, spline_index, out var splError);
            if (spline == null)
                return SplinesJson.Error("spline_not_found", splError);

            if (spline.Count < 2)
                return SplinesJson.Error("spline_too_short",
                    "Spline has fewer than 2 knots — nothing to evaluate. Add " +
                    "knots with splines_add_knot first.");

            var clamped = Mathf.Clamp01(t);
            // SplineContainer.Evaluate(splineIndex, t, out pos, out tan, out up)
            // returns position/direction/up in world space (accounts for the
            // container's transform).
            container.Evaluate(spline_index, clamped, out float3 pos, out float3 tan, out float3 up);
            float length = container.CalculateLength(spline_index);

            var sb = new StringBuilder(200);
            sb.Append("\"t\":").Append(clamped).Append(',');
            sb.Append("\"position\":").Append(SplinesJson.Vec3(ToVec3(pos))).Append(',');
            sb.Append("\"tangent\":").Append(SplinesJson.Vec3(ToVec3(math.normalizesafe(tan)))).Append(',');
            sb.Append("\"up\":").Append(SplinesJson.Vec3(ToVec3(up))).Append(',');
            sb.Append("\"length\":").Append(length);
            return SplinesJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Get knots (read-only discovery)
        // =====================================================================

        // List every knot on a spline with position / rotation / tangent in/out.
        // Read-only, gate-free — agents use it to inspect a spline before
        // mutating, mirroring navigation_list / probuilder_get_mesh_info.
        [BridgeTool("unity_open_mcp_splines_get_knots",
            Title = "Splines: Get Knots",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "splines")]
        [System.ComponentModel.Description(
            "List every knot on a SplineContainer's spline with position, " +
            "rotation, tangent in/out, and tangent mode. Read-only, gate-free. " +
            "Address the host by instance_id > path > name. Use this to inspect " +
            "a spline before mutating, or to discover valid knot indices.")]
        public static string GetKnots(
            int instance_id = 0,
            string path = null,
            string name = null,
            int spline_index = 0)
        {
            var host = SplinesTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var container = host.GetComponent<SplineContainer>();
            if (container == null)
                return SplinesJson.Error("component_not_found",
                    "Target has no SplineContainer.");

            var spline = GetSpline(container, spline_index, out var splError);
            if (spline == null)
                return SplinesJson.Error("spline_not_found", splError);

            var sb = new StringBuilder(512);
            sb.Append("\"splineIndex\":").Append(spline_index).Append(',');
            sb.Append("\"closed\":").Append(spline.Closed ? "true" : "false").Append(',');
            sb.Append("\"knotCount\":").Append(spline.Count).Append(',');
            sb.Append("\"knots\":[");
            for (int i = 0; i < spline.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var k = spline[i];
                var mode = spline.GetTangentMode(i);
                sb.Append('{');
                sb.Append("\"index\":").Append(i).Append(',');
                sb.Append("\"position\":").Append(SplinesJson.Vec3(ToVec3(k.Position))).Append(',');
                sb.Append("\"rotation\":").Append(SplinesJson.Vec3(ToEuler(k.Rotation))).Append(',');
                sb.Append("\"tangentIn\":").Append(SplinesJson.Vec3(ToVec3(k.TangentIn))).Append(',');
                sb.Append("\"tangentOut\":").Append(SplinesJson.Vec3(ToVec3(k.TangentOut))).Append(',');
                sb.Append("\"tangentMode\":").Append(SplinesJson.Esc(mode.ToString()));
                sb.Append('}');
            }
            sb.Append(']');
            return SplinesJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Modify (reflective field setter for advanced spline/container tuning)
        // =====================================================================

        // Reflective field setter for the SplineContainer / Spline — agents use
        // it when a typed mutator does not cover a niche field (e.g.
        // SplineContainer editor-only serialized settings). Each entry is
        // { field, value, type? }; value is a primitive or "x,y,z" vector.
        // Unknown fields are reported as errors and the tool fails atomically.
        [BridgeTool("unity_open_mcp_splines_modify",
            Title = "Splines: Modify",
            IsMutating = true,
            Gate = GateMode.Enforce,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "splines")]
        [System.ComponentModel.Description(
            "Set one or more serialized fields on the SplineContainer component " +
            "(not the Spline itself — use set_knot / set_tangent_mode for knot " +
            "fields). Each entry is { field, value, type? } where type is " +
            "'int' | 'float' | 'bool' | 'string' | 'vector' (default inferred " +
            "from the current value). Mutating: runs the gate path; paths_hint " +
            "is the host's scene path.")]
        public static string Modify(
            int instance_id = 0,
            string path = null,
            string name = null,
            string fields_json = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return SplinesJson.Error("paths_hint_required",
                    "splines_modify is mutating; pass a non-empty paths_hint.");

            var host = SplinesTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var container = host.GetComponent<SplineContainer>();
            if (container == null)
                return SplinesJson.Error("component_not_found",
                    "Target has no SplineContainer.");

            Undo.RecordObject(container, "Modify SplineContainer");
            var applied = new StringBuilder(256);
            var errors = new StringBuilder(256);
            applied.Append('[');
            errors.Append('[');
            bool firstApplied = true;
            bool firstError = true;

            var entries = ParseFieldArray(fields_json);
            if (entries == null)
                return SplinesJson.Error("missing_parameter",
                    "'fields_json' must be a JSON array of {field, value, type?} objects.");

            foreach (var entry in entries)
            {
                var fieldResult = SetField(container, entry);
                if (fieldResult.Ok)
                {
                    if (!firstApplied) applied.Append(',');
                    firstApplied = false;
                    applied.Append('{');
                    applied.Append("\"field\":").Append(SplinesJson.Esc(entry.Field)).Append(',');
                    applied.Append("\"applied\":true");
                    applied.Append('}');
                }
                else
                {
                    if (!firstError) errors.Append(',');
                    firstError = false;
                    errors.Append('{');
                    errors.Append("\"field\":").Append(SplinesJson.Esc(entry.Field)).Append(',');
                    errors.Append("\"error\":").Append(SplinesJson.Esc(fieldResult.Message));
                    errors.Append('}');
                }
            }
            applied.Append(']');
            errors.Append(']');

            EditorUtility.SetDirty(container);
            return SplinesJson.Ok(
                "\"applied\":" + applied.ToString() + ',' +
                "\"errors\":" + errors.ToString());
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static Spline GetSpline(SplineContainer container, int splineIndex, out string error)
        {
            error = null;
            var splines = container.Splines;
            if (splineIndex < 0 || splineIndex >= splines.Count)
            {
                error = $"spline_index {splineIndex} out of range (container has " +
                        $"{splines.Count} spline(s)).";
                return null;
            }
            var spline = splines[splineIndex];
            if (spline == null)
            {
                error = $"Spline at index {splineIndex} is null.";
                return null;
            }
            return spline;
        }

        private static BezierKnot MakeKnot(float3 pos, string rotation, string tangentIn, string tangentOut)
        {
            var rot = string.IsNullOrEmpty(rotation)
                ? quaternion.identity
                : quaternion.Euler(ParseVector3(rotation, Vector3.zero) * Mathf.Deg2Rad);
            var tin = string.IsNullOrEmpty(tangentIn) ? float3.zero : ParseFloat3(tangentIn, float3.zero);
            var tout = string.IsNullOrEmpty(tangentOut) ? float3.zero : ParseFloat3(tangentOut, float3.zero);
            return new BezierKnot(pos, tin, tout, rot);
        }

        private static bool TryParseTangentMode(string s, out TangentMode mode, out string error)
        {
            error = null;
            if (System.Enum.TryParse<TangentMode>(s, true, out mode))
                return true;
            error = $"Unknown tangent_mode '{s}'. Valid: AutoSmooth, Broken, " +
                    "Mirrored, Linear, BezierSmooth.";
            return false;
        }

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
            var field = t.GetField(entry.Field,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            if (field == null)
                return new FieldResult { Ok = false, Message = $"Unknown field '{entry.Field}' on {t.Name}." };

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
            if (targetType == typeof(Vector3)) return ParseVector3(raw, Vector3.zero);
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

        // float3 ↔ Vector3 conversion. Unity.Mathematics float3 and UnityEngine
        // Vector3 are distinct types; we convert explicitly (no implicit cast
        // across all Unity.Mathematics versions).
        private static Vector3 ToVec3(float3 f) => new Vector3(f.x, f.y, f.z);

        private static float3 ParseFloat3(string s, float3 fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            var parts = s.Split(',');
            if (parts.Length != 3) return fallback;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return fallback;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return fallback;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z)) return fallback;
            return new float3(x, y, z);
        }

        private static Vector3 ParseVector3(string s, Vector3 fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            var parts = s.Split(',');
            if (parts.Length != 3) return fallback;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return fallback;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return fallback;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z)) return fallback;
            return new Vector3(x, y, z);
        }

        // quaternion → Euler degrees (for JSON output, matches the rotation
        // input convention). Unity.Mathematics.quaternion and UnityEngine
        // Quaternion are distinct types with no cast operator; rebuild the
        // Quaternion from its (x,y,z,w) components. Both lay out the
        // components identically.
        private static Vector3 ToEuler(quaternion q)
        {
            var v = q.value;
            return new Quaternion(v.x, v.y, v.z, v.w).eulerAngles;
        }

        private static string TargetNotFound()
            => SplinesJson.Error("target_not_found",
                "No GameObject resolved. Address by instance_id > path > name.");
    }
}
#endif
