#pragma warning disable CS0618
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityOpenMcpBridge.TypedTools
{
    // M16 Plan 1 — typed prefab lifecycle tools. Covers instantiate / create /
    // stage open+close+save / instance apply+revert+unpack / status+overrides.
    // Mutation tools run through the gate envelope with paths_hint. Status and
    // overrides are gate-free reads.
    //
    // Resolve target scene instances by instance_id (canonical) > path > name
    // via TypedTargets — same addressing model as spatial-query.ts so agents
    // can pass the same handle across typed tools.
    public static class PrefabTools
    {
        public static ToolDispatchResult Instantiate(string body)
        {
            var prefabAssetPath = JsonBody.GetString(body, "prefab_asset_path");
            if (string.IsNullOrWhiteSpace(prefabAssetPath))
                return ToolDispatchResult.Fail("missing_parameter", "'prefab_asset_path' is required.");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath);
            if (prefab == null)
                return ToolDispatchResult.Fail("prefab_not_found",
                    $"Prefab not found at '{prefabAssetPath}'.");

            var name = JsonBody.GetString(body, "name");
            var parentPath = JsonBody.GetString(body, "parent_path");
            var position = ParseVector(JsonBody.GetString(body, "position"), Vector3.zero);
            var rotation = ParseVector(JsonBody.GetString(body, "rotation"), Vector3.zero);
            var scale = ParseVector(JsonBody.GetString(body, "scale"), Vector3.one);

            GameObject parentGo = null;
            if (!string.IsNullOrEmpty(parentPath))
            {
                parentGo = TypedTargets.FindByPath(parentPath);
                if (parentGo == null)
                    return ToolDispatchResult.Fail("parent_not_found",
                        $"Parent GameObject not found at '{parentPath}'.");
            }

            GameObject instance;
            try
            {
                instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null)
                    return ToolDispatchResult.Fail("instantiate_failed",
                        $"PrefabUtility.InstantiatePrefab returned null for '{prefabAssetPath}'.");
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("instantiate_failed", e.Message);
            }

            if (!string.IsNullOrEmpty(name)) instance.name = name;
            if (parentGo != null) instance.transform.SetParent(parentGo.transform, false);

            var t = instance.transform;
            t.SetPositionAndRotation(position, Quaternion.Euler(rotation));
            t.localScale = scale;

            EditorUtility.SetDirty(instance);

            var sb = new StringBuilder(192);
            sb.Append("{\"status\":\"ok\",\"prefabAssetPath\":\"").Append(TypedTargets.Esc(prefabAssetPath));
            sb.Append("\",\"instanceId\":").Append(instance.GetInstanceID());
            sb.Append(",\"name\":\"").Append(TypedTargets.Esc(instance.name));
            sb.Append("\",\"path\":\"").Append(TypedTargets.Esc(TypedTargets.HierarchyPath(instance)));
            sb.Append("\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult Create(string body)
        {
            var prefabAssetPath = JsonBody.GetString(body, "prefab_asset_path");
            if (string.IsNullOrWhiteSpace(prefabAssetPath))
                return ToolDispatchResult.Fail("missing_parameter", "'prefab_asset_path' is required.");

            var normalized = prefabAssetPath.Replace('\\', '/').Trim('/');
            if (!normalized.StartsWith("Assets/") || !normalized.EndsWith(".prefab"))
                return ToolDispatchResult.Fail("invalid_paths",
                    $"prefab_asset_path must start with 'Assets/' and end with '.prefab': '{normalized}'.");

            var instanceId = JsonBody.GetInt(body, "instance_id", 0);
            var path = JsonBody.GetString(body, "path");
            var name = JsonBody.GetString(body, "name");
            var connect = JsonBody.GetBool(body, "connect", true);

            var sourceGo = TypedTargets.ResolveGameObject(instanceId, path, name);
            if (sourceGo == null)
                return ToolDispatchResult.Fail("gameobject_not_found",
                    $"Source GameObject not found (instance_id={instanceId}, path='{path}', name='{name}').");

            // Ensure intermediate folders.
            var lastSlash = normalized.LastIndexOf('/');
            if (lastSlash > 0)
                MaterialTools.EnsureFolderRecursive(normalized.Substring(0, lastSlash));

            GameObject prefab;
            try
            {
                prefab = connect
                    ? PrefabUtility.SaveAsPrefabAssetAndConnect(sourceGo, normalized, InteractionMode.AutomatedAction, out _)
                    : PrefabUtility.SaveAsPrefabAsset(sourceGo, normalized);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed", e.Message);
            }
            if (prefab == null)
                return ToolDispatchResult.Fail("create_failed",
                    $"PrefabUtility.SaveAsPrefabAsset returned null for '{normalized}'.");

            var sb = new StringBuilder(192);
            sb.Append("{\"status\":\"ok\",\"path\":\"").Append(TypedTargets.Esc(normalized));
            sb.Append("\",\"name\":\"").Append(TypedTargets.Esc(prefab.name));
            sb.Append("\",\"instanceId\":").Append(prefab.GetInstanceID());
            sb.Append(",\"sourceInstanceId\":").Append(sourceGo.GetInstanceID());
            sb.Append(",\"isPrefabInstance\":").Append(PrefabUtility.IsPartOfPrefabInstance(sourceGo) ? "true" : "false");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult Open(string body)
        {
            var prefabAssetPath = JsonBody.GetString(body, "prefab_asset_path");
            if (string.IsNullOrWhiteSpace(prefabAssetPath))
                return ToolDispatchResult.Fail("missing_parameter", "'prefab_asset_path' is required.");

            PrefabStage stage;
            try
            {
                stage = PrefabStageUtility.OpenPrefab(prefabAssetPath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("open_failed", e.Message);
            }
            if (stage == null)
                return ToolDispatchResult.Fail("open_failed",
                    $"PrefabStageUtility.OpenPrefab returned null for '{prefabAssetPath}'.");

            var sb = new StringBuilder(128);
            sb.Append("{\"status\":\"ok\",\"prefabAssetPath\":\"").Append(TypedTargets.Esc(prefabAssetPath));
            sb.Append("\",\"stageOpen\":true}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult Close(string body)
        {
            var save = JsonBody.GetBool(body, "save", true);
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return ToolDispatchResult.Ok("{\"status\":\"noop\",\"note\":\"No prefab stage is currently open.\"}");

            var prefabContentsRoot = stage.prefabContentsRoot;
            var assetPath = stage.assetPath;

            try
            {
                if (save && prefabContentsRoot != null && !string.IsNullOrEmpty(assetPath))
                    PrefabUtility.SaveAsPrefabAsset(prefabContentsRoot, assetPath);
                // ClearDirtiness + GoBackToPreviousStage are the supported way
                // to exit a prefab stage. PrefabStage.Save()/Close() exist but
                // are protected/internal (CS0122).
                stage.ClearDirtiness();
                StageUtility.GoBackToPreviousStage();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("close_failed", e.Message);
            }

            var sb = new StringBuilder(96);
            sb.Append("{\"status\":\"ok\",\"saved\":").Append(save ? "true" : "false");
            sb.Append(",\"prefabAssetPath\":\"").Append(TypedTargets.Esc(assetPath));
            sb.Append("\",\"stageOpen\":false}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult Save(string body)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return ToolDispatchResult.Ok("{\"status\":\"noop\",\"note\":\"No prefab stage is currently open.\"}");

            var prefabContentsRoot = stage.prefabContentsRoot;
            var assetPath = stage.assetPath;
            if (prefabContentsRoot == null || string.IsNullOrEmpty(assetPath))
                return ToolDispatchResult.Fail("save_failed",
                    "Open prefab stage is missing prefabContentsRoot or assetPath.");

            try
            {
                // SaveAsPrefabAsset + ClearDirtiness; PrefabStage.Save() is
                // protected (CS0122).
                PrefabUtility.SaveAsPrefabAsset(prefabContentsRoot, assetPath);
                stage.ClearDirtiness();
            }
            catch (System.Exception e) { return ToolDispatchResult.Fail("save_failed", e.Message); }

            var sb = new StringBuilder(96);
            sb.Append("{\"status\":\"ok\",\"prefabAssetPath\":\"").Append(TypedTargets.Esc(assetPath));
            sb.Append("\",\"saved\":true}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult Apply(string body)
        {
            var resolved = ResolveInstance(body);
            if (!resolved.Ok) return resolved.Result;
            var go = resolved.GameObject;

            string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            try { PrefabUtility.ApplyPrefabInstance(go, InteractionMode.AutomatedAction); }
            catch (System.Exception e) { return ToolDispatchResult.Fail("apply_failed", e.Message); }

            MarkSceneDirty(go);
            var sb = new StringBuilder(128);
            sb.Append("{\"status\":\"ok\",\"assetPath\":\"").Append(TypedTargets.Esc(assetPath));
            sb.Append("\",\"name\":\"").Append(TypedTargets.Esc(go.name)).Append("\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult Revert(string body)
        {
            var resolved = ResolveInstance(body);
            if (!resolved.Ok) return resolved.Result;
            var go = resolved.GameObject;

            try { PrefabUtility.RevertPrefabInstance(go, InteractionMode.AutomatedAction); }
            catch (System.Exception e) { return ToolDispatchResult.Fail("revert_failed", e.Message); }

            MarkSceneDirty(go);
            var sb = new StringBuilder(96);
            sb.Append("{\"status\":\"ok\",\"name\":\"").Append(TypedTargets.Esc(go.name)).Append("\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult Unpack(string body)
        {
            var resolved = ResolveInstance(body);
            if (!resolved.Ok) return resolved.Result;
            var go = resolved.GameObject;

            var completely = JsonBody.GetBool(body, "completely", false);
            var mode = completely ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;

            try { PrefabUtility.UnpackPrefabInstance(go, mode, InteractionMode.AutomatedAction); }
            catch (System.Exception e) { return ToolDispatchResult.Fail("unpack_failed", e.Message); }

            MarkSceneDirty(go);
            var sb = new StringBuilder(96);
            sb.Append("{\"status\":\"ok\",\"name\":\"").Append(TypedTargets.Esc(go.name));
            sb.Append("\",\"mode\":\"").Append(mode.ToString()).Append("\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult GetOverrides(string body)
        {
            var resolved = ResolveInstance(body);
            if (!resolved.Ok) return resolved.Result;
            var go = resolved.GameObject;

            var sb = new StringBuilder(512);
            sb.Append("{\"name\":\"").Append(TypedTargets.Esc(go.name)).Append("\"");

            var modifications = PrefabUtility.GetPropertyModifications(go);
            sb.Append(",\"propertyModifications\":[");
            if (modifications != null)
            {
                for (int i = 0; i < modifications.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    var m = modifications[i];
                    sb.Append("{\"target\":\"").Append(TypedTargets.Esc(m.target != null ? m.target.GetType().Name : "null"));
                    sb.Append("\",\"propertyPath\":\"").Append(TypedTargets.Esc(m.propertyPath));
                    sb.Append("\",\"value\":\"").Append(TypedTargets.Esc(m.value ?? "")).Append("\"}");
                }
            }
            sb.Append("]");

            var added = PrefabUtility.GetAddedComponents(go);
            sb.Append(",\"addedComponents\":[");
            if (added != null)
            {
                for (int i = 0; i < added.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var ac = added[i];
                    sb.Append("{\"component\":\"").Append(TypedTargets.Esc(ac.instanceComponent.GetType().Name));
                    sb.Append("\",\"instanceId\":").Append(ac.instanceComponent.GetInstanceID()).Append("}");
                }
            }
            sb.Append("]");

            var removed = PrefabUtility.GetRemovedComponents(go);
            sb.Append(",\"removedComponents\":[");
            if (removed != null)
            {
                for (int i = 0; i < removed.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var rc = removed[i];
                    sb.Append("{\"component\":\"").Append(TypedTargets.Esc(rc.assetComponent.GetType().Name)).Append("\"}");
                }
            }
            sb.Append("]");

            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult Status(string body)
        {
            // Status accepts the same target address as the other tools but
            // also operates on a prefab asset path (no scene instance).
            var assetPath = JsonBody.GetString(body, "asset_path");
            if (!string.IsNullOrEmpty(assetPath))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab == null)
                    return ToolDispatchResult.Fail("prefab_not_found",
                        $"Prefab not found at '{assetPath}'.");

                return ToolDispatchResult.Ok(BuildAssetStatus(prefab, assetPath));
            }

            var resolved = ResolveInstance(body);
            if (!resolved.Ok) return resolved.Result;
            return ToolDispatchResult.Ok(BuildInstanceStatus(resolved.GameObject));
        }

        // ----------------------------- helpers -----------------------------

        public struct InstanceResolve
        {
            public bool Ok;
            public GameObject GameObject;
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

            if (!PrefabUtility.IsPartOfAnyPrefab(go))
                return new InstanceResolve
                {
                    Ok = false,
                    Result = ToolDispatchResult.Fail("not_a_prefab",
                        $"GameObject '{go.name}' is not part of a prefab.")
                };

            return new InstanceResolve { Ok = true, GameObject = go };
        }

        private static string BuildInstanceStatus(GameObject go)
        {
            var sb = new StringBuilder(192);
            bool isInstance = PrefabUtility.IsPartOfPrefabInstance(go);
            bool isRoot = PrefabUtility.IsOutermostPrefabInstanceRoot(go);
            bool hasOverrides = false;
            string sourcePath = "";
            string sourceName = "";
            if (isInstance)
            {
                try { hasOverrides = PrefabUtility.HasPrefabInstanceAnyOverrides(go, false); } catch { }
                var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
                if (source != null)
                {
                    sourcePath = AssetDatabase.GetAssetPath(source);
                    sourceName = source.name;
                }
            }

            sb.Append("{\"name\":\"").Append(TypedTargets.Esc(go.name));
            sb.Append("\",\"isPrefab\":true");
            sb.Append(",\"isInstance\":").Append(isInstance ? "true" : "false");
            sb.Append(",\"isRoot\":").Append(isRoot ? "true" : "false");
            sb.Append(",\"hasOverrides\":").Append(hasOverrides ? "true" : "false");
            sb.Append(",\"instanceId\":").Append(go.GetInstanceID());
            sb.Append(",\"sourcePath\":\"").Append(TypedTargets.Esc(sourcePath));
            sb.Append("\",\"sourceName\":\"").Append(TypedTargets.Esc(sourceName)).Append("\"}");
            return sb.ToString();
        }

        private static string BuildAssetStatus(GameObject prefab, string assetPath)
        {
            var kind = PrefabUtility.GetPrefabAssetType(prefab);
            var sb = new StringBuilder(128);
            sb.Append("{\"name\":\"").Append(TypedTargets.Esc(prefab.name));
            sb.Append("\",\"isPrefab\":true,\"isInstance\":false");
            sb.Append(",\"isAsset\":true");
            sb.Append(",\"assetPath\":\"").Append(TypedTargets.Esc(assetPath));
            sb.Append("\",\"assetType\":\"").Append(kind.ToString()).Append("\"}");
            return sb.ToString();
        }

        private static void MarkSceneDirty(GameObject go)
        {
            var scene = go.scene;
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
        }

        // Parse "x,y,z" → Vector3. Returns `fallback` on parse failure.
        //
        // Canonical vector parser for the bridge. Several TypedTools copies
        // (Extensions/Navigation/NavigationTools.cs, ProBuilder/ProBuilderTools.cs,
        // Splines/SplinesTools.cs) ship private ParseVector3 helpers that are
        // near-identical EXCEPT they reject `parts.Length != 3` strictly, while
        // this one accepts `parts.Length < 3` (so "1,2,3,4" parses to (1,2,3)
        // here but returns fallback there). The two behaviors only differ on
        // malformed input no legitimate caller sends; do NOT silently collapse
        // the copies onto this one without auditing each call site's arity
        // expectation. See PrefabToolsTests.ParseVector_* for the pinned cases.
        public static Vector3 ParseVector(string raw, Vector3 fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            var trimmed = raw.Trim();
            var parts = trimmed.Split(',');
            if (parts.Length < 3) return fallback;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return fallback;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return fallback;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z)) return fallback;
            return new Vector3(x, y, z);
        }
    }
}
