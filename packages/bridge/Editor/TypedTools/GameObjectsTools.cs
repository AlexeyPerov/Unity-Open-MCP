using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityOpenMcpBridge.ObjectRefs;

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
                // Prefab instances must clone via PrefabUtility.InstantiatePrefab —
                // Object.Instantiate strips the prefab connection and produces a
                // broken disconnected copy (PrefabTools.Instantiate uses the same
                // API at PrefabTools.cs:49 as the reference). Re-apply the
                // caller's local pose after InstantiatePrefab (it does not take a
                // world pose), then place the clone next to the source (editor
                // duplicate sibling-index semantics).
                if (PrefabUtility.IsPartOfPrefabInstance(go))
                {
                    clone = PrefabUtility.InstantiatePrefab(go, go.transform.parent) as GameObject;
                    if (clone == null)
                        return ToolDispatchResult.Fail("duplicate_failed",
                            "PrefabUtility.InstantiatePrefab returned null.");
                }
                else
                {
                    clone = Object.Instantiate(go, go.transform.parent, false);
                }
                // Name after Instantiate appends "(Clone)" — match editor
                // duplicate semantics so the result name is stable.
                clone.name = go.name;

                // Re-apply the source's local transform so InstantiatePrefab
                // (which takes no pose) lands the clone at the same local pose.
                var srcT = go.transform;
                clone.transform.localPosition = srcT.localPosition;
                clone.transform.localRotation = srcT.localRotation;
                clone.transform.localScale = srcT.localScale;

                // Editor duplicate places the clone immediately after the source
                // rather than at the parent end.
                clone.transform.SetSiblingIndex(srcT.GetSiblingIndex() + 1);
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
            var instanceId = JsonBody.GetLongFlexible(body, "instance_id", 0);
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
        //
        // M22 T22.1.4 — three-surface RFC 7396 form (additive, backwards-
        // compatible). In addition to the legacy flat fields, the caller may
        // provide three grouped surfaces:
        //   - gameObjectDiffs          : top-level patches for the root target.
        //   - pathPatchesPerGameObject : {childPath: diffs} applied to descendants.
        //   - jsonPatchesPerGameObject : {componentTypeName: mergePatch} applied to
        //                                the root target's components via reflection.
        // Apply order (matches the established three-surface convention):
        // jsonPatches → pathPatches → gameObjectDiffs/flat. When any of the path
        // or json surfaces is present the response carries a `surfaces` summary;
        // a legacy (root-only) call keeps the original compact result shape.
        public static ToolDispatchResult Modify(string body)
        {
            // Resolve the target with name_target so `name` stays free for
            // the new value. Falls back to `name` ONLY when name_target is
            // absent — an explicit `name_target: null` must not fall through
            // to `name` (which is the new value), or the call would resolve
            // the wrong object. JsonBody.TryGetString distinguishes the two
            // null cases GetString collapses (absent vs explicit null).
            var instanceId = JsonBody.GetLongFlexible(body, "instance_id", 0);
            var path = JsonBody.GetString(body, "path");
            var nameTarget = JsonBody.TryGetString(body, "name_target", out var hasNameTarget);
            var resolveName = hasNameTarget
                ? nameTarget
                : JsonBody.GetString(body, "name");
            var go = TypedTargets.ResolveGameObject(instanceId, path, resolveName);
            if (go == null)
                return ToolDispatchResult.Fail("gameobject_not_found",
                    $"GameObject not found (instance_id={instanceId}, path='{path}', name_target='{nameTarget}').");

            // ---- surface detection (T22.1.4) ----
            var pathPatchesRaw = JsonBody.GetRawValue(body, "pathPatchesPerGameObject");
            var jsonPatchesRaw = JsonBody.GetRawValue(body, "jsonPatchesPerGameObject");
            bool hasPathPatches = !string.IsNullOrEmpty(pathPatchesRaw) && pathPatchesRaw.Trim() != "{}";
            bool hasJsonPatches = !string.IsNullOrEmpty(jsonPatchesRaw) && jsonPatchesRaw.Trim() != "{}";

            // Root diff source: gameObjectDiffs wins when present, else the
            // legacy flat fields on the body. Read fields once from whichever
            // source applies.
            var diffsSource = JsonBody.GetRawValue(body, "gameObjectDiffs");
            bool hasGameObjectDiffs = !string.IsNullOrEmpty(diffsSource) && diffsSource.Trim() != "{}";
            string rootSource = hasGameObjectDiffs ? diffsSource : body;

            // ---- surface 3: root diffs / legacy flat fields (fail-fast) ----
            var name = JsonBody.GetString(rootSource, "name");
            var tag = JsonBody.GetString(rootSource, "tag");
            var layerStr = JsonBody.GetRawValue(rootSource, "layer");
            var activeStr = JsonBody.GetRawValue(rootSource, "active");
            var positionStr = JsonBody.GetString(rootSource, "position");
            var rotationStr = JsonBody.GetString(rootSource, "rotation");
            var scaleStr = JsonBody.GetString(rootSource, "scale");
            var localSpace = JsonBody.GetBool(rootSource, "local_space", false);

            var hasName = !string.IsNullOrEmpty(name);
            var hasTag = !string.IsNullOrEmpty(tag);
            var hasLayer = layerStr != null;
            var hasActive = activeStr != null;
            var hasTransform = positionStr != null || rotationStr != null || scaleStr != null;
            bool hasRootDiff = hasName || hasTag || hasLayer || hasActive || hasTransform;

            if (!hasRootDiff && !hasGameObjectDiffs && !hasPathPatches && !hasJsonPatches)
                return ToolDispatchResult.Fail("missing_parameter",
                    "Provide at least one of: gameObjectDiffs, pathPatchesPerGameObject, " +
                    "jsonPatchesPerGameObject, or the legacy flat fields " +
                    "(name, tag, layer, active, position, rotation, scale).");

            // ---- surface 1: jsonPatchesPerGameObject (RFC 7396 merge patch) ----
            // Applied FIRST so top-level renames in the root diff don't race the
            // component lookup. Per-component errors accumulate; nothing aborts
            // the batch. `applied`/`failed` mirror the diff-surface vocabulary.
            var jsonApplied = new List<string>();
            var jsonFailed = new List<string>();
            if (hasJsonPatches)
                ApplyJsonPatchesPerGameObject(go, jsonPatchesRaw, jsonApplied, jsonFailed);

            // ---- surface 2: pathPatchesPerGameObject ----
            var pathApplied = new List<string>();
            var pathFailed = new List<string>();
            if (hasPathPatches)
                ApplyPathPatchesPerGameObject(go, pathPatchesRaw, pathApplied, pathFailed);

            // ---- surface 3: root diffs (fail-fast, keeps legacy error codes) ----
            if (hasRootDiff)
            {
                var rootErr = ApplyRootDiff(go, hasName, name, hasTag, tag, hasLayer, layerStr,
                    hasActive, activeStr, hasTransform, positionStr, rotationStr, scaleStr, localSpace);
                if (rootErr != null) return rootErr;
            }

            EditorUtility.SetDirty(go);
            MarkActiveSceneDirty();

            // ---- response shape ----
            // Legacy (root-only) call → original compact shape so existing
            // parsers are unaffected. Any path/json surface → extended shape
            // with a `surfaces` summary.
            if (!hasPathPatches && !hasJsonPatches)
                return ToolDispatchResult.Ok(BuildGameObjectResult(go, "modified"));

            return ToolDispatchResult.Ok(BuildThreeSurfaceResult(go,
                hasRootDiff, jsonApplied, jsonFailed, pathApplied, pathFailed));
        }

        // Apply the root-target diff (name/tag/layer/active/transform). Mirrors
        // the pre-T22.1.4 behavior exactly, including the fail-fast validation
        // (invalid_tag / invalid_layer) so the legacy error codes are preserved.
        // Returns a ToolDispatchResult on validation failure, null on success.
        private static ToolDispatchResult ApplyRootDiff(GameObject go,
            bool hasName, string name, bool hasTag, string tag,
            bool hasLayer, string layerStr, bool hasActive, string activeStr,
            bool hasTransform, string positionStr, string rotationStr, string scaleStr, bool localSpace)
        {
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
            return null;
        }

        // Apply per-component RFC 7396 merge patches to the root target. Each
        // top-level key names a component type (class name first, then full
        // name); the value is a {field: value} object applied via the same
        // reflection + ConvertValue path object_modify uses. Reuses
        // ReflectionScriptsObjectsTools.ApplyFieldPatches. Per-entry errors
        // accumulate and never abort the batch.
        private static void ApplyJsonPatchesPerGameObject(GameObject go, string patchesRaw,
            List<string> applied, List<string> failed)
        {
            var keys = JsonBody.GetObjectKeys(patchesRaw);
            if (keys == null) return;
            var components = go.GetComponents<Component>();

            foreach (var typeName in keys)
            {
                // Resolve the component on this GameObject by class name, then
                // full name (mirrors component_get's resolver vocabulary).
                Component comp = null;
                foreach (var c in components)
                {
                    if (c == null) continue;
                    var t = c.GetType();
                    if (t.Name == typeName || t.FullName == typeName) { comp = c; break; }
                }
                if (comp == null)
                {
                    failed.Add($"{typeName}: component not found on '{go.name}'.");
                    continue;
                }

                var mergePatchRaw = JsonBody.GetRawValue(patchesRaw, typeName);
                var fieldNames = JsonBody.GetObjectKeys(mergePatchRaw);
                if (fieldNames == null || fieldNames.Count == 0)
                {
                    failed.Add($"{typeName}: empty merge patch.");
                    continue;
                }

                // Build the {name, value} entries ApplyFieldPatches consumes.
                var entries = new string[fieldNames.Count];
                for (int i = 0; i < fieldNames.Count; i++)
                {
                    var fn = fieldNames[i];
                    var fv = JsonBody.GetRawValue(mergePatchRaw, fn);
                    entries[i] = "{\"name\":\"" + fn + "\",\"value\":" + (fv ?? "null") + "}";
                }

                var modified = new List<string>();
                var errors = new List<string>();
                ReflectionScriptsObjectsTools.ApplyFieldPatches(comp, entries, false, modified, errors);

                foreach (var m in modified) applied.Add(typeName + "." + m);
                foreach (var e in errors) failed.Add(typeName + ": " + e);
            }
        }

        // Apply per-child-path diffs to descendants of the root target. Each
        // key is a slash-delimited path resolved via transform.Find (relative to
        // the root); the value is the same diffs shape the root surface accepts.
        // Per-entry errors accumulate.
        private static void ApplyPathPatchesPerGameObject(GameObject root, string patchesRaw,
            List<string> applied, List<string> failed)
        {
            var keys = JsonBody.GetObjectKeys(patchesRaw);
            if (keys == null) return;

            foreach (var childPath in keys)
            {
                var child = root.transform.Find(childPath);
                if (child == null)
                {
                    failed.Add($"{childPath}: child not found under '{root.name}'.");
                    continue;
                }
                var childGo = child.gameObject;
                var diffsRaw = JsonBody.GetRawValue(patchesRaw, childPath);
                if (string.IsNullOrEmpty(diffsRaw) || diffsRaw.Trim() == "{}")
                {
                    failed.Add($"{childPath}: empty diff.");
                    continue;
                }

                var tag = JsonBody.GetString(diffsRaw, "tag");
                var layerStr = JsonBody.GetRawValue(diffsRaw, "layer");
                var activeStr = JsonBody.GetRawValue(diffsRaw, "active");
                var positionStr = JsonBody.GetString(diffsRaw, "position");
                var rotationStr = JsonBody.GetString(diffsRaw, "rotation");
                var scaleStr = JsonBody.GetString(diffsRaw, "scale");
                var localSpace = JsonBody.GetBool(diffsRaw, "local_space", false);
                var childName = JsonBody.GetString(diffsRaw, "name");

                Undo.RecordObject(child, "MCP Modify GameObject (path patch)");

                if (!string.IsNullOrEmpty(tag))
                {
                    if (IsTagValid(tag)) childGo.tag = tag;
                    else failed.Add($"{childPath}: tag '{tag}' is not defined.");
                }
                if (layerStr != null)
                {
                    if (int.TryParse(StripQuotes(layerStr), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var layer) && layer >= 0 && layer <= 31)
                        childGo.layer = layer;
                    else
                        failed.Add($"{childPath}: layer must be 0-31, got '{layerStr}'.");
                }
                if (activeStr != null)
                {
                    childGo.SetActive(activeStr.Trim() == "true");
                }
                if (!string.IsNullOrEmpty(childName)) childGo.name = childName;

                if (positionStr != null || rotationStr != null || scaleStr != null)
                {
                    var pos = positionStr != null
                        ? PrefabTools.ParseVector(positionStr, localSpace ? child.localPosition : child.position)
                        : (localSpace ? child.localPosition : child.position);
                    var rot = rotationStr != null
                        ? PrefabTools.ParseVector(rotationStr, localSpace ? child.localEulerAngles : child.eulerAngles)
                        : (localSpace ? child.localEulerAngles : child.eulerAngles);
                    var scl = scaleStr != null
                        ? PrefabTools.ParseVector(scaleStr, child.localScale)
                        : child.localScale;
                    ApplyTransform(child, pos, rot, scl, localSpace);
                }

                EditorUtility.SetDirty(childGo);
                applied.Add(childPath);
            }
        }

        // Build the extended three-surface result: root GO summary + a
        // `surfaces` breakdown + a flattened per-entry `errors[]` so callers
        // can scan failures in one pass. status is "ok" when at least one
        // surface applied, else "error" (the call resolved the target but
        // nothing changed — e.g. every patch failed validation).
        private static string BuildThreeSurfaceResult(GameObject go, bool rootDiffApplied,
            List<string> jsonApplied, List<string> jsonFailed,
            List<string> pathApplied, List<string> pathFailed)
        {
            var summary = BuildGameObjectSummary(go);
            var sb = new StringBuilder(256 + summary.Length);
            sb.Append("{\"status\":");
            bool anyApplied = rootDiffApplied || jsonApplied.Count > 0 || pathApplied.Count > 0;
            sb.Append(anyApplied ? "\"ok\"" : "\"error\"");
            sb.Append(",\"action\":\"modified\",");
            // Splice the root GO summary fields in (strip leading '{').
            sb.Append(summary.Substring(1));
            sb.Append(",\"surfaces\":{");
            sb.Append("\"diffs\":{\"applied\":").Append(rootDiffApplied ? "true" : "false");
            sb.Append("},\"pathPatches\":{\"applied\":");
            AppendStringArray(sb, pathApplied);
            sb.Append(",\"failed\":");
            AppendStringArray(sb, pathFailed);
            sb.Append("},\"jsonPatches\":{\"applied\":");
            AppendStringArray(sb, jsonApplied);
            sb.Append(",\"failed\":");
            AppendStringArray(sb, jsonFailed);
            sb.Append("}}");

            // Flattened errors across surfaces (non-fatal failures only).
            int errCount = jsonFailed.Count + pathFailed.Count;
            if (errCount > 0)
            {
                sb.Append(",\"errors\":[");
                int i = 0;
                foreach (var e in jsonFailed) { if (i++ > 0) sb.Append(','); sb.Append(StringQuote(e)); }
                foreach (var e in pathFailed) { if (i++ > 0) sb.Append(','); sb.Append(StringQuote(e)); }
                sb.Append("],\"errorCount\":").Append(errCount);
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendStringArray(StringBuilder sb, List<string> items)
        {
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(StringQuote(items[i]));
            }
            sb.Append(']');
        }

        private static string StringQuote(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static ToolDispatchResult SetParent(string body)
        {
            var resolved = ResolveInstance(body);
            if (!resolved.Ok) return resolved.Result;
            var go = resolved.GameObject;

            var parentPath = JsonBody.GetString(body, "parent_path");
            var parentInstanceId = JsonBody.GetLongFlexible(body, "parent_instance_id", 0);
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

            // Reparent exactly once, honoring the caller's worldPositionStays.
            // The Undo.SetTransformParent overload that takes worldPositionStays
            // (2021.3+) records the undo snapshot in the FINAL pose, so a later
            // Ctrl+Z restores the true pre-parent world transform. Calling
            // SetTransformParent then SetParent would move the transform twice;
            // with worldPositionStays=false the undo snapshot captured the
            // wrong pose (the true/intermediate one) but the live state used
            // false — Ctrl+Z then restored a pose the object never had.
            Undo.SetTransformParent(go.transform, parentGo.transform, worldPositionStays, "MCP Set Parent");

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
            var instanceId = JsonBody.GetLongFlexible(body, "instance_id", 0);
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

        private static PrimitiveType? ParsePrimitive(string raw)
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

        private static void ApplyTransform(Transform t, Vector3 position, Vector3 rotationEuler, Vector3 scale, bool localSpace)
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

        private static bool Matches(GameObject go, string nameContains, string tag, string component)
        {
            if (!string.IsNullOrEmpty(nameContains) && go.name.IndexOf(nameContains, System.StringComparison.Ordinal) < 0)
                return false;
            if (!string.IsNullOrEmpty(tag) && go.tag != tag)
                return false;
            if (!string.IsNullOrEmpty(component) && go.GetComponent(component) == null)
                return false;
            return true;
        }

        private static void CollectMatches(GameObject go, List<GameObject> sink, string nameContains, string tag, string component)
        {
            if (Matches(go, nameContains, tag, component)) sink.Add(go);
            foreach (Transform child in go.transform)
                CollectMatches(child.gameObject, sink, nameContains, tag, component);
        }

        private static bool IsTagValid(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            try { return UnityInternalEditorUtilityTagsContains(tag); }
            catch { return false; }
        }

        private static bool UnityInternalEditorUtilityTagsContains(string tag)
        {
            // UnityEditorInternal.InternalEditorUtility.tags — kept behind a
            // reflection shim so this file compiles against Unity versions
            // where the API surface differs.
            var tags = UnityEditorInternal.InternalEditorUtility.tags;
            foreach (var t in tags) if (t == tag) return true;
            return false;
        }

        private static string StripQuotes(string s)
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
            sb.Append("{\"instanceId\":").Append(InstanceId.ToJson(go));
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
                sb.Append("\",\"instanceId\":").Append(InstanceId.ToJson(c)).Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendTransform(StringBuilder sb, Transform t)
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

        private static void AppendVector(StringBuilder sb, Vector3 v)
        {
            sb.Append('[');
            sb.Append(v.x.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(',').Append(v.y.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(',').Append(v.z.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(']');
        }

        private static string BuildGameObjectResult(GameObject go, string action)
        {
            var summary = BuildGameObjectSummary(go);
            var sb = new StringBuilder(16 + summary.Length + action.Length);
            sb.Append("{\"status\":\"ok\",\"action\":\"").Append(action).Append("\",");
            // Strip the leading '{' off the summary so we splice fields into
            // the outer object cleanly.
            sb.Append(summary.Substring(1));
            return sb.ToString();
        }

        private static void MarkActiveSceneDirty()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
