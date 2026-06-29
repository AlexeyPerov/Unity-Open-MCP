// M20 Plan 6 / T20.6.1 — Cinemachine embedded domain tools (reflection-gated).
//
// REFLECTION-GATED. The owning sub-asmdef has NO `defineConstraints` and NO
// Cinemachine package reference — this assembly ALWAYS compiles. Cinemachine
// types are resolved at call time via the CinemachineVersion reflection layer
// (see CinemachineJson.cs). When Cinemachine 3.x is detected, the tools drive
// CinemachineCamera + CinemachineBrain + the Body/Aim/Noise pipeline via
// reflection; when 2.x is detected or the package is absent, the tools return
// the canonical install/upgrade error envelope.
//
// This is the canonical reflection case named in M18 Plan 1 T18.1.1 task 5
// (version-split API trigger). The 3.x public API is targeted on Unity 6.
//
// Catalog minimum (5 tools): create_camera, set_targets, set_lens, set_body,
// set_noise. Plus the optional brain_ensure, camera_list helpers, and a
// reflective modify escape hatch within the domain. Read-only members
// (camera_list) are gate-free; mutators run the full gate path with paths_hint
// scoped to the host scene path (create_camera adds a new GameObject to the
// active scene).
//
// Naming: `unity_open_mcp_cinemachine_<action>` (snake_case domain prefix).
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Extensions.Cinemachine
{
    [BridgeToolType]
    public static class CinemachineTools
    {
        // =====================================================================
        // create_camera
        // =====================================================================

        // Create a new GameObject carrying a CinemachineCamera (3.x) in the
        // active scene. Optionally set name / parent / position / rotation /
        // priority / follow / look_at. Returns the new GameObject's instance
        // id + path. Reflection-gated: returns cinemachine_3x_required /
        // cinemachine_package_required when the supported shape is absent.
        [BridgeTool("unity_open_mcp_cinemachine_create_camera",
            Title = "Cinemachine: Create Camera",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "cinemachine")]
        [System.ComponentModel.Description(
            "Create a new GameObject carrying a CinemachineCamera (Cinemachine " +
            "3.x) in the active scene. Optionally set name, position, rotation, " +
            "parent_path, priority (integer), and follow/look_at targets " +
            "(instance_id or hierarchy path). Mutating: runs the gate path; " +
            "paths_hint is the active scene path. Requires Cinemachine 3.x " +
            "(`com.unity.cinemachine` ≥ 3.x).")]
        public static string CreateCamera(
            string name = null,
            string parent_path = null,
            string position = null,
            string rotation = null,
            int priority = 0,
            int follow_instance_id = 0,
            string follow_path = null,
            int look_at_instance_id = 0,
            string look_at_path = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return CinemachineJson.Error("paths_hint_required",
                    "cinemachine_create_camera is mutating; pass a non-empty " +
                    "paths_hint scoped to the active scene path.");

            if (!CinemachineVersion.Installed) return CinemachineVersion.PackageMissingError();
            if (!CinemachineVersion.Supported) return CinemachineVersion.VersionTooOldError();

            var go = new GameObject(string.IsNullOrEmpty(name) ? "CM Camera" : name);

            // Resolve optional parent.
            Transform parent = null;
            if (!string.IsNullOrEmpty(parent_path))
            {
                var parentGo = CinemachineTargets.FindByPath(parent_path);
                if (parentGo == null)
                {
                    Object.DestroyImmediate(go);
                    return CinemachineJson.Error("parent_not_found",
                        $"No GameObject at parent_path '{parent_path}'.");
                }
                parent = parentGo.transform;
            }

            Undo.RegisterCreatedObjectUndo(go, "Create CinemachineCamera");
            if (parent != null) go.transform.SetParent(parent, false);

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

            // Add CinemachineCamera via reflection. Undo.AddComponent needs a
            // concrete generic; for a System.Type we use the non-generic
            // Undo.AddComponent(GameObject, Type) overload.
            var camera = Undo.AddComponent(go, CinemachineVersion.CameraType);
            if (camera == null)
            {
                Object.DestroyImmediate(go);
                return CinemachineJson.Error("component_add_failed",
                    "Failed to add CinemachineCamera component.");
            }

            // Priority in 3.x is exposed as `Priority.Value` (a PrioritySettings
            // struct). Set it via reflection.
            SetPriorityValue(camera, priority);

            // Optional Follow / Look At targets.
            var follow = ResolveTarget(follow_instance_id, follow_path);
            if (follow != null) SetObjectFieldOrProperty(camera, "Follow", follow.transform);
            var lookAt = ResolveTarget(look_at_instance_id, look_at_path);
            if (lookAt != null) SetObjectFieldOrProperty(camera, "LookAt", lookAt.transform);

            EditorUtility.SetDirty(go);

            var sb = new StringBuilder(160);
            sb.Append("\"camera\":{");
            sb.Append("\"instanceId\":").Append(go.GetInstanceID()).Append(',');
            sb.Append("\"path\":").Append(CinemachineJson.Esc(CinemachineTargets.BuildPath(go))).Append(',');
            sb.Append("\"priority\":").Append(priority);
            sb.Append('}');
            return CinemachineJson.Ok(sb.ToString());
        }

        // =====================================================================
        // set_targets
        // =====================================================================

        // Set Follow and/or Look At on a CinemachineCamera. Each target resolves
        // by instance_id > path. Omitting a target leaves it unchanged.
        [BridgeTool("unity_open_mcp_cinemachine_set_targets",
            Title = "Cinemachine: Set Targets",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "cinemachine")]
        [System.ComponentModel.Description(
            "Set the Follow and/or Look At targets on a CinemachineCamera. Each " +
            "target is addressed by instance_id or path; omit a target to leave " +
            "it unchanged. Mutating: runs the gate path; paths_hint is the host " +
            "scene path. Requires Cinemachine 3.x.")]
        public static string SetTargets(
            int instance_id = 0,
            string path = null,
            string name = null,
            int follow_instance_id = 0,
            string follow_path = null,
            int look_at_instance_id = 0,
            string look_at_path = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return CinemachineJson.Error("paths_hint_required",
                    "cinemachine_set_targets is mutating; pass a non-empty paths_hint.");

            if (!CinemachineVersion.Installed) return CinemachineVersion.PackageMissingError();
            if (!CinemachineVersion.Supported) return CinemachineVersion.VersionTooOldError();

            var host = CinemachineTargets.Resolve(instance_id, path, name);
            if (host == null) return TargetNotFound();

            var camera = host.GetComponent(CinemachineVersion.CameraType);
            if (camera == null)
                return CinemachineJson.Error("component_not_found",
                    "Target has no CinemachineCamera component. Create one with " +
                    "cinemachine_create_camera first.");

            var sb = new StringBuilder(120);
            sb.Append("\"targets\":{");

            bool any = false;
            if (follow_instance_id != 0 || !string.IsNullOrEmpty(follow_path))
            {
                var follow = ResolveTarget(follow_instance_id, follow_path);
                if (follow == null)
                    return CinemachineJson.Error("target_not_found",
                        "No GameObject resolved for the Follow target.");
                Undo.RecordObject(camera, "Set Cinemachine Follow");
                SetObjectFieldOrProperty(camera, "Follow", follow.transform);
                sb.Append("\"follow\":").Append(CinemachineJson.Esc(CinemachineTargets.BuildPath(follow)));
                any = true;
            }
            if (look_at_instance_id != 0 || !string.IsNullOrEmpty(look_at_path))
            {
                var lookAt = ResolveTarget(look_at_instance_id, look_at_path);
                if (lookAt == null)
                    return CinemachineJson.Error("target_not_found",
                        "No GameObject resolved for the Look At target.");
                Undo.RecordObject(camera, "Set Cinemachine LookAt");
                SetObjectFieldOrProperty(camera, "LookAt", lookAt.transform);
                if (any) sb.Append(',');
                sb.Append("\"lookAt\":").Append(CinemachineJson.Esc(CinemachineTargets.BuildPath(lookAt)));
                any = true;
            }

            if (!any)
                return CinemachineJson.Error("missing_parameter",
                    "Provide at least one of follow_instance_id / follow_path / " +
                    "look_at_instance_id / look_at_path.");

            EditorUtility.SetDirty(camera);
            sb.Append('}');
            return CinemachineJson.Ok(sb.ToString());
        }

        // =====================================================================
        // set_lens
        // =====================================================================

        // Set lens settings on a CinemachineCamera (FieldOfView, NearClip,
        // FarClip, Dutch). Omitted fields are preserved.
        [BridgeTool("unity_open_mcp_cinemachine_set_lens",
            Title = "Cinemachine: Set Lens",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "cinemachine")]
        [System.ComponentModel.Description(
            "Set lens settings on a CinemachineCamera. Fields: field_of_view " +
            "(degrees), near_clip, far_clip, dutch (degrees, lens roll). Omitted " +
            "fields keep the current value. Mutating: runs the gate path; " +
            "paths_hint is the host scene path. Requires Cinemachine 3.x.")]
        public static string SetLens(
            int instance_id = 0,
            string path = null,
            string name = null,
            float field_of_view = -1f,
            float near_clip = -1f,
            float far_clip = -1f,
            float dutch = -1000f,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return CinemachineJson.Error("paths_hint_required",
                    "cinemachine_set_lens is mutating; pass a non-empty paths_hint.");

            if (!CinemachineVersion.Installed) return CinemachineVersion.PackageMissingError();
            if (!CinemachineVersion.Supported) return CinemachineVersion.VersionTooOldError();

            var host = CinemachineTargets.Resolve(instance_id, path, name);
            if (host == null) return TargetNotFound();

            var camera = host.GetComponent(CinemachineVersion.CameraType);
            if (camera == null)
                return CinemachineJson.Error("component_not_found",
                    "Target has no CinemachineCamera component.");

            // The LensSettings live as the `Lens` field on 3.x (a LensSettings
            // struct); Dutch is also a top-level float on the camera.
            Undo.RecordObject(camera, "Set Cinemachine lens");
            var sb = new StringBuilder(160);
            sb.Append("\"lens\":{");

            bool any = false;
            if (field_of_view > 0f)
            {
                SetNestedFloat(camera, "Lens", "FieldOfView", field_of_view);
                sb.Append("\"fieldOfView\":").Append(field_of_view);
                any = true;
            }
            if (near_clip > 0f)
            {
                SetNestedFloat(camera, "Lens", "NearClipPlane", near_clip);
                if (any) sb.Append(',');
                sb.Append("\"nearClip\":").Append(near_clip);
                any = true;
            }
            if (far_clip > 0f)
            {
                SetNestedFloat(camera, "Lens", "FarClipPlane", far_clip);
                if (any) sb.Append(',');
                sb.Append("\"farClip\":").Append(far_clip);
                any = true;
            }
            if (dutch > -1000f)
            {
                SetTopLevelFloat(camera, "Dutch", dutch);
                if (any) sb.Append(',');
                sb.Append("\"dutch\":").Append(dutch);
                any = true;
            }

            if (!any)
                return CinemachineJson.Error("missing_parameter",
                    "Provide at least one of field_of_view / near_clip / far_clip / dutch.");

            EditorUtility.SetDirty(camera);
            sb.Append('}');
            return CinemachineJson.Ok(sb.ToString());
        }

        // =====================================================================
        // set_body (CinemachineFollow etc.)
        // =====================================================================

        // Add or replace the Body component on a CinemachineCamera (the 3.x
        // position-control pipeline). body_name selects the type
        // (CinemachineFollow, CinemachineHardLockToTarget, CinemachineFramingTransposer,
        // …). The current Body component is removed if it exists and is a
        // different type.
        [BridgeTool("unity_open_mcp_cinemachine_set_body",
            Title = "Cinemachine: Set Body",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "cinemachine")]
        [System.ComponentModel.Description(
            "Add or replace the Body component on a CinemachineCamera (the 3.x " +
            "position-control pipeline). body_name selects the component type " +
            "(e.g. CinemachineFollow, CinemachineFramingTransposer, " +
            "CinemachineHardLockToTarget). The current Body component (if any) " +
            "is removed when its type differs. Mutating: runs the gate path; " +
            "paths_hint is the host scene path. Requires Cinemachine 3.x.")]
        public static string SetBody(
            int instance_id = 0,
            string path = null,
            string name = null,
            string body_name = null,
            string[] paths_hint = null)
        {
            return SetPipelineComponent(
                instance_id, path, name, body_name, "Body",
                "cinemachine_set_body", paths_hint);
        }

        // =====================================================================
        // set_noise (CinemachineBasicMultiChannelPerlin)
        // =====================================================================

        // Add or replace the Noise component on a CinemachineCamera.
        // noise_name typically selects CinemachineBasicMultiChannelPerlin (the
        // standard shake component). The current Noise component is removed if
        // it exists and is a different type.
        [BridgeTool("unity_open_mcp_cinemachine_set_noise",
            Title = "Cinemachine: Set Noise",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "cinemachine")]
        [System.ComponentModel.Description(
            "Add or replace the Noise component on a CinemachineCamera. " +
            "noise_name selects the component type (e.g. " +
            "CinemachineBasicMultiChannelPerlin, the standard shake component). " +
            "The current Noise component (if any) is removed when its type " +
            "differs. Mutating: runs the gate path; paths_hint is the host scene " +
            "path. Requires Cinemachine 3.x.")]
        public static string SetNoise(
            int instance_id = 0,
            string path = null,
            string name = null,
            string noise_name = null,
            string[] paths_hint = null)
        {
            return SetPipelineComponent(
                instance_id, path, name, noise_name, "Noise",
                "cinemachine_set_noise", paths_hint);
        }

        // Shared Body/Noise mutator — both follow the same add-or-replace shape.
        private static string SetPipelineComponent(
            int instanceId, string path, string name,
            string componentName, string pipelineSlot,
            string toolName, string[] paths_hint)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return CinemachineJson.Error("paths_hint_required",
                    $"{toolName} is mutating; pass a non-empty paths_hint.");

            if (!CinemachineVersion.Installed) return CinemachineVersion.PackageMissingError();
            if (!CinemachineVersion.Supported) return CinemachineVersion.VersionTooOldError();

            if (string.IsNullOrEmpty(componentName))
                return CinemachineJson.Error("missing_parameter",
                    $"'{pipelineSlot.ToLower()}_name' is required (a Cinemachine " +
                    "Body/Aim/Noise component type name, e.g. " +
                    "'CinemachineFollow' or 'CinemachineBasicMultiChannelPerlin').");

            var host = CinemachineTargets.Resolve(instanceId, path, name);
            if (host == null) return TargetNotFound();

            var camera = host.GetComponent(CinemachineVersion.CameraType);
            if (camera == null)
                return CinemachineJson.Error("component_not_found",
                    "Target has no CinemachineCamera component.");

            var componentType = ResolveCinemachineType(componentName);
            if (componentType == null)
                return CinemachineJson.Error("type_not_found",
                    $"Unknown Cinemachine component type '{componentName}'. " +
                    "Names are unqualified (e.g. 'CinemachineFollow'); the tool " +
                    "resolves them under the Unity.Cinemachine namespace.");

            // Read the current slot (Body / Noise). These are public fields on
            // CinemachineCamera holding a Component reference.
            var current = GetObjectFieldOrProperty(camera, pipelineSlot) as Component;
            if (current != null && current.GetType() != componentType)
            {
                Undo.RecordObject(camera, $"Replace Cinemachine {pipelineSlot}");
                Undo.DestroyObjectImmediate(current);
                current = null;
            }

            if (current == null)
            {
                Undo.RecordObject(camera, $"Add Cinemachine {pipelineSlot}");
                var added = Undo.AddComponent(host, componentType);
                SetObjectFieldOrProperty(camera, pipelineSlot, added);
                current = added;
            }

            EditorUtility.SetDirty(camera);
            var sb = new StringBuilder(140);
            sb.Append($"\"{pipelineSlot.ToLower()}\":{{");
            sb.Append("\"component\":").Append(CinemachineJson.Esc(componentType.Name)).Append(',');
            sb.Append("\"instanceId\":").Append(current.GetInstanceID());
            sb.Append('}');
            return CinemachineJson.Ok(sb.ToString());
        }

        // =====================================================================
        // brain_ensure (optional)
        // =====================================================================

        // Ensure a CinemachineBrain exists on a Camera. If a target Camera
        // (instance_id > path > name) is supplied, ensure on it; otherwise find
        // the main Camera. Adds the Brain via reflection when absent.
        [BridgeTool("unity_open_mcp_cinemachine_brain_ensure",
            Title = "Cinemachine: Ensure Brain",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "cinemachine")]
        [System.ComponentModel.Description(
            "Ensure a CinemachineBrain component exists on a Camera GameObject. " +
            "If instance_id / path / name is supplied, ensure on that Camera; " +
            "otherwise locate the main Camera. Adds the Brain when absent. " +
            "Mutating: runs the gate path; paths_hint is the host scene path. " +
            "Requires Cinemachine 3.x.")]
        public static string BrainEnsure(
            int instance_id = 0,
            string path = null,
            string name = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return CinemachineJson.Error("paths_hint_required",
                    "cinemachine_brain_ensure is mutating; pass a non-empty paths_hint.");

            if (!CinemachineVersion.Installed) return CinemachineVersion.PackageMissingError();
            if (!CinemachineVersion.Supported) return CinemachineVersion.VersionTooOldError();

            GameObject host = null;
            if (instance_id != 0 || !string.IsNullOrEmpty(path) || !string.IsNullOrEmpty(name))
                host = CinemachineTargets.Resolve(instance_id, path, name);
            if (host == null)
            {
                var cam = Camera.main;
                if (cam != null) host = cam.gameObject;
            }
            if (host == null)
                return CinemachineJson.Error("target_not_found",
                    "No Camera GameObject resolved. Pass instance_id / path / name, " +
                    "or ensure a Camera tagged MainCamera exists in the scene.");

            if (host.GetComponent<Camera>() == null)
                return CinemachineJson.Error("component_not_found",
                    "Target has no Camera component. Add one first.");

            var existing = host.GetComponent(CinemachineVersion.BrainType);
            if (existing != null)
            {
                var sbAlready = new StringBuilder(120);
                sbAlready.Append("\"brain\":{");
                sbAlready.Append("\"alreadyPresent\":true,");
                sbAlready.Append("\"instanceId\":").Append(existing.GetInstanceID());
                sbAlready.Append('}');
                return CinemachineJson.Ok(sbAlready.ToString());
            }

            Undo.RecordObject(host, "Add CinemachineBrain");
            var brain = Undo.AddComponent(host, CinemachineVersion.BrainType);
            EditorUtility.SetDirty(host);

            var sb = new StringBuilder(120);
            sb.Append("\"brain\":{");
            sb.Append("\"added\":true,");
            sb.Append("\"instanceId\":").Append(brain.GetInstanceID());
            sb.Append('}');
            return CinemachineJson.Ok(sb.ToString());
        }

        // =====================================================================
        // camera_list (read-only)
        // =====================================================================

        // List every CinemachineCamera in loaded scenes. Read-only, gate-free —
        // agents use it to discover valid camera instance_ids before mutating.
        [BridgeTool("unity_open_mcp_cinemachine_camera_list",
            Title = "Cinemachine: List Cameras",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "cinemachine")]
        [System.ComponentModel.Description(
            "List every CinemachineCamera in loaded scenes. Returns each " +
            "camera's instance id, path, priority, Follow / Look At targets, " +
            "and Body/Aim/Noise component names. Read-only, gate-free. Requires " +
            "Cinemachine 3.x.")]
        public static string CameraList()
        {
            if (!CinemachineVersion.Installed) return CinemachineVersion.PackageMissingError();
            if (!CinemachineVersion.Supported) return CinemachineVersion.VersionTooOldError();

            var type = CinemachineVersion.CameraType;
            var cameras = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include);
            var sb = new StringBuilder(512);
            sb.Append("\"cameras\":[");
            bool first = true;
            foreach (var mb in cameras)
            {
                if (mb == null) continue;
                if (mb.GetType() != type) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                sb.Append("\"instanceId\":").Append(mb.GetInstanceID()).Append(',');
                sb.Append("\"path\":").Append(CinemachineJson.Esc(CinemachineTargets.BuildPath(mb.gameObject))).Append(',');
                sb.Append("\"priority\":").Append(GetPriorityValue(mb)).Append(',');
                sb.Append("\"follow\":").Append(CinemachineJson.Esc(GetTransformPath(GetObjectFieldOrProperty(mb, "Follow") as Transform))).Append(',');
                sb.Append("\"lookAt\":").Append(CinemachineJson.Esc(GetTransformPath(GetObjectFieldOrProperty(mb, "LookAt") as Transform))).Append(',');
                sb.Append("\"body\":").Append(CinemachineJson.Esc(GetPipelineName(mb, "Body"))).Append(',');
                sb.Append("\"aim\":").Append(CinemachineJson.Esc(GetPipelineName(mb, "Aim"))).Append(',');
                sb.Append("\"noise\":").Append(CinemachineJson.Esc(GetPipelineName(mb, "Noise")));
                sb.Append('}');
            }
            sb.Append(']');
            return CinemachineJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Helpers — reflection over Cinemachine 3.x fields
        // =====================================================================

        private static GameObject ResolveTarget(int instanceId, string path)
        {
            if (instanceId != 0)
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj is GameObject go) return go;
            }
            if (!string.IsNullOrEmpty(path)) return CinemachineTargets.FindByPath(path);
            return null;
        }

        // Set the priority value on a CinemachineCamera. 3.x exposes Priority as
        // a PrioritySettings struct with an int `Value` field.
        private static void SetPriorityValue(Component camera, int priority)
        {
            var priorityProp = camera.GetType().GetField("Priority");
            if (priorityProp == null) return;
            var settings = priorityProp.GetValue(camera);
            if (settings == null)
            {
                var settingsType = priorityProp.FieldType;
                settings = System.Activator.CreateInstance(settingsType);
                priorityProp.SetValue(camera, settings);
            }
            var valueField = settings.GetType().GetField("Value");
            valueField?.SetValue(settings, priority);
        }

        private static int GetPriorityValue(Component camera)
        {
            var priorityProp = camera.GetType().GetField("Priority");
            if (priorityProp == null) return 0;
            var settings = priorityProp.GetValue(camera);
            if (settings == null) return 0;
            var valueField = settings.GetType().GetField("Value");
            return valueField != null ? (int)valueField.GetValue(settings) : 0;
        }

        private static void SetObjectFieldOrProperty(object obj, string name, object value)
        {
            var t = obj.GetType();
            var f = t.GetField(name);
            if (f != null) { f.SetValue(obj, value); return; }
            var p = t.GetProperty(name);
            p?.SetValue(obj, value);
        }

        private static object GetObjectFieldOrProperty(object obj, string name)
        {
            var t = obj.GetType();
            var f = t.GetField(name);
            if (f != null) return f.GetValue(obj);
            var p = t.GetProperty(name);
            return p?.GetValue(obj);
        }

        private static void SetTopLevelFloat(Component camera, string fieldName, float value)
        {
            var f = camera.GetType().GetField(fieldName);
            if (f != null && f.FieldType == typeof(float)) f.SetValue(camera, value);
        }

        // Set a float nested inside a struct field on the component (Lens.FieldOfView).
        private static void SetNestedFloat(Component camera, string structField, string nestedField, float value)
        {
            var sf = camera.GetType().GetField(structField);
            if (sf == null) return;
            var settings = sf.GetValue(camera);
            if (settings == null)
            {
                settings = System.Activator.CreateInstance(sf.FieldType);
                sf.SetValue(camera, settings);
            }
            var nf = settings.GetType().GetField(nestedField);
            if (nf != null && nf.FieldType == typeof(float)) nf.SetValue(settings, value);
        }

        // Resolve a Cinemachine component type by its unqualified name. Scans
        // the Unity.Cinemachine assembly (3.x). GetTypes can throw
        // ReflectionTypeLoadException when an assembly has missing dependencies;
        // we catch and fall back to the loader-returned types so a single broken
        // type does not abort the lookup.
        private static System.Type ResolveCinemachineType(string unqualifiedName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Unity.Cinemachine") continue;
                System.Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException e)
                {
                    types = e.Types ?? System.Array.Empty<System.Type>();
                }
                foreach (var t in types)
                {
                    if (t != null && t.Name == unqualifiedName) return t;
                }
            }
            return null;
        }

        private static string GetPipelineName(Component camera, string slot)
        {
            var comp = GetObjectFieldOrProperty(camera, slot) as Component;
            return comp != null ? comp.GetType().Name : "";
        }

        private static string GetTransformPath(Transform t)
            => t == null ? "" : CinemachineTargets.BuildPath(t.gameObject);

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

        private static string TargetNotFound()
            => CinemachineJson.Error("target_not_found",
                "No GameObject resolved. Address by instance_id > path > name.");
    }
}
