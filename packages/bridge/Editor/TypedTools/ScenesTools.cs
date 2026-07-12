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
    // M16 Plan 3 — typed scene lifecycle and data tools. Covers create / open /
    // save / unload / set_active / list_opened / get_data / get_dirty_summary /
    // focus. Mutation tools run through the gate envelope with paths_hint.
    // list_opened / get_data / get_dirty_summary are gate-free reads.
    //
    // `scene_get_data` is the structured scene hierarchy read that supersedes
    // the standalone M10 scene snapshot (T3.8), unifying summarize +
    // hierarchy_describe into the `detail` modes (summary / normal / verbose).
    // It reflects unsaved editor state (unlike read_asset on the .unity file,
    // which only shows the last-saved YAML).
    //
    // Mutating tools are undo-recorded where the Unity API supports it.
    // `scene_open` runs on the RestartThenSettle lifecycle path so the active-
    // scene dirty guard preflights it (Single-mode open can lose unsaved
    // changes in currently-open scenes); the other mutators are EditorSettle.
    //
    // These tools are NOT registry-discovered: they are wired into
    // BridgeHttpServer.DispatchTool alongside the other M16 typed tools so
    // their snake_case schemas parse the same way.
    public static class ScenesTools
    {
        // ------------------------- lifecycle ------------------------------

        public static ToolDispatchResult Create(string body)
        {
            var path = JsonBody.GetString(body, "path");
            if (string.IsNullOrWhiteSpace(path))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'path' is required and must be a non-empty string.");

            var normalized = NormalizeScenePath(path);
            if (normalized == null)
                return ToolDispatchResult.Fail("invalid_parameter",
                    $"'path' must end with '.unity': '{path}'.");

            var setup = ParseSetup(JsonBody.GetString(body, "setup"), NewSceneSetup.EmptyScene);
            var mode = ParseNewSceneMode(JsonBody.GetString(body, "mode"), NewSceneMode.Single);

            // Ensure the parent folder exists so SaveScene does not fail
            // silently — mirrors MaterialTools / prefab_create behavior.
            var lastSlash = normalized.LastIndexOf('/');
            if (lastSlash > 0)
                MaterialTools.EnsureFolderRecursive(normalized.Substring(0, lastSlash));

            Scene scene;
            try
            {
                scene = EditorSceneManager.NewScene(setup, mode);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed", e.Message);
            }
            if (!scene.IsValid())
                return ToolDispatchResult.Fail("create_failed",
                    "EditorSceneManager.NewScene returned an invalid scene.");

            bool saved;
            try
            {
                saved = EditorSceneManager.SaveScene(scene, normalized);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_failed",
                    $"Scene was created but could not be saved to '{normalized}': {e.Message}");
            }
            if (!saved)
                return ToolDispatchResult.Fail("create_failed",
                    $"Failed to save scene at '{normalized}'.");

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // After SaveScene the in-memory scene name should match the asset
            // filename stem, but in some Unity versions/additive paths the
            // scene.name can lag the on-disk asset (staying "Untitled" or the
            // previous name). Re-open the asset so the opened-scene stack
            // reflects the saved name+path — subsequent name-only lookups then
            // resolve reliably. This is the complementary hardening called out
            // in the scene-path-identity plan: path-based lookup is primary,
            // but name should not silently drift.
            scene = SyncCreatedSceneName(scene, normalized);

            var sb = new StringBuilder(128);
            sb.Append("{\"status\":\"ok\",\"action\":\"created\",");
            sb.Append("\"scene\":").Append(BuildSceneShallow(scene)).Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult Open(string body)
        {
            var path = JsonBody.GetString(body, "path");
            if (string.IsNullOrWhiteSpace(path))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'path' is required and must be a non-empty string.");

            var normalized = NormalizeScenePath(path);
            if (normalized == null)
                return ToolDispatchResult.Fail("invalid_parameter",
                    $"'path' must end with '.unity': '{path}'.");

            if (!System.IO.File.Exists(System.IO.Path.GetFullPath(normalized)))
                return ToolDispatchResult.Fail("scene_not_found",
                    $"No scene file at '{normalized}'.");

            var mode = ParseOpenSceneMode(JsonBody.GetString(body, "mode"), OpenSceneMode.Single);

            Scene opened;
            try
            {
                opened = EditorSceneManager.OpenScene(normalized, mode);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("open_failed", e.Message);
            }
            if (!opened.IsValid() || !opened.isLoaded)
                return ToolDispatchResult.Fail("open_failed",
                    $"Failed to load scene at '{normalized}'.");

            return ToolDispatchResult.Ok(BuildOpenedScenesEnvelope("opened", opened.path));
        }

        public static ToolDispatchResult Save(string body)
        {
            var name = JsonBody.GetString(body, "name");
            var destPath = JsonBody.GetString(body, "path");

            // Scene identity: `name` selects an opened scene; `path` is
            // primarily the save-as destination, but when it matches an opened
            // scene's asset path it also disambiguates identity (the common
            // "save this exact opened scene" case). Precedence:
            //   1. name resolves  → use that scene (path, if any, is save-as dest)
            //   2. path resolves to an opened scene → use that scene, save to its own path
            //      (path-as-identity: destPath is treated as identity, not a new dest)
            //   3. path does not match an opened scene + no name → active scene, save-as to destPath
            //   4. neither → active scene
            Scene scene;
            bool pathIsIdentity = false;
            if (!string.IsNullOrWhiteSpace(name))
            {
                scene = ResolveOpenedByName(name);
            }
            else if (!string.IsNullOrWhiteSpace(destPath))
            {
                // path could be identity (an opened scene's asset path) or a
                // save-as destination for the active scene. Resolve identity
                // first; if it hits, the save goes back to that scene's own
                // path unless a separate save-as is intended (callers pass name
                // for that disambiguation).
                var byPath = ResolveOpenedByPath(destPath);
                if (byPath.IsValid() && byPath.isLoaded)
                {
                    scene = byPath;
                    pathIsIdentity = true;
                }
                else
                {
                    scene = SceneManager.GetActiveScene();
                }
            }
            else
            {
                scene = SceneManager.GetActiveScene();
            }

            if (!scene.IsValid() || !scene.isLoaded)
                return ToolDispatchResult.Fail("scene_not_found",
                    string.IsNullOrEmpty(name)
                        ? "No active loaded scene to save."
                        : $"No opened scene named '{name}'. Use unity_open_mcp_scene_list_opened to enumerate opened scenes.");

            // Determine the actual write path. When path was identity (matched
            // an opened scene), save back to that scene's own asset path —
            // do NOT treat it as a save-as. Otherwise path is a save-as dest.
            string savePath;
            if (pathIsIdentity)
            {
                savePath = scene.path;
            }
            else
            {
                savePath = string.IsNullOrEmpty(destPath) ? scene.path : NormalizeScenePath(destPath);
                // NormalizeScenePath returns null for a path that does not end
                // with '.unity' — that is an invalid_parameter, not a
                // missing_parameter, so check it before the empty-path guard.
                if (destPath != null && savePath == null)
                    return ToolDispatchResult.Fail("invalid_parameter",
                        $"'path' must end with '.unity': '{destPath}'.");
            }
            if (string.IsNullOrEmpty(savePath))
                return ToolDispatchResult.Fail("missing_parameter",
                    $"Scene '{scene.name}' has no path. Provide 'path' (ending with '.unity') to save-as.");

            // Idempotent: a clean scene is reported as saved:false with a note.
            // Only the asset-backed write counts as a mutation.
            if (!scene.isDirty && savePath == scene.path && !string.IsNullOrEmpty(scene.path))
            {
                var noop = new StringBuilder(96);
                noop.Append("{\"status\":\"ok\",\"saved\":false,\"name\":\"")
                    .Append(TypedTargets.Esc(scene.name))
                    .Append("\",\"path\":\"").Append(TypedTargets.Esc(savePath))
                    .Append("\",\"note\":\"Scene was not dirty; no write performed.\"}");
                return ToolDispatchResult.Ok(noop.ToString());
            }

            // Ensure the parent folder exists when save-as into a new location.
            var lastSlash = savePath.LastIndexOf('/');
            if (lastSlash > 0)
                MaterialTools.EnsureFolderRecursive(savePath.Substring(0, lastSlash));

            bool saved;
            try
            {
                saved = EditorSceneManager.SaveScene(scene, savePath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("save_failed", e.Message);
            }
            if (!saved)
                return ToolDispatchResult.Fail("save_failed",
                    $"EditorSceneManager.SaveScene returned false for '{savePath}'.");

            var sb = new StringBuilder(96);
            sb.Append("{\"status\":\"ok\",\"saved\":true,\"name\":\"")
              .Append(TypedTargets.Esc(scene.name))
              .Append("\",\"path\":\"").Append(TypedTargets.Esc(savePath)).Append("\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult Unload(string body)
        {
            var name = JsonBody.GetString(body, "name");
            var path = JsonBody.GetString(body, "path");
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(path))
                return ToolDispatchResult.Fail("missing_parameter",
                    "Either 'name' or 'path' is required to identify the scene. " +
                    "Use unity_open_mcp_scene_list_opened to enumerate opened scenes.");

            var scene = ResolveOpenedForMutator(name, path, out var resolvedBy);
            if (!scene.IsValid() || !scene.isLoaded)
                return ToolDispatchResult.Fail("scene_not_found",
                    BuildSceneNotFoundMessage(name, path));

            var subject = !string.IsNullOrWhiteSpace(scene.name) ? scene.name
                : (!string.IsNullOrWhiteSpace(path) ? path : name);

            // Refuse to unload the last loaded scene — Unity leaves the editor
            // in an awkward state (no active scene). The agent should open a
            // replacement first.
            if (CountOpenedScenes() <= 1)
                return ToolDispatchResult.Fail("invalid_parameter",
                    $"Cannot unload '{subject}': it is the only opened scene. Open another scene first.");

            // EditorSceneManager.UnloadSceneAsync returns AsyncOperation (Unity
            // 6+; it returned bool in older versions). The editor unload is
            // effectively synchronous — the scene is removed from the loaded
            // hierarchy before the call returns — so we treat a null return /
            // thrown exception as failure rather than awaiting the operation.
            // We are NOT using the runtime SceneManager overload; the editor
            // surface is correct here because we are not in play mode.
            try
            {
                var op = EditorSceneManager.UnloadSceneAsync(scene);
                if (op == null)
                    return ToolDispatchResult.Fail("unload_failed",
                        $"EditorSceneManager.UnloadSceneAsync returned null for '{subject}'.");
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("unload_failed", e.Message);
            }

            return ToolDispatchResult.Ok(BuildOpenedScenesEnvelope("unloaded", subject));
        }

        public static ToolDispatchResult SetActive(string body)
        {
            var name = JsonBody.GetString(body, "name");
            var path = JsonBody.GetString(body, "path");
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(path))
                return ToolDispatchResult.Fail("missing_parameter",
                    "Either 'name' or 'path' is required to identify the scene. " +
                    "Use unity_open_mcp_scene_list_opened to enumerate opened scenes.");

            var scene = ResolveOpenedForMutator(name, path, out var resolvedBy);
            if (!scene.IsValid() || !scene.isLoaded)
                return ToolDispatchResult.Fail("scene_not_found",
                    BuildSceneNotFoundMessage(name, path));

            var subject = !string.IsNullOrWhiteSpace(scene.name) ? scene.name
                : (!string.IsNullOrWhiteSpace(path) ? path : name);

            // Idempotent: a no-op when the scene is already active.
            if (EditorSceneManager.GetActiveScene() == scene)
                return ToolDispatchResult.Ok(BuildOpenedScenesEnvelope("set_active_noop", subject));

            bool ok;
            try
            {
                ok = EditorSceneManager.SetActiveScene(scene);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("set_active_failed", e.Message);
            }
            if (!ok)
                return ToolDispatchResult.Fail("set_active_failed",
                    $"EditorSceneManager.SetActiveScene returned false for '{subject}'.");

            return ToolDispatchResult.Ok(BuildOpenedScenesEnvelope("set_active", subject));
        }

        // --------------------------- reads ---------------------------------

        // ListOpened returns a shallow snapshot of every opened scene. Gate-free.
        public static ToolDispatchResult ListOpened(string body)
        {
            return ToolDispatchResult.Ok(BuildOpenedScenesEnvelope("list_opened", null));
        }

        // GetDirtySummary reports the dirty flag + rootCount for every opened
        // scene, highlighting dirty ones. Gate-free.
        public static ToolDispatchResult GetDirtySummary(string body)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"status\":\"ok\",\"scenes\":[");
            int dirtyCount = 0;
            var opened = OpenedScenes();
            for (int i = 0; i < opened.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var scene = opened[i];
                if (scene.isDirty) dirtyCount++;
                sb.Append("{\"name\":\"").Append(TypedTargets.Esc(scene.name));
                sb.Append("\",\"path\":\"").Append(TypedTargets.Esc(scene.path));
                sb.Append("\",\"isDirty\":").Append(scene.isDirty ? "true" : "false");
                sb.Append(",\"isLoaded\":").Append(scene.isLoaded ? "true" : "false");
                sb.Append(",\"rootCount\":").Append(scene.rootCount).Append('}');
            }
            sb.Append("],\"dirtySceneCount\":").Append(dirtyCount);
            sb.Append(",\"openedSceneCount\":").Append(opened.Count).Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // GetData is the structured hierarchy read. detail controls verbosity:
        //   summary  — scene overview + root roster (name/childCount/components)
        //   normal   — + nested children to `depth` with active/tag/layer/components
        //   verbose  — + per-node instanceId and transform
        // max_nodes caps the total node count to bound token output.
        public static ToolDispatchResult GetData(string body)
        {
            var name = JsonBody.GetString(body, "name");
            var path = JsonBody.GetString(body, "path");
            Scene scene;
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(path))
            {
                scene = SceneManager.GetActiveScene();
            }
            else
            {
                scene = ResolveOpenedForMutator(name, path, out _);
            }

            if (!scene.IsValid() || !scene.isLoaded)
                return ToolDispatchResult.Fail("scene_not_found",
                    (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(path))
                        ? "No active loaded scene."
                        : BuildSceneNotFoundMessage(name, path));

            var detail = ParseDetail(JsonBody.GetString(body, "detail"), DetailLevel.Summary);
            int depth = JsonBody.GetInt(body, "depth", 3);
            if (depth < 0) depth = 0;
            int maxNodes = JsonBody.GetInt(body, "max_nodes", 200);
            if (maxNodes < 1) maxNodes = 1;

            var roots = scene.GetRootGameObjects();
            var counter = new NodeCounter(maxNodes);

            var sb = new StringBuilder(512);
            sb.Append("{\"status\":\"ok\",\"scene\":{\"name\":\"")
              .Append(TypedTargets.Esc(scene.name))
              .Append("\",\"path\":\"").Append(TypedTargets.Esc(scene.path))
              .Append("\",\"isDirty\":").Append(scene.isDirty ? "true" : "false")
              .Append(",\"isLoaded\":").Append(scene.isLoaded ? "true" : "false")
              .Append(",\"rootCount\":").Append(scene.rootCount)
              .Append(",\"buildIndex\":").Append(scene.buildIndex)
              .Append(",\"detail\":\"").Append(DetailToWire(detail)).Append("\",")
              .Append("\"depth\":").Append(depth)
              .Append(",\"maxNodes\":").Append(maxNodes)
              .Append(",\"roots\":[");

            for (int i = 0; i < roots.Length; i++)
            {
                if (i > 0) sb.Append(',');
                SerializeNode(sb, roots[i], detail, depth, 0, counter);
            }
            sb.Append("],\"moreHidden\":[");
            for (int i = 0; i < counter.MoreHidden.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":\"").Append(TypedTargets.Esc(counter.MoreHidden[i].Name));
                sb.Append("\",\"count\":").Append(counter.MoreHidden[i].Count).Append('}');
            }
            sb.Append("],\"truncated\":").Append(counter.Truncated).Append('}');
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --------------------------- focus ---------------------------------

        // Focus frames a GameObject in the SceneView and optionally sets the
        // view axis. Mutating (it moves the editor camera + sets Selection).
        public static ToolDispatchResult Focus(string body)
        {
            var instanceId = JsonBody.GetLongFlexible(body, "instance_id", 0);
            var path = JsonBody.GetString(body, "path");
            var name = JsonBody.GetString(body, "name");
            var target = TypedTargets.ResolveGameObject(instanceId, path, name);
            if (target == null)
                return ToolDispatchResult.Fail("gameobject_not_found",
                    $"GameObject not found (instance_id={instanceId}, path='{path}', name='{name}').");

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                try
                {
                    sceneView = EditorWindow.GetWindow<SceneView>();
                }
                catch
                {
                    sceneView = null;
                }
            }
            if (sceneView == null)
                return ToolDispatchResult.Fail("focus_failed",
                    "No SceneView available to focus. Open the Scene window in the editor and retry.");

            var bounds = CalculateFocusBounds(target);
            var focusPoint = bounds.center;
            float focusSize = JsonBody.GetFloat(body, "size", Mathf.Max(bounds.extents.magnitude * 2f, 1f));

            var axisWire = JsonBody.GetString(body, "axis");
            var axis = ParseAxis(axisWire);

            try
            {
                sceneView.Show();
                sceneView.Focus();
                Selection.activeGameObject = target;

                if (axis.HasValue)
                {
                    var rotation = Quaternion.LookRotation(-axis.Value, SelectUpVector(axis.Value));
                    sceneView.LookAtDirect(focusPoint, rotation, focusSize);
                }
                else
                {
                    sceneView.LookAtDirect(focusPoint, sceneView.rotation, focusSize);
                }
                sceneView.Repaint();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("focus_failed", e.Message);
            }

            var sb = new StringBuilder(160);
            sb.Append("{\"status\":\"ok\",\"focused\":true,");
            sb.Append("\"instanceId\":").Append(InstanceId.ToJson(target));
            sb.Append(",\"name\":\"").Append(TypedTargets.Esc(target.name)).Append("\"");
            sb.Append(",\"path\":\"").Append(TypedTargets.Esc(TypedTargets.HierarchyPath(target))).Append("\"");
            sb.Append(",\"pivot\":");
            AppendVector(sb, sceneView.pivot);
            sb.Append(",\"cameraPosition\":");
            AppendVector(sb, sceneView.camera.transform.position);
            sb.Append(",\"cameraRotationEuler\":");
            AppendVector(sb, sceneView.camera.transform.rotation.eulerAngles);
            sb.Append(",\"size\":").Append(sceneView.size.ToString("R", CultureInfo.InvariantCulture));
            if (axis.HasValue)
            {
                sb.Append(",\"axis\":");
                AppendVector(sb, axis.Value.normalized);
            }
            else if (!string.IsNullOrEmpty(axisWire))
            {
                sb.Append(",\"axisRequested\":\"").Append(TypedTargets.Esc(axisWire)).Append("\",\"axisApplied\":false");
            }
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // SceneView pose-level read. Distinct from Focus(): this reports the
        // current editor camera pose/pivot so the agent can reason about what
        // the human is looking at before taking action.
        public static ToolDispatchResult SceneViewGetCamera(string body)
        {
            var sceneView = ResolveSceneView();
            if (sceneView == null)
                return ToolDispatchResult.Fail("sceneview_unavailable",
                    "No SceneView available. Open the Scene window in the editor and retry.");

            return ToolDispatchResult.Ok(BuildSceneViewCameraEnvelope(sceneView, moved: false));
        }

        // SceneView pose-level mutation. This is an editor-UI state mutation
        // (camera/window move), not a project-asset write.
        public static ToolDispatchResult SceneViewSetCamera(string body)
        {
            if (!TryParseVector3Object(body, "position", out var position))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'position' is required and must be an object with numeric x/y/z fields.");

            var sceneView = ResolveSceneView();
            if (sceneView == null)
                return ToolDispatchResult.Fail("sceneview_unavailable",
                    "No SceneView available. Open the Scene window in the editor and retry.");

            var rotation = sceneView.rotation;
            if (TryParseVector3Object(body, "rotation", out var rotationEuler))
                rotation = Quaternion.Euler(rotationEuler);

            // Keep the current zoom unless the caller sets an explicit size.
            float size = sceneView.size;
            var rawSize = JsonBody.GetRawValue(body, "size");
            if (!string.IsNullOrEmpty(rawSize))
            {
                var requested = JsonBody.GetFloat(body, "size", size);
                if (requested > 0f)
                    size = requested;
            }

            var rawOrtho = JsonBody.GetRawValue(body, "orthographic");
            if (!string.IsNullOrEmpty(rawOrtho))
                sceneView.orthographic = JsonBody.GetBool(body, "orthographic", sceneView.orthographic);

            // SceneView.LookAtDirect is pivot-centric; derive pivot from desired
            // camera position + forward vector + distance(size).
            var pivot = position + (rotation * Vector3.forward * size);

            try
            {
                sceneView.Show();
                sceneView.Focus();
                sceneView.LookAtDirect(pivot, rotation, size);
                sceneView.Repaint();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("sceneview_set_camera_failed", e.Message);
            }

            return ToolDispatchResult.Ok(BuildSceneViewCameraEnvelope(sceneView, moved: true));
        }

        // ----------------------------- helpers -----------------------------

        enum DetailLevel { Summary, Normal, Verbose }

        private static string DetailToWire(DetailLevel d) => d switch
        {
            DetailLevel.Summary => "summary",
            DetailLevel.Normal => "normal",
            DetailLevel.Verbose => "verbose",
            _ => "summary",
        };

        private static DetailLevel ParseDetail(string raw, DetailLevel fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return raw.ToLowerInvariant() switch
            {
                "summary" => DetailLevel.Summary,
                "normal" => DetailLevel.Normal,
                "verbose" => DetailLevel.Verbose,
                _ => fallback,
            };
        }

        // Class, not struct: SerializeNode mutates Emitted/Truncated/MoreHidden
        // across the recursive walk, and the caller reads those fields after
        // the root loop. A struct passed by value would silently lose every
        // mutation.
        class NodeCounter
        {
            public int Emitted;
            public readonly int Max;
            public int Truncated;
            public readonly List<MoreHiddenEntry> MoreHidden;

            public NodeCounter(int max)
            {
                Emitted = 0;
                Max = max;
                Truncated = 0;
                MoreHidden = new List<MoreHiddenEntry>();
            }

            public bool CanEmit => Emitted < Max;
        }

        struct MoreHiddenEntry
        {
            public string Name;
            public int Count;
        }

        // Serialize one node + (when detail allows) its descendants up to depth.
        // Nodes past max_nodes are counted in counter.Truncated and not emitted.
        private static void SerializeNode(StringBuilder sb, GameObject go, DetailLevel detail,
            int maxDepth, int depth, NodeCounter counter)
        {
            if (!counter.CanEmit)
            {
                counter.Truncated++;
                return;
            }
            counter.Emitted++;

            sb.Append("{\"name\":\"").Append(TypedTargets.Esc(go.name)).Append("\"");
            if (detail == DetailLevel.Verbose)
            {
                sb.Append(",\"instanceId\":").Append(InstanceId.ToJson(go));
            }
            if (detail >= DetailLevel.Normal)
            {
                sb.Append(",\"active\":").Append(go.activeInHierarchy ? "true" : "false");
                sb.Append(",\"tag\":\"").Append(TypedTargets.Esc(go.tag)).Append("\"");
                sb.Append(",\"layer\":").Append(go.layer);
            }
            sb.Append(",\"childCount\":").Append(go.transform.childCount);

            // Components: always emit (cheap, drives component_add/get chaining).
            // Missing-script slots show as "<missing>" so agents can spot them.
            var comps = go.GetComponents<Component>();
            sb.Append(",\"components\":[");
            for (int i = 0; i < comps.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var c = comps[i];
                sb.Append("{\"name\":\"").Append(TypedTargets.Esc(c == null ? "<missing>" : c.GetType().Name));
                sb.Append("\",\"fullName\":\"").Append(TypedTargets.Esc(c == null ? "" : c.GetType().FullName));
                sb.Append("\",\"instanceId\":").Append(InstanceId.ToJson(c)).Append('}');
            }
            sb.Append(']');

            if (detail == DetailLevel.Verbose)
            {
                sb.Append(",\"transform\":");
                AppendTransform(sb, go.transform);
            }

            // Children: only walked in normal/verbose. Summary stops at roots.
            if (detail >= DetailLevel.Normal && depth < maxDepth && go.transform.childCount > 0)
            {
                sb.Append(",\"children\":[");
                int emittedHere = 0;
                int hiddenHere = 0;
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    if (!counter.CanEmit)
                    {
                        hiddenHere += go.transform.childCount - i;
                        break;
                    }
                    if (emittedHere > 0) sb.Append(',');
                    SerializeNode(sb, go.transform.GetChild(i).gameObject, detail, maxDepth, depth + 1, counter);
                    emittedHere++;
                }
                sb.Append(']');
                if (hiddenHere > 0)
                {
                    counter.MoreHidden.Add(new MoreHiddenEntry { Name = go.name, Count = hiddenHere });
                }
            }
            else if (detail >= DetailLevel.Normal && depth >= maxDepth && go.transform.childCount > 0)
            {
                // Depth cap reached but this node has children — record them as
                // hidden under this name so the agent knows to bump `depth`.
                counter.MoreHidden.Add(new MoreHiddenEntry { Name = go.name, Count = go.transform.childCount });
            }

            sb.Append('}');
        }

        private static void AppendTransform(StringBuilder sb, Transform t)
        {
            sb.Append("{\"position\":");
            AppendVector(sb, t.position);
            sb.Append(",\"rotation\":");
            AppendVector(sb, t.eulerAngles);
            sb.Append(",\"localScale\":");
            AppendVector(sb, t.localScale);
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

        private static SceneView ResolveSceneView()
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                try
                {
                    sceneView = EditorWindow.GetWindow<SceneView>();
                }
                catch
                {
                    sceneView = null;
                }
            }

            return sceneView;
        }

        private static string BuildSceneViewCameraEnvelope(SceneView sceneView, bool moved)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"status\":\"ok\"");
            sb.Append(",\"windowMoved\":").Append(moved ? "true" : "false");
            sb.Append(",\"camera\":{");
            sb.Append("\"position\":");
            AppendVector(sb, sceneView.camera.transform.position);
            sb.Append(",\"rotationEuler\":");
            AppendVector(sb, sceneView.camera.transform.rotation.eulerAngles);
            sb.Append(",\"pivot\":");
            AppendVector(sb, sceneView.pivot);
            sb.Append(",\"orthographic\":").Append(sceneView.orthographic ? "true" : "false");
            sb.Append(",\"size\":").Append(sceneView.size.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"fieldOfView\":").Append(sceneView.camera.fieldOfView.ToString("R", CultureInfo.InvariantCulture));
            sb.Append("}}");
            return sb.ToString();
        }

        private static bool TryParseVector3Object(string body, string key, out Vector3 value)
        {
            value = default;
            var raw = JsonBody.GetRawValue(body, key);
            if (string.IsNullOrWhiteSpace(raw) || !raw.TrimStart().StartsWith("{"))
                return false;

            var xRaw = JsonBody.GetRawValue(raw, "x");
            var yRaw = JsonBody.GetRawValue(raw, "y");
            var zRaw = JsonBody.GetRawValue(raw, "z");
            if (xRaw == null || yRaw == null || zRaw == null)
                return false;

            value = new Vector3(
                JsonBody.GetFloat(raw, "x", 0f),
                JsonBody.GetFloat(raw, "y", 0f),
                JsonBody.GetFloat(raw, "z", 0f));
            return true;
        }

        private static Bounds CalculateFocusBounds(GameObject target)
        {
            var hasBounds = false;
            var bounds = new Bounds(target.transform.position, Vector3.one);

            foreach (var renderer in target.GetComponentsInChildren<Renderer>())
            {
                if (!hasBounds) { bounds = renderer.bounds; hasBounds = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            foreach (var collider in target.GetComponentsInChildren<Collider>())
            {
                if (!hasBounds) { bounds = collider.bounds; hasBounds = true; }
                else bounds.Encapsulate(collider.bounds);
            }

            if (!hasBounds)
                bounds = new Bounds(target.transform.position, Vector3.one);
            return bounds;
        }

        private static Vector3? ParseAxis(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            return raw.ToLowerInvariant() switch
            {
                "top" => new Vector3(0, -1, 0),    // look straight down (−Y forward)
                "bottom" => new Vector3(0, 1, 0),
                "front" => new Vector3(0, 0, -1),
                "back" => new Vector3(0, 0, 1),
                "left" => new Vector3(1, 0, 0),
                "right" => new Vector3(-1, 0, 0),
                _ => null,
            };
        }

        private static Vector3 SelectUpVector(Vector3 forward)
        {
            if (Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.98f)
                return Vector3.forward;
            return Vector3.up;
        }

        // Normalize a caller-provided scene path to an Assets/-rooted .unity
        // path. Returns null when the path does not end with '.unity'.
        private static string NormalizeScenePath(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var normalized = raw.Replace('\\', '/').Trim();
            if (!normalized.EndsWith(".unity")) return null;
            // Accept both "Assets/..." (already project-relative) and bare
            // "Scenes/Foo.unity" (relative to Assets/). We do not rewrite
            // absolute paths — those would let a caller reach outside Assets/.
            return normalized;
        }

        private static NewSceneSetup ParseSetup(string raw, NewSceneSetup fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return raw.ToLowerInvariant() switch
            {
                "empty" => NewSceneSetup.EmptyScene,
                "default" => NewSceneSetup.DefaultGameObjects,
                _ => fallback,
            };
        }

        private static NewSceneMode ParseNewSceneMode(string raw, NewSceneMode fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return raw.ToLowerInvariant() switch
            {
                "single" => NewSceneMode.Single,
                "additive" => NewSceneMode.Additive,
                _ => fallback,
            };
        }

        private static OpenSceneMode ParseOpenSceneMode(string raw, OpenSceneMode fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return raw.ToLowerInvariant() switch
            {
                "single" => OpenSceneMode.Single,
                "additive" => OpenSceneMode.Additive,
                _ => fallback,
            };
        }

        private static Scene ResolveOpenedByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return new Scene();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && s.name == name) return s;
            }
            return new Scene();
        }

        // Resolve an opened scene by its asset path. `rawPath` is normalized
        // the same way NormalizeScenePath normalizes create/open paths
        // (backslashes → '/', trim). An empty/untitled scene has path == "",
        // so it will never match a caller-supplied path — that is intentional:
        // path identity is asset-centric and only matches asset-backed scenes.
        // Comparison is case-insensitive on the normalized path to tolerate
        // capitalization drift across platforms.
        private static Scene ResolveOpenedByPath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return new Scene();
            var normalized = rawPath.Replace('\\', '/').Trim();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded || string.IsNullOrEmpty(s.path)) continue;
                var scenePath = s.path.Replace('\\', '/').Trim();
                if (string.Equals(scenePath, normalized, System.StringComparison.OrdinalIgnoreCase))
                    return s;
            }
            return new Scene();
        }

        // Unified identity resolver for the name-only mutators (set_active,
        // unload). Precedence: `path` wins when supplied and resolves to an
        // opened scene; `name` is the fallback. When both are supplied and
        // `path` resolves, the name is ignored (path is the authoritative
        // identity for an asset-centric MCP). outResolvedBy reports which key
        // resolved so callers can build precise error messages / subjects.
        private enum SceneIdentity { None, ByPath, ByName, Active }

        private static Scene ResolveOpenedForMutator(string name, string path, out SceneIdentity resolvedBy)
        {
            resolvedBy = SceneIdentity.None;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var byPath = ResolveOpenedByPath(path);
                if (byPath.IsValid() && byPath.isLoaded)
                {
                    resolvedBy = SceneIdentity.ByPath;
                    return byPath;
                }
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                var byName = ResolveOpenedByName(name);
                if (byName.IsValid() && byName.isLoaded)
                {
                    resolvedBy = SceneIdentity.ByName;
                    return byName;
                }
            }
            return new Scene();
        }

        // After scene_create saves the asset, the in-memory scene name can lag
        // the filename stem (staying "Untitled" or the pre-save name). Re-open
        // the saved asset so the opened-scene stack reflects the saved name +
        // path; subsequent name-only lookups then resolve reliably. We avoid
        // re-opening when the scene is already correctly named+pathed (common
        // case) and when the mode is Single (the scene is already the only
        // opened scene and SaveScene has named it). Returns the (possibly
        // refreshed) scene handle.
        //
        // Note: Scene structs are value-type snapshots captured at resolution
        // time; after SaveScene the captured `scene` may report a stale name,
        // so we re-resolve from the opened stack by path.
        private static Scene SyncCreatedSceneName(Scene scene, string assetPath)
        {
            var refreshed = ResolveOpenedByPath(assetPath);
            if (refreshed.IsValid() && refreshed.isLoaded)
                return refreshed;
            // Fallback: the scene is in the stack but its path field hasn't
            // populated yet (rare). Return the original handle.
            return scene;
        }

        // Build a precise scene_not_found message that names whichever
        // identity key(s) the caller supplied, and points at list_opened for
        // discovery. Precedence note: when both name+path are supplied and the
        // path does not resolve, we report the path (the authoritative key)
        // and mention the name so the agent can tell which key missed.
        private static string BuildSceneNotFoundMessage(string name, string path)
        {
            var hasName = !string.IsNullOrWhiteSpace(name);
            var hasPath = !string.IsNullOrWhiteSpace(path);
            if (hasPath && hasName)
                return $"No opened scene matching path '{path}' or name '{name}'. " +
                       "Use unity_open_mcp_scene_list_opened to enumerate opened scenes.";
            if (hasPath)
                return $"No opened scene at path '{path}'. " +
                       "Use unity_open_mcp_scene_list_opened to enumerate opened scenes.";
            return $"No opened scene named '{name}'. " +
                   "Use unity_open_mcp_scene_list_opened to enumerate opened scenes.";
        }

        private static int CountOpenedScenes()
        {
            int n = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isLoaded) n++;
            return n;
        }

        private static List<Scene> OpenedScenes()
        {
            var list = new List<Scene>(SceneManager.sceneCount);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) list.Add(s);
            }
            return list;
        }

        // Shallow per-scene snapshot shared by every lifecycle op response.
        private static string BuildSceneShallow(Scene scene)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"name\":\"").Append(TypedTargets.Esc(scene.name));
            sb.Append("\",\"path\":\"").Append(TypedTargets.Esc(scene.path));
            sb.Append("\",\"isDirty\":").Append(scene.isDirty ? "true" : "false");
            sb.Append(",\"isLoaded\":").Append(scene.isLoaded ? "true" : "false");
            sb.Append(",\"rootCount\":").Append(scene.rootCount);
            sb.Append(",\"buildIndex\":").Append(scene.buildIndex);
            sb.Append(",\"isActive\":").Append(EditorSceneManager.GetActiveScene() == scene ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        // Envelope listing all opened scenes — returned by open/unload/set_active
        // and list_opened so the agent can confirm post-op state in one call.
        private static string BuildOpenedScenesEnvelope(string action, string subject)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"status\":\"ok\",\"action\":\"").Append(TypedTargets.Esc(action)).Append("\"");
            if (subject != null)
            {
                sb.Append(",\"subject\":\"").Append(TypedTargets.Esc(subject)).Append("\"");
            }
            sb.Append(",\"activeScene\":\"")
              .Append(TypedTargets.Esc(EditorSceneManager.GetActiveScene().name));
            sb.Append("\",\"scenes\":[");
            var opened = OpenedScenes();
            for (int i = 0; i < opened.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BuildSceneShallow(opened[i]));
            }
            sb.Append("],\"openedSceneCount\":").Append(opened.Count).Append('}');
            return sb.ToString();
        }
    }
}
