// GetInstanceID() is deprecated in Unity 6000.4+ in favour of GetEntityId(),
// but GetEntityId() returns different values and does not exist in 2022.3 (this
// package's declared minimum). Our JSON handle contract is built on the stable
// int instance ID, so the deprecated int API is used deliberately here. See
// ObjectRefs/ObjectHandle.cs for the canonical rationale.
#pragma warning disable CS0618
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityOpenMcpBridge.TypedTools
{
    // M16 Plan 2 — typed GameObject lifecycle tools. Covers create / destroy /
    // duplicate / find / modify / set_parent. Mutation tools run through the
    // gate envelope with paths_hint. `find` is gate-free.
    //
    // Resolve target scene instances by instance_id (canonical) > path > name
    // via TypedTargets — same addressing model as spatial-query.ts so agents
    // can pass the same handle across typed tools. Mutations register Undo and
    // mark the active scene dirty so Unity knows to save.
    //
    // paths_hint is the active scene path (the GameObject is a scene side-effect)
    // for every mutating tool here — GameObjects are not asset-backed, so there
    // is no .prefab/.mat path to scope. Pass the scene that contains (or will
    // contain) the GameObject.
    public static class GameObjectsTools
    {
        public static ToolDispatchResult Create(string body)
        {
            var name = JsonBody.GetString(body, "name");
            if (string.IsNullOrWhiteSpace(name))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'name' is required and must be a non-empty string.");

            var primitiveStr = JsonBody.GetString(body, "primitive_type");
            var parentPath = JsonBody.GetString(body, "parent_path");
            var position = PrefabTools.ParseVector(JsonBody.GetString(body, "position"), Vector3.zero);
            var rotation = PrefabTools.ParseVector(JsonBody.GetString(body, "rotation"), Vector3.zero);
            var scale = PrefabTools.ParseVector(JsonBody.GetString(body, "scale"), Vector3.one);
            var localSpace = JsonBody.GetBool(body, "local_space", false);

            // Validate primitive_type up-front so a typo fails fast instead of
            // silently creating an empty GameObject.
            var primitive = ParsePrimitive(primitiveStr);
            if (primitiveStr != null && primitive == null)
                return ToolDispatchResult.Fail("invalid_parameter",
                    $"Unknown primitive_type '{primitiveStr}'. Expected one of: " +
                    "Cube, Sphere, Capsule, Cylinder, Plane, Quad.");

            GameObject parentGo = null;
            if (!string.IsNullOrEmpty(parentPath))
            {
                parentGo = TypedTargets.FindByPath(parentPath);
                if (parentGo == null)
                    return ToolDispatchResult.Fail("parent_not_found",
                        $"Parent GameObject not found at path '{parentPath}'.");
            }

            GameObject go;
            try
            {
                go = primitive.HasValue
                    ? GameObject.CreatePrimitive(primitive.Value)
                    : new GameObject(name);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed", e.Message);
            }

            go.name = name;

            if (parentGo != null)
                go.transform.SetParent(parentGo.transform, false);

            ApplyTransform(go.transform, position, rotation, scale, localSpace);

            Undo.RegisterCreatedObjectUndo(go, "MCP Create GameObject");
            EditorUtility.SetDirty(go);
            MarkActiveSceneDirty();

            return ToolDispatchResult.Ok(BuildGameObjectResult(go, "created"));
        }

        public static ToolDispatchResult Destroy(string body)
        {
            var resolved = ResolveInstance(body);
            if (!resolved.Ok) return resolved.Result;
            var go = resolved.GameObject;

            try
            {
                Undo.DestroyObjectImmediate(go);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("destroy_failed", e.Message);
            }

            MarkActiveSceneDirty();

            var sb = new StringBuilder(64);
            sb.Append("{\"status\":\"ok\",\"destroyed\":true,\"name\":\"")
              .Append(TypedTargets.Esc(resolved.Name)).Append("\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult Duplicate(string body)
        {
            var resolved = ResolveInstance(body);
            if (!resolved.Ok) return resolved.Result;
            var go = resolved.GameObject;

            GameObject clone;
            try
            {
                clone = Object.Instantiate(go, go.transform.parent, false);
                // Name after Instantiate appends "(Clone)" — match editor
                // duplicate semantics so the result name is stable.
                clone.name = go.name;
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("duplicate_failed", e.Message);
            }

            Undo.RegisterCreatedObjectUndo(clone, "MCP Duplicate GameObject");
            EditorUtility.SetDirty(clone);
            MarkActiveSceneDirty();

            return ToolDispatchResult.Ok(BuildGameObjectResult(clone, "duplicated"));
        }

        // Find returns a token-bounded list of GameObjects that match the
        // provided filters. Read-only, gate-free. Mirrors the addressing
        // vocabulary of spatial-query.ts (instance_id / path / name) but adds
        // tag / component / active filters and an explicit list mode (no
        // target address → enumerate matching roots and children).
        public static ToolDispatchResult Find(string body)
        {
            int maxResults = JsonBody.GetInt(body, "max_results", 50);
            if (maxResults < 1) maxResults = 1;

            // Targeted lookup (instance_id / path / name) short-circuits to a
            // single-object result when it resolves, so an agent can use Find
            // as the canonical "acquire a handle" tool.
            var instanceId = JsonBody.GetInt(body, "instance_id", 0);
            var path = JsonBody.GetString(body, "path");
            var name = JsonBody.GetString(body, "name");
            if (instanceId != 0 || !string.IsNullOrEmpty(path) || !string.IsNullOrEmpty(name))
            {
                var go = TypedTargets.ResolveGameObject(instanceId, path, name);
                if (go == null)
                {
                    // NOTE: named sbNotFound (not sb) — a `var sb` here would
                    // collide with the list-mode sb declared below in this
                    // method (CS0136: nested local cannot reuse an enclosing
                    // scope name).
                    var sbNotFound = new StringBuilder(64);
                    sbNotFound.Append("{\"objects\":[],\"count\":0,\"truncated\":0,\"notFound\":true}");
                    return ToolDispatchResult.Ok(sbNotFound.ToString());
                }
                var one = BuildGameObjectSummary(go);
                var sbOne = new StringBuilder(64 + one.Length);
                sbOne.Append("{\"objects\":[").Append(one).Append("]");
                sbOne.Append(",\"count\":1,\"truncated\":0}");
                return ToolDispatchResult.Ok(sbOne.ToString());
            }

            // List mode — enumerate candidates from loaded scenes and apply
            // filters. Use a query-driven walk so a project with many roots
            // stays bounded by max_results.
            var tagFilter = JsonBody.GetString(body, "tag");
            var componentFilter = JsonBody.GetString(body, "component");
            var nameContains = JsonBody.GetString(body, "name_contains");
            var rootOnly = JsonBody.GetBool(body, "root_only", false);

            var matches = new List<GameObject>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (rootOnly)
                    {
                        if (Matches(root, nameContains, tagFilter, componentFilter))
                            matches.Add(root);
                    }
                    else
                    {
                        CollectMatches(root, matches, nameContains, tagFilter, componentFilter);
                    }
                    if (matches.Count >= maxResults) break;
                }
                if (matches.Count >= maxResults) break;
            }

            int truncated = 0;
            var emitted = matches;
            if (matches.Count > maxResults)
            {
                emitted = matches.GetRange(0, maxResults);
                truncated = matches.Count - maxResults;
            }

            var sb = new StringBuilder(256 + emitted.Count * 96);
            sb.Append("{\"objects\":[");
            for (int i = 0; i < emitted.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BuildGameObjectSummary(emitted[i]));
            }
            sb.Append("],\"count\":").Append(emitted.Count);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // Modify covers name / tag / layer / active AND transform (pos / rot /
        // scale). Each field is optional; only provided fields are touched.
        //
        // The target is resolved via instance_id > path > name_target (note:
        // `name_target`, not `name`, because `name` is the new value here).
        public static ToolDispatchResult Modify(string body)
        {
            // Resolve the target with name_target so `name` stays free for
            // the new value. Falls back to `name` when name_target is unset
            // so callers without a rename still work via the standard key.
            var instanceId = JsonBody.GetInt(body, "instance_id", 0);
            var path = JsonBody.GetString(body, "path");
            var nameTarget = JsonBody.GetString(body, "name_target");
            var resolveName = string.IsNullOrEmpty(nameTarget)
                ? JsonBody.GetString(body, "name")
                : nameTarget;
            var go = TypedTargets.ResolveGameObject(instanceId, path, resolveName);
            if (go == null)
                return ToolDispatchResult.Fail("gameobject_not_found",
                    $"GameObject not found (instance_id={instanceId}, path='{path}', name_target='{nameTarget}').");

            var name = JsonBody.GetString(body, "name");
            var tag = JsonBody.GetString(body, "tag");
            var layerStr = JsonBody.GetRawValue(body, "layer");
            var activeStr = JsonBody.GetRawValue(body, "active");
            var positionStr = JsonBody.GetString(body, "position");
            var rotationStr = JsonBody.GetString(body, "rotation");
            var scaleStr = JsonBody.GetString(body, "scale");
            var localSpace = JsonBody.GetBool(body, "local_space", false);

            var hasName = !string.IsNullOrEmpty(name);
            var hasTag = !string.IsNullOrEmpty(tag);
            var hasLayer = layerStr != null;
            var hasActive = activeStr != null;
            var hasTransform = positionStr != null || rotationStr != null || scaleStr != null;
            if (!hasName && !hasTag && !hasLayer && !hasActive && !hasTransform)
                return ToolDispatchResult.Fail("missing_parameter",
                    "Provide at least one of: name, tag, layer, active, position, rotation, scale.");

            Undo.RecordObject(go.transform, "MCP Modify GameObject");
            if (hasName) Undo.RecordObject(go, "MCP Modify GameObject");

            if (hasTag)
            {
                if (!IsTagValid(tag))
                    return ToolDispatchResult.Fail("invalid_tag",
                        $"Tag '{tag}' is not defined. Use unity_open_mcp_editor_get_tags (Plan 5) or the Tag Manager to enumerate valid tags.");
                go.tag = tag;
            }

            if (hasLayer)
            {
                if (!int.TryParse(StripQuotes(layerStr), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var layer) || layer < 0 || layer > 31)
                    return ToolDispatchResult.Fail("invalid_layer",
                        $"Layer must be an integer 0-31, got '{layerStr}'.");
                go.layer = layer;
            }

            if (hasActive)
            {
                var active = activeStr != null && activeStr.Trim() == "true";
                go.SetActive(active);
            }

            if (hasName) go.name = name;

            if (hasTransform)
            {
                var pos = positionStr != null
                    ? PrefabTools.ParseVector(positionStr, localSpace ? go.transform.localPosition : go.transform.position)
                    : (localSpace ? go.transform.localPosition : go.transform.position);
                var rot = rotationStr != null
                    ? PrefabTools.ParseVector(rotationStr, localSpace ? go.transform.localEulerAngles : go.transform.eulerAngles)
                    : (localSpace ? go.transform.localEulerAngles : go.transform.eulerAngles);
                var scl = scaleStr != null
                    ? PrefabTools.ParseVector(scaleStr, go.transform.localScale)
                    : go.transform.localScale;
                ApplyTransform(go.transform, pos, rot, scl, localSpace);
            }

            EditorUtility.SetDirty(go);
            MarkActiveSceneDirty();

            return ToolDispatchResult.Ok(BuildGameObjectResult(go, "modified"));
        }

        public static ToolDispatchResult SetParent(string body)
        {
            var resolved = ResolveInstance(body);
            if (!resolved.Ok) return resolved.Result;
            var go = resolved.GameObject;

            var parentPath = JsonBody.GetString(body, "parent_path");
            var parentInstanceId = JsonBody.GetInt(body, "parent_instance_id", 0);
            var worldPositionStays = JsonBody.GetBool(body, "world_position_stays", true);

            GameObject parentGo = null;
            if (parentInstanceId != 0)
            {
                parentGo = TypedTargets.FindByInstanceId(parentInstanceId);
            }
            else if (!string.IsNullOrEmpty(parentPath))
            {
                parentGo = TypedTargets.FindByPath(parentPath);
            }
            else
            {
                return ToolDispatchResult.Fail("missing_parameter",
                    "Provide 'parent_path' or 'parent_instance_id' to identify the new parent. " +
                    "Pass an empty parent_path with parent_instance_id=0 to detach to scene root.");
            }

            if (parentGo == null)
                return ToolDispatchResult.Fail("parent_not_found",
                    $"Parent GameObject not found (parent_instance_id={parentInstanceId}, parent_path='{parentPath}').");

            if (parentGo == go)
                return ToolDispatchResult.Fail("invalid_parameter",
                    "Cannot parent a GameObject under itself.");

            // Detect cycles: walking up from the new parent must not hit `go`.
            var walker = parentGo.transform.parent;
            while (walker != null)
            {
                if (walker == go.transform)
                    return ToolDispatchResult.Fail("invalid_parameter",
                        $"Cannot parent '{go.name}' under '{parentGo.name}': '{parentGo.name}' is a descendant of '{go.name}'.");
                walker = walker.parent;
            }

            Undo.SetTransformParent(go.transform, parentGo.transform, "MCP Set Parent");
            go.transform.SetParent(parentGo.transform, worldPositionStays);

            EditorUtility.SetDirty(go);
            MarkActiveSceneDirty();

            return ToolDispatchResult.Ok(BuildGameObjectResult(go, "reparented"));
        }

        // ----------------------------- helpers -----------------------------

        public struct InstanceResolve
        {
            public bool Ok;
            public GameObject GameObject;
            public string Name;
            public ToolDispatchResult Result;
        }

        public static InstanceResolve ResolveInstance(string body)
        {
            var instanceId = JsonBody.GetInt(body, "instance_id", 0);
            var path = JsonBody.GetString(body, "path");
            var name = JsonBody.GetString(body, "name");

            var go = TypedTargets.ResolveGameObject(instanceId, path, name);
            if (go == null)
                return new InstanceResolve
                {
                    Ok = false,
                    Result = ToolDispatchResult.Fail("gameobject_not_found",
                        $"GameObject not found (instance_id={instanceId}, path='{path}', name='{name}').")
                };

            return new InstanceResolve { Ok = true, GameObject = go, Name = go.name };
        }

        static PrimitiveType? ParsePrimitive(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            return raw.ToLowerInvariant() switch
            {
                "cube" => PrimitiveType.Cube,
                "sphere" => PrimitiveType.Sphere,
                "capsule" => PrimitiveType.Capsule,
                "cylinder" => PrimitiveType.Cylinder,
                "plane" => PrimitiveType.Plane,
                "quad" => PrimitiveType.Quad,
                _ => (PrimitiveType?)null,
            };
        }

        static void ApplyTransform(Transform t, Vector3 position, Vector3 rotationEuler, Vector3 scale, bool localSpace)
        {
            if (localSpace)
            {
                t.localPosition = position;
                t.localEulerAngles = rotationEuler;
            }
            else
            {
                t.SetPositionAndRotation(position, Quaternion.Euler(rotationEuler));
            }
            t.localScale = scale;
        }

        static bool Matches(GameObject go, string nameContains, string tag, string component)
        {
            if (!string.IsNullOrEmpty(nameContains) && go.name.IndexOf(nameContains, System.StringComparison.Ordinal) < 0)
                return false;
            if (!string.IsNullOrEmpty(tag) && go.tag != tag)
                return false;
            if (!string.IsNullOrEmpty(component) && go.GetComponent(component) == null)
                return false;
            return true;
        }

        static void CollectMatches(GameObject go, List<GameObject> sink, string nameContains, string tag, string component)
        {
            if (Matches(go, nameContains, tag, component)) sink.Add(go);
            foreach (Transform child in go.transform)
                CollectMatches(child.gameObject, sink, nameContains, tag, component);
        }

        static bool IsTagValid(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            try { return UnityInternalEditorUtilityTagsContains(tag); }
            catch { return false; }
        }

        static bool UnityInternalEditorUtilityTagsContains(string tag)
        {
            // UnityEditorInternal.InternalEditorUtility.tags — kept behind a
            // reflection shim so this file compiles against Unity versions
            // where the API surface differs.
            var tags = UnityEditorInternal.InternalEditorUtility.tags;
            foreach (var t in tags) if (t == tag) return true;
            return false;
        }

        static string StripQuotes(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2);
            return s;
        }

        // Inline JSON snapshot for one GameObject — instanceId, name, path,
        // active, tag, layer, top-level transform values, and component
        // short-names. Used by create / modify / set_parent / duplicate and
        // by find's per-result rows.
        public static string BuildGameObjectSummary(GameObject go)
        {
            var t = go.transform;
            var sb = new StringBuilder(256);
            sb.Append("{\"instanceId\":").Append(go.GetInstanceID());
            sb.Append(",\"name\":\"").Append(TypedTargets.Esc(go.name));
            sb.Append("\",\"path\":\"").Append(TypedTargets.Esc(TypedTargets.HierarchyPath(go)));
            sb.Append("\",\"active\":").Append(go.activeInHierarchy ? "true" : "false");
            sb.Append(",\"tag\":\"").Append(TypedTargets.Esc(go.tag));
            sb.Append("\",\"layer\":").Append(go.layer);
            sb.Append(",\"scene\":\"").Append(TypedTargets.Esc(go.scene.path));
            sb.Append("\",\"transform\":");
            AppendTransform(sb, t);
            sb.Append(",\"components\":[");
            var comps = go.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var c = comps[i];
                sb.Append("{\"name\":\"").Append(TypedTargets.Esc(c == null ? "<missing>" : c.GetType().Name));
                sb.Append("\",\"fullName\":\"").Append(TypedTargets.Esc(c == null ? "" : c.GetType().FullName));
                sb.Append("\",\"instanceId\":").Append(c == null ? 0 : c.GetInstanceID()).Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static void AppendTransform(StringBuilder sb, Transform t)
        {
            sb.Append("{\"position\":");
            AppendVector(sb, t.position);
            sb.Append(",\"rotation\":");
            AppendVector(sb, t.eulerAngles);
            sb.Append(",\"localPosition\":");
            AppendVector(sb, t.localPosition);
            sb.Append(",\"localRotation\":");
            AppendVector(sb, t.localEulerAngles);
            sb.Append(",\"localScale\":");
            AppendVector(sb, t.localScale);
            sb.Append(",\"right\":");
            AppendVector(sb, t.right);
            sb.Append(",\"up\":");
            AppendVector(sb, t.up);
            sb.Append(",\"forward\":");
            AppendVector(sb, t.forward);
            sb.Append('}');
        }

        static void AppendVector(StringBuilder sb, Vector3 v)
        {
            sb.Append('[');
            sb.Append(v.x.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(',').Append(v.y.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(',').Append(v.z.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(']');
        }

        static string BuildGameObjectResult(GameObject go, string action)
        {
            var summary = BuildGameObjectSummary(go);
            var sb = new StringBuilder(16 + summary.Length + action.Length);
            sb.Append("{\"status\":\"ok\",\"action\":\"").Append(action).Append("\",");
            // Strip the leading '{' off the summary so we splice fields into
            // the outer object cleanly.
            sb.Append(summary.Substring(1));
            return sb.ToString();
        }

        static void MarkActiveSceneDirty()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
