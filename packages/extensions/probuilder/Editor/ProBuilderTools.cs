using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpExtensions.ProBuilder
{
    // M16 Plan 10 / T6.6.5 — ProBuilder extension pack.
    //
    // Five typed tools for in-editor mesh editing:
    //   - create_shape: ShapeGenerator.CreateShape primitive
    //   - get_mesh_info: read face / vertex / edge counts + direction summary
    //   - extrude: extrude faces by index or by direction (Up/Down/...)
    //   - delete_faces: destructive — delete faces by index or direction
    //   - set_face_material: assign a Material to faces by index or direction
    //
    // Tools that mutate a scene GameObject run the gate path with paths_hint
    // scoped to the host's scene path. create_shape adds a new GameObject to
    // the scene — its paths_hint is the active scene path. Face selection is
    // index-based (NOT SceneView mouse picking) so the tools work headlessly;
    // a semantic direction (Up / Down / Left / Right / Forward / Back) is
    // accepted as a friendlier alternative to indices.
    //
    // Naming: `unity_open_mcp_probuilder_<action>` (snake_case domain prefix —
    // mirrors the kebab `probuilder-*` ids in the upstream Unity-MCP reference
    // pack). Reference: IvanMurzak/Unity-AI-ProBuilder (Apache-2.0).
    [BridgeToolType]
    public static class ProBuilderTools
    {
        // =====================================================================
        // Create shape
        // =====================================================================

        // Create a new ProBuilderMesh primitive in the active scene. The shape
        // is added at the scene root (or under an optional parent_path). Returns
        // the new GameObject's instance id so the next call can address it.
        [BridgeTool("unity_open_mcp_probuilder_create_shape",
            Title = "ProBuilder: Create Shape",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle)]
        [System.ComponentModel.Description(
            "Create a new editable ProBuilderMesh GameObject from a ShapeType " +
            "primitive (Cube / Cylinder / Sphere / Plane / Prism / Cone / Stair / " +
            "Door / Pipe / Arch / Sprite / Torus). Optionally set name, position, " +
            "rotation, scale, and parent. Mutating: runs the gate path; paths_hint " +
            "is the active scene path (the new GameObject lives there).")]
        public static string CreateShape(
            string shape_type = "Cube",
            string name = null,
            string parent_path = null,
            string position = null,
            string rotation = null,
            string scale = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return ProBuilderJson.Error("paths_hint_required",
                    "probuilder_create_shape is mutating; pass a non-empty paths_hint " +
                    "scoped to the active scene path.");

            if (!TryParseShapeType(shape_type, out var shape, out var shapeError))
                return ProBuilderJson.Error("invalid_shape_type", shapeError);

            var mesh = ShapeGenerator.CreateShape(shape, PivotLocation.Center);
            if (mesh == null)
                return ProBuilderJson.Error("creation_failed",
                    $"ShapeGenerator.CreateShape returned null for '{shape}'.");

            var go = mesh.gameObject;
            go.name = string.IsNullOrEmpty(name) ? $"ProBuilder {shape}" : name;

            // Resolve optional parent. parent_path is a slash-separated hierarchy.
            Transform parent = null;
            if (!string.IsNullOrEmpty(parent_path))
            {
                var parentGo = ProBuilderTargets.FindByPath(parent_path);
                if (parentGo == null)
                    return ProBuilderJson.Error("parent_not_found",
                        $"No GameObject at parent_path '{parent_path}'.");
                parent = parentGo.transform;
                go.transform.SetParent(parent, false);
            }

            // Apply transform. position/rotation are world space unless a parent
            // exists (then they're local — matches the reference pack's
            // isLocalSpace:false-with-parent behavior).
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
            if (!string.IsNullOrEmpty(scale))
                go.transform.localScale = ParseVector3(scale, Vector3.one);

            mesh.ToMesh();
            mesh.Refresh();
            EditorUtility.SetDirty(go);

            var sb = new StringBuilder(220);
            sb.Append("\"shape\":{");
            sb.Append("\"name\":").Append(ProBuilderJson.Esc(go.name)).Append(',');
            sb.Append("\"instanceId\":").Append(go.GetInstanceID()).Append(',');
            sb.Append("\"shapeType\":").Append(ProBuilderJson.Esc(shape.ToString())).Append(',');
            sb.Append("\"path\":").Append(ProBuilderJson.Esc(ProBuilderTargets.BuildPath(go))).Append(',');
            sb.Append("\"faceCount\":").Append(mesh.faceCount).Append(',');
            sb.Append("\"vertexCount\":").Append(mesh.vertexCount).Append(',');
            sb.Append("\"edgeCount\":").Append(mesh.edgeCount);
            sb.Append('}');
            return ProBuilderJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Get mesh info (read-only)
        // =====================================================================

        // Read face / vertex / edge counts plus a face-direction summary. Read-
        // only, gate-free — agents use this to discover valid face indices
        // before mutating.
        [BridgeTool("unity_open_mcp_probuilder_get_mesh_info",
            Title = "ProBuilder: Get Mesh Info",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None)]
        [System.ComponentModel.Description(
            "Inspect a ProBuilderMesh — face / vertex / edge counts, bounds, and " +
            "a face-direction summary (which face indices face Up / Down / Left / " +
            "Right / Forward / Back). Read-only, gate-free. Use this to discover " +
            "valid face indices or to pick a semantic direction for extrude / " +
            "delete_faces / set_face_material.")]
        public static string GetMeshInfo(
            int instance_id = 0,
            string path = null,
            string name = null)
        {
            var host = ProBuilderTargets.Resolve(instance_id, path, name);
            if (host == null)
                return ProBuilderJson.Error("target_not_found",
                    "No GameObject resolved. Address by instance_id > path > name.");

            var mesh = host.GetComponent<ProBuilderMesh>();
            if (mesh == null)
                return ProBuilderJson.Error("component_not_found",
                    "Target has no ProBuilderMesh. Create one with probuilder_create_shape first.");

            var sb = new StringBuilder(512);
            sb.Append("{\"status\":\"ok\",\"mesh\":{");
            sb.Append("\"name\":").Append(ProBuilderJson.Esc(host.name)).Append(',');
            sb.Append("\"instanceId\":").Append(host.GetInstanceID()).Append(',');
            sb.Append("\"path\":").Append(ProBuilderJson.Esc(ProBuilderTargets.BuildPath(host))).Append(',');
            sb.Append("\"faceCount\":").Append(mesh.faceCount).Append(',');
            sb.Append("\"vertexCount\":").Append(mesh.vertexCount).Append(',');
            sb.Append("\"edgeCount\":").Append(mesh.edgeCount).Append(',');
            sb.Append("\"triangleCount\":").Append(mesh.triangleCount);

            var mf = host.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                var b = mf.sharedMesh.bounds;
                sb.Append(",\"bounds\":{");
                sb.Append("\"center\":").Append(ProBuilderJson.Vec3(b.center)).Append(',');
                sb.Append("\"size\":").Append(ProBuilderJson.Vec3(b.size)).Append(',');
                sb.Append("\"min\":").Append(ProBuilderJson.Vec3(b.min)).Append(',');
                sb.Append("\"max\":").Append(ProBuilderJson.Vec3(b.max));
                sb.Append('}');
            }

            // Face-direction summary — which indices face each axis.
            sb.Append(",\"faceDirections\":{");
            AppendFaceDirectionSummary(sb, mesh);
            sb.Append("}}");
            return sb.ToString();
        }

        // =====================================================================
        // Extrude
        // =====================================================================

        // Extrude faces along their normals. Faces can be selected by index
        // (face_indices) OR by direction (face_direction). Positive distance
        // extrudes outward; negative inward.
        [BridgeTool("unity_open_mcp_probuilder_extrude",
            Title = "ProBuilder: Extrude Faces",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle)]
        [System.ComponentModel.Description(
            "Extrude faces of a ProBuilderMesh along their normals, creating new " +
            "geometry. Supply either face_indices (explicit) or face_direction " +
            "('Up' / 'Down' / 'Left' / 'Right' / 'Forward' / 'Back'); exactly one " +
            "is required. Positive distance extrudes outward, negative inward. " +
            "extrude_method is 'IndividualFaces' / 'FaceNormal' (default) / " +
            "'VertexNormal'. Mutating: runs the gate path; paths_hint is the host " +
            "scene path.")]
        public static string Extrude(
            int instance_id = 0,
            string path = null,
            string name = null,
            int[] face_indices = null,
            string face_direction = null,
            float distance = 0.5f,
            string extrude_method = "FaceNormal",
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return PathRequired("probuilder_extrude");

            if (!TryParseExtrudeMethod(extrude_method, out var method, out var methodError))
                return ProBuilderJson.Error("invalid_extrude_method", methodError);

            var host = ProBuilderTargets.Resolve(instance_id, path, name);
            if (host == null) return TargetNotFound();

            var mesh = host.GetComponent<ProBuilderMesh>();
            if (mesh == null)
                return ProBuilderJson.Error("component_not_found",
                    "Target has no ProBuilderMesh. Create one with probuilder_create_shape first.");

            if (!ResolveFaces(mesh, face_indices, face_direction, out var resolved, out var selectionMethod, out var errorEnvelope))
                return errorEnvelope;

            var facesToExtrude = resolved.Select(i => mesh.faces[i]).ToArray();
            Face[] newFaces;
            try
            {
                newFaces = mesh.Extrude(facesToExtrude, method, distance);
            }
            catch (System.Exception e)
            {
                return ProBuilderJson.Error("extrude_failed", e.Message);
            }

            if (newFaces == null || newFaces.Length == 0)
                return ProBuilderJson.Error("extrude_failed",
                    "Extrusion created no new faces. The operation may not be valid for this mesh.");

            mesh.ToMesh();
            mesh.Refresh();
            EditorUtility.SetDirty(mesh);
            EditorUtility.SetDirty(host);

            var sb = new StringBuilder(220);
            sb.Append("\"extrude\":{");
            sb.Append("\"extrudedFaceCount\":").Append(facesToExtrude.Length).Append(',');
            sb.Append("\"selectionMethod\":").Append(ProBuilderJson.Esc(selectionMethod)).Append(',');
            sb.Append("\"extrudeMethod\":").Append(ProBuilderJson.Esc(method.ToString())).Append(',');
            sb.Append("\"distance\":").Append(distance).Append(',');
            sb.Append("\"newFacesCreated\":").Append(newFaces.Length).Append(',');
            sb.Append("\"totalFaceCount\":").Append(mesh.faceCount).Append(',');
            sb.Append("\"totalVertexCount\":").Append(mesh.vertexCount);
            sb.Append('}');
            return ProBuilderJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Delete faces (destructive)
        // =====================================================================

        // Delete faces from a ProBuilderMesh, creating holes or removing
        // geometry. Destructive — the DestructiveHint flag is set so MCP
        // clients can prompt the user for confirmation. Refuses to delete
        // every face (at least one must remain).
        [BridgeTool("unity_open_mcp_probuilder_delete_faces",
            Title = "ProBuilder: Delete Faces",
            IsMutating = true,
            DestructiveHint = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle)]
        [System.ComponentModel.Description(
            "Delete faces from a ProBuilderMesh, creating holes or removing geometry. " +
            "DESTRUCTIVE — irreversible without undo. Supply either face_indices " +
            "(explicit) or face_direction ('Up' / 'Down' / 'Left' / 'Right' / " +
            "'Forward' / 'Back'); exactly one is required. Refuses to delete every " +
            "face. Mutating: runs the gate path; paths_hint is the host scene path.")]
        public static string DeleteFaces(
            int instance_id = 0,
            string path = null,
            string name = null,
            int[] face_indices = null,
            string face_direction = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return PathRequired("probuilder_delete_faces");

            var host = ProBuilderTargets.Resolve(instance_id, path, name);
            if (host == null) return TargetNotFound();

            var mesh = host.GetComponent<ProBuilderMesh>();
            if (mesh == null)
                return ProBuilderJson.Error("component_not_found",
                    "Target has no ProBuilderMesh. Create one with probuilder_create_shape first.");

            if (!ResolveFaces(mesh, face_indices, face_direction, out var resolved, out var selectionMethod, out var errorEnvelope))
                return errorEnvelope;

            // De-duplicate so the count check matches mesh.faces.Count semantics.
            var unique = resolved.Distinct().ToArray();
            if (unique.Length >= mesh.faces.Count)
                return ProBuilderJson.Error("cannot_delete_all_faces",
                    "Cannot delete every face from a mesh — at least one must remain.");

            var originalFaceCount = mesh.faceCount;
            var originalVertexCount = mesh.vertexCount;
            var facesToDelete = unique.Select(i => mesh.faces[i]).ToArray();

            try
            {
                mesh.DeleteFaces(facesToDelete);
            }
            catch (System.Exception e)
            {
                return ProBuilderJson.Error("delete_failed", e.Message);
            }

            mesh.ToMesh();
            mesh.Refresh();
            EditorUtility.SetDirty(mesh);
            EditorUtility.SetDirty(host);

            var sb = new StringBuilder(220);
            sb.Append("\"delete\":{");
            sb.Append("\"deletedFaceCount\":").Append(unique.Length).Append(',');
            sb.Append("\"selectionMethod\":").Append(ProBuilderJson.Esc(selectionMethod)).Append(',');
            sb.Append("\"facesRemoved\":").Append(originalFaceCount - mesh.faceCount).Append(',');
            sb.Append("\"verticesRemoved\":").Append(originalVertexCount - mesh.vertexCount).Append(',');
            sb.Append("\"totalFaceCount\":").Append(mesh.faceCount).Append(',');
            sb.Append("\"totalVertexCount\":").Append(mesh.vertexCount);
            sb.Append('}');
            return ProBuilderJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Set face material
        // =====================================================================

        // Assign a Material to faces of a ProBuilderMesh, enabling multi-
        // material meshes (e.g. grass on top, dirt on sides). The Material is
        // added to the MeshRenderer.sharedMaterials array if it is not already
        // present, and each selected face's submeshIndex is set to it.
        [BridgeTool("unity_open_mcp_probuilder_set_face_material",
            Title = "ProBuilder: Set Face Material",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle)]
        [System.ComponentModel.Description(
            "Assign a Material to faces of a ProBuilderMesh, enabling multi-material " +
            "meshes (e.g. grass on top, dirt on sides). material_path is an " +
            "'Assets/'-rooted path to a .mat asset (or a bare name — searched via " +
            "AssetDatabase.FindAssets). Supply either face_indices (explicit) or " +
            "face_direction ('Up' / 'Down' / 'Left' / 'Right' / 'Forward' / 'Back'); " +
            "exactly one is required. Mutating: runs the gate path; paths_hint is " +
            "the host scene path.")]
        public static string SetFaceMaterial(
            int instance_id = 0,
            string path = null,
            string name = null,
            string material_path = null,
            int[] face_indices = null,
            string face_direction = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return PathRequired("probuilder_set_face_material");

            if (string.IsNullOrWhiteSpace(material_path))
                return ProBuilderJson.Error("missing_parameter",
                    "'material_path' is required (an 'Assets/'-rooted .mat path or a bare material name).");

            var host = ProBuilderTargets.Resolve(instance_id, path, name);
            if (host == null) return TargetNotFound();

            var mesh = host.GetComponent<ProBuilderMesh>();
            if (mesh == null)
                return ProBuilderJson.Error("component_not_found",
                    "Target has no ProBuilderMesh. Create one with probuilder_create_shape first.");

            var material = LoadMaterial(material_path);
            if (material == null)
                return ProBuilderJson.Error("material_not_found",
                    $"No Material found at '{material_path}'. Pass an 'Assets/'-rooted .mat path or a bare material name.");

            if (!ResolveFaces(mesh, face_indices, face_direction, out var resolved, out var selectionMethod, out var errorEnvelope))
                return errorEnvelope;

            var renderer = host.GetComponent<MeshRenderer>();
            if (renderer == null)
                return ProBuilderJson.Error("component_not_found",
                    "Target has no MeshRenderer (ProBuilder shapes should have one).");

            // Append the material to sharedMaterials if not already present.
            var materials = renderer.sharedMaterials.ToList();
            int materialIndex = materials.IndexOf(material);
            if (materialIndex < 0)
            {
                materialIndex = materials.Count;
                materials.Add(material);
                renderer.sharedMaterials = materials.ToArray();
            }

            // Assign submeshIndex on each selected face.
            foreach (var i in resolved)
                mesh.faces[i].submeshIndex = materialIndex;

            mesh.ToMesh();
            mesh.Refresh();
            EditorUtility.SetDirty(mesh);
            EditorUtility.SetDirty(renderer);
            EditorUtility.SetDirty(host);

            var sb = new StringBuilder(220);
            sb.Append("\"setFaceMaterial\":{");
            sb.Append("\"materialName\":").Append(ProBuilderJson.Esc(material.name)).Append(',');
            sb.Append("\"materialIndex\":").Append(materialIndex).Append(',');
            sb.Append("\"selectionMethod\":").Append(ProBuilderJson.Esc(selectionMethod)).Append(',');
            sb.Append("\"facesUpdatedCount\":").Append(resolved.Length).Append(',');
            sb.Append("\"materialCount\":").Append(renderer.sharedMaterials.Length);
            sb.Append('}');
            return ProBuilderJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        static string PathRequired(string tool)
            => ProBuilderJson.Error("paths_hint_required",
                $"{tool} is mutating; pass a non-empty paths_hint scoped to the host scene path.");

        static string TargetNotFound()
            => ProBuilderJson.Error("target_not_found",
                "No GameObject resolved. Address by instance_id > path > name.");

        static bool TryParseShapeType(string s, out ShapeType shape, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(s))
            {
                shape = ShapeType.Cube;
                return true;
            }
            if (System.Enum.TryParse<ShapeType>(s, true, out shape))
                return true;
            error = $"Unknown shape_type '{s}'. Valid: {string.Join(", ", System.Enum.GetNames(typeof(ShapeType)))}.";
            return false;
        }

        static bool TryParseExtrudeMethod(string s, out ExtrudeMethod method, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(s))
            {
                method = ExtrudeMethod.FaceNormal;
                return true;
            }
            if (System.Enum.TryParse<ExtrudeMethod>(s, true, out method))
                return true;
            error = $"Unknown extrude_method '{s}'. Valid: IndividualFaces, FaceNormal, VertexNormal.";
            return false;
        }

        // Resolve faces by index OR direction. Exactly one of face_indices /
        // face_direction is required. Returns false + sets outErrorEnvelope
        // (a structured JSON error) on any failure.
        static bool ResolveFaces(ProBuilderMesh mesh,
            int[] faceIndices, string faceDirection,
            out int[] resolved, out string selectionMethod, out string errorEnvelope)
        {
            resolved = null;
            selectionMethod = null;
            errorEnvelope = null;

            bool hasIndices = faceIndices != null && faceIndices.Length > 0;
            bool hasDirection = !string.IsNullOrWhiteSpace(faceDirection);

            if (hasIndices && hasDirection)
            {
                errorEnvelope = ProBuilderJson.Error("conflicting_selection",
                    "Pass either face_indices OR face_direction, not both.");
                return false;
            }
            if (!hasIndices && !hasDirection)
            {
                errorEnvelope = ProBuilderJson.Error("missing_parameter",
                    "Either face_indices or face_direction is required.");
                return false;
            }

            var faces = mesh.faces;
            var faceCount = faces.Count;
            if (faceCount == 0)
            {
                errorEnvelope = ProBuilderJson.Error("mesh_has_no_faces",
                    "Mesh has no faces to operate on.");
                return false;
            }

            if (hasIndices)
            {
                // Validate index range.
                var invalid = faceIndices.Where(i => i < 0 || i >= faceCount).ToArray();
                if (invalid.Length > 0)
                {
                    errorEnvelope = ProBuilderJson.Error("invalid_face_indices",
                        $"Invalid face indices: {string.Join(", ", invalid)}. " +
                        $"Valid range: 0 to {faceCount - 1}.");
                    return false;
                }
                resolved = faceIndices;
                selectionMethod = "by index";
                return true;
            }

            // Semantic direction.
            if (!FaceSelection.IsKnownDirection(faceDirection))
            {
                errorEnvelope = ProBuilderJson.Error("invalid_face_direction",
                    $"Unknown face_direction '{faceDirection}'. Valid: Up, Down, Left, Right, Forward, Back.");
                return false;
            }
            resolved = FaceSelection.SelectByDirection(mesh, faceDirection, out var dirError);
            if (resolved == null)
            {
                errorEnvelope = ProBuilderJson.Error("no_faces_in_direction", dirError);
                return false;
            }
            selectionMethod = $"by direction '{faceDirection}'";
            return true;
        }

        static UnityEngine.Material LoadMaterial(string materialPath)
        {
            // Try as asset path first.
            if (materialPath.StartsWith("Assets/"))
            {
                var mat = AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(materialPath);
                if (mat != null) return mat;
            }
            // Fall back to a name search.
            var guids = AssetDatabase.FindAssets($"t:Material {materialPath}");
            if (guids.Length > 0)
            {
                var resolved = AssetDatabase.GUIDToAssetPath(guids[0]);
                return AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(resolved);
            }
            return null;
        }

        static Vector3 ParseVector3(string s, Vector3 fallback)
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

        // Append the face-direction summary to a StringBuilder. Maps each axis
        // to the indices facing it (dot >= DirectionThreshold). Faces that do
        // not match any axis land in "other". A face can only be claimed by
        // one direction (first match wins) so the buckets are disjoint.
        static void AppendFaceDirectionSummary(StringBuilder sb, ProBuilderMesh mesh)
        {
            var faces = mesh.faces;
            var positions = mesh.positions;
            var dirs = new[] {
                ("up", Vector3.up), ("down", Vector3.down),
                ("left", Vector3.left), ("right", Vector3.right),
                ("forward", Vector3.forward), ("back", Vector3.back),
            };

            // Pre-compute the direction label per face (or null for "other").
            var labels = new string[faces.Count];
            for (int i = 0; i < faces.Count; i++)
            {
                var normal = FaceSelection.CalculateFaceNormal(faces[i], positions);
                foreach (var (label, vec) in dirs)
                {
                    if (Vector3.Dot(normal.normalized, vec) >= FaceSelection.DirectionThreshold)
                    {
                        labels[i] = label;
                        break;
                    }
                }
            }

            bool firstDir = true;
            foreach (var (label, _) in dirs)
            {
                var indices = new List<int>();
                for (int i = 0; i < faces.Count; i++)
                    if (labels[i] == label) indices.Add(i);
                if (indices.Count == 0) continue;
                if (!firstDir) sb.Append(',');
                firstDir = false;
                sb.Append(ProBuilderJson.Esc(label)).Append(":[").Append(string.Join(",", indices)).Append(']');
            }

            // "other": faces that did not match any direction.
            var other = new List<int>();
            for (int i = 0; i < faces.Count; i++)
                if (labels[i] == null) other.Add(i);
            if (other.Count > 0)
            {
                if (!firstDir) sb.Append(',');
                sb.Append(ProBuilderJson.Esc("other")).Append(":[").Append(string.Join(",", other)).Append(']');
            }
        }
    }
}
