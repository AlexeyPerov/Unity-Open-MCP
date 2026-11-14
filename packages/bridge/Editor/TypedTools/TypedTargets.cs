using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.TypedTools
{
    /// <summary>The role a looked-up GameObject plays in the call; it picks
    /// the not-found code (gameobject_not_found / parent_not_found).</summary>
    public enum TargetRole
    {
        Host,
        Parent,
    }

    // The one GameObject resolver of the typed surface (gameobject_*,
    // component_*, prefab_*, selection, scene focus, object references and
    // every embedded domain). Addressing priority: instance_id > path > name,
    // the same vocabulary as spatial-query.ts / ManageSpatialQueryTool.cs.
    //
    // Containers. A GameObject lives in a container: a loaded scene, or the
    // open prefab stage (the prefab's contents in a preview scene, which
    // SceneManager does not list). A path inside the stage is relative to the
    // prefab root ("Panel/Child"); the wrapper Unity adds around a UI prefab
    // ("Canvas (Environment)/Panel/Child") is accepted too. Returned paths are
    // canonical: relative to the prefab root for stage objects, from the
    // scene root otherwise.
    //
    // Which container a path resolves in. Every container is searched. A path
    // that matches in one resolves there. When it matches in several, the
    // candidates narrow in this order and the call is refused with
    // ambiguous_path only if more than one is left:
    //   1. the container the call names (scene_path / scene_name — a loaded
    //      scene, or the open stage by its prefab asset path); it is binding;
    //   2. containers whose asset path (or a folder holding it) is in
    //      paths_hint;
    //   3. the open prefab stage — the editing context the Hierarchy shows;
    //   4. the active scene.
    // Within one container the first match in hierarchy order wins, as
    // before. A name lookup narrows the same way.
    //
    // Instance ids resolve through the Unity API (InstanceId.ToObject), not by
    // walking hierarchies, and only to a GameObject in a loaded scene or in
    // the open stage's prefab contents. An object inside an asset on disk
    // (persistent_object), an object of another preview scene or of the
    // stage's environment (preview_scene_object), and a Component or any
    // other non-GameObject (not_a_gameobject) are refused, never edited.
    public static class TypedTargets
    {
        /// <summary>Why the most recent lookup on this (main) thread failed;
        /// null after a success. Tools that only get a GameObject-or-null
        /// back (the embedded domains) read it right after a null.</summary>
        public static ResolveFailure LastFailure { get; private set; }

        // Each tool call starts without a failure from an earlier call.
        internal static void ClearLastFailure() => LastFailure = null;

        // ------------------------------------------------------------------
        // GameObject-or-null entry points. They read the container request
        // of the call running now (TargetRequest.Ambient).
        // ------------------------------------------------------------------

        public static GameObject ResolveGameObject(long instanceId, string path, string name)
            => Resolve(instanceId, path, name, TargetRequest.Ambient).GameObject;

        public static GameObject FindByInstanceId(long instanceId)
            => Resolve(instanceId, null, null, TargetRequest.Ambient).GameObject;

        public static GameObject FindByPath(string path)
            => Resolve(0, path, null, TargetRequest.Ambient).GameObject;

        public static GameObject FindByName(string name)
            => Resolve(0, null, name, TargetRequest.Ambient).GameObject;

        /// <summary>The embedded-domain lookup: <see cref="ResolveGameObject"/>,
        /// plus a one-segment path that names no root also matching a nested
        /// GameObject of that name (what those tools accepted before they
        /// shared this resolver).</summary>
        public static GameObject ResolveLenient(long instanceId, string path, string name)
        {
            var request = TargetRequest.Ambient;
            var r = Resolve(instanceId, path, name, request);
            if (r.Ok || !r.Failure.IsNotFound) return r.GameObject;
            var single = path?.Trim('/');
            if (!string.IsNullOrEmpty(single) && single.IndexOf('/') < 0)
            {
                var byName = Resolve(0, null, single, request);
                if (byName.Ok || !byName.Failure.IsNotFound) return byName.GameObject;
                LastFailure = r.Failure;
            }
            return null;
        }

        /// <summary>The refusal body for the last failed lookup when it was
        /// more than "not found" (ambiguous_path, a refused instance id, an
        /// unknown scene_path); null otherwise, so a domain keeps its own
        /// not-found error.</summary>
        public static string RefusalJson()
        {
            var f = LastFailure;
            return f == null || f.IsNotFound ? null : f.ToRefusalJson();
        }

        // ------------------------------------------------------------------
        // Structured lookup.
        // ------------------------------------------------------------------

        /// <summary>Resolve by the body's selectors' values, honouring the
        /// container request the body (or the enclosing call) carries.</summary>
        public static TargetResolution Resolve(long instanceId, string path, string name, string body,
            TargetRole role = TargetRole.Host)
            => Resolve(instanceId, path, name, TargetRequest.For(body), role);

        public static TargetResolution Resolve(long instanceId, string path, string name, TargetRequest request,
            TargetRole role = TargetRole.Host)
        {
            request = request ?? TargetRequest.None;
            var named = TargetContainers.Named(request, out var namedFailure);
            if (namedFailure != null) return Remember(TargetResolution.Fail(namedFailure));

            var elsewhere = new List<TargetContainer>();
            if (instanceId != 0)
            {
                var byId = ResolveId(instanceId, role);
                // An id that no longer resolves falls back to the path / name
                // (ids change on a domain reload); a refused id does not.
                if (byId.Ok || !byId.Failure.IsNotFound) return Remember(byId);
            }
            if (!string.IsNullOrEmpty(path))
            {
                var byPath = ResolveAddress(path, false, named, request, role, elsewhere);
                if (byPath.Ok || !byPath.Failure.IsNotFound) return Remember(byPath);
            }
            if (!string.IsNullOrEmpty(name))
            {
                var byName = ResolveAddress(name, true, named, request, role, elsewhere);
                if (byName.Ok || !byName.Failure.IsNotFound) return Remember(byName);
            }
            return Remember(TargetResolution.Fail(NotFound(instanceId, path, name, role, named, elsewhere)));
        }

        /// <summary>The container a GameObject lives in, or null when a typed
        /// tool must not edit it (asset on disk, other preview scene).</summary>
        public static TargetContainer ContainerOf(GameObject go) => TargetContainers.Of(go);

        /// <summary>The container JSON of a GameObject ("null" when none).</summary>
        public static string ContainerJson(GameObject go) => TargetContainers.Of(go)?.ToJson() ?? "null";

        /// <summary>Appends ,"container":{…} for the GameObject (nothing when
        /// it has no container).</summary>
        public static void AppendContainerMember(StringBuilder sb, GameObject go)
        {
            var c = TargetContainers.Of(go);
            if (c == null) return;
            sb.Append(",\"container\":");
            c.AppendJson(sb);
        }

        /// <summary>True for the root of the open stage's prefab contents: it
        /// cannot be destroyed, duplicated or detached inside the stage (a
        /// prefab has exactly one root).</summary>
        public static bool IsStageRoot(GameObject go)
        {
            var stage = TargetContainers.OpenStage();
            return stage != null && go != null && go == stage.prefabContentsRoot;
        }

        // ------------------------------------------------------------------
        // Where a new root GameObject goes.
        // ------------------------------------------------------------------

        /// <summary>
        /// The container for a GameObject created without a parent. A named
        /// container (scene_path / scene_name) wins. While a prefab stage is
        /// open the new object goes under the prefab root (as Unity's own
        /// Create menu does in Prefab Mode) unless paths_hint names a loaded
        /// scene and not the prefab — then, as without a stage, the active
        /// scene.
        /// </summary>
        public static TargetContainer CreationContainer(TargetRequest request, out ResolveFailure failure)
        {
            request = request ?? TargetRequest.None;
            var named = TargetContainers.Named(request, out failure);
            if (failure != null) return null;
            if (named != null) return named;

            var stage = TargetContainers.Stage();
            if (stage != null)
            {
                var hints = request.PathsHint;
                bool hintsScene = false;
                if (!stage.IsNamedBy(hints))
                    foreach (var c in TargetContainers.All())
                        if (c.Kind == TargetContainerKind.Scene && c.IsNamedBy(hints)) { hintsScene = true; break; }
                if (!hintsScene) return stage;
            }
            var active = SceneManager.GetActiveScene();
            return active.IsValid() ? TargetContainer.ForScene(active) : null;
        }

        /// <summary>Moves a GameObject created without a parent into its
        /// container: into the scene, or under the prefab root of the stage.
        /// Records the container as touched by this call.</summary>
        public static void PlaceInContainer(GameObject go, TargetContainer container)
        {
            if (go == null || container == null) return;
            if (container.Kind == TargetContainerKind.PrefabStage)
            {
                if (go.scene != container.Scene) SceneManager.MoveGameObjectToScene(go, container.Scene);
                go.transform.SetParent(container.PrefabRoot.transform, false);
            }
            else if (go.transform.parent == null && go.scene != container.Scene)
            {
                SceneManager.MoveGameObjectToScene(go, container.Scene);
            }
            TargetScope.Touch(container);
        }

        // ------------------------------------------------------------------
        // Paths.
        // ------------------------------------------------------------------

        /// <summary>The canonical hierarchy path of a GameObject: relative to
        /// the prefab root for an object of the open stage (no environment
        /// wrapper), from the scene root otherwise. Feed it back as
        /// game_object_path / parent_path.</summary>
        public static string HierarchyPath(GameObject go)
        {
            if (go == null) return "";
            Transform stop = null;
            var stage = TargetContainers.OpenStage();
            if (stage != null && go.scene == stage.scene)
            {
                var root = stage.prefabContentsRoot.transform;
                if (TargetContainers.IsSelfOrDescendant(go.transform, root)) stop = root.parent;
            }
            var sb = new StringBuilder(64);
            sb.Append(go.name);
            for (var t = go.transform.parent; t != null && t != stop; t = t.parent)
            {
                sb.Insert(0, '/');
                sb.Insert(0, t.name);
            }
            return sb.ToString();
        }

        // Escape a string for inline JSON.
        public static string Esc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
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
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Internals.
        // ------------------------------------------------------------------

        private static TargetResolution Remember(TargetResolution r)
        {
            LastFailure = r.Failure;
            if (r.Ok) TargetScope.Touch(r.Container);
            return r;
        }

        private static string NotFoundCode(TargetRole role) =>
            role == TargetRole.Parent ? ResolveFailure.ParentNotFound : ResolveFailure.GameObjectNotFound;

        private static TargetResolution ResolveId(long id, TargetRole role)
        {
            var obj = InstanceId.ToObject(id);
            if (obj == null)
                return TargetResolution.Fail(new ResolveFailure(NotFoundCode(role), "instance_id not found"));

            if (obj is Component comp)
            {
                var host = comp.gameObject;
                return TargetResolution.Fail(new ResolveFailure(ResolveFailure.NotAGameObject,
                    $"instance_id {id} is a {comp.GetType().Name} component on '{host.name}', not a GameObject. " +
                    $"Pass the GameObject's instance_id ({InstanceId.Of(host)}), or name the component with " +
                    "component_instance_id where the tool takes one.",
                    "{\"objectType\":" + BridgeJson.EscapeString(comp.GetType().FullName) +
                    ",\"gameObjectInstanceId\":" + InstanceId.ToJson(host) + "}"));
            }
            if (!(obj is GameObject go))
                return TargetResolution.Fail(new ResolveFailure(ResolveFailure.NotAGameObject,
                    $"instance_id {id} is a {obj.GetType().FullName} ('{obj.name}'), not a GameObject.",
                    "{\"objectType\":" + BridgeJson.EscapeString(obj.GetType().FullName) + "}"));

            var container = TargetContainers.Of(go, out var reason);
            if (container != null) return TargetResolution.Found(go, container);

            switch (reason)
            {
                case "persistent":
                {
                    var assetPath = AssetDatabase.GetAssetPath(go);
                    bool isPrefab = assetPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase);
                    var steps = isPrefab
                        ? new[]
                        {
                            "Open the prefab with unity_open_mcp_prefab_open {\"asset_path\":\"" + assetPath +
                            "\"}; its result names the root's path and instance id. Then address the object by " +
                            "its path from the prefab root."
                        }
                        : null;
                    return TargetResolution.Fail(new ResolveFailure(ResolveFailure.PersistentObject,
                        $"instance_id {id} is '{go.name}' inside the asset '{assetPath}' on disk, not an object of a " +
                        "loaded scene or of the open prefab stage. Typed GameObject tools do not edit assets " +
                        "through their ids" + (isPrefab ? "; open the prefab in a stage and edit it there." : "."),
                        "{\"assetPath\":" + BridgeJson.EscapeString(assetPath) + "}", steps));
                }
                case "stage_environment":
                {
                    var stage = TargetContainers.OpenStage();
                    var rootPath = stage != null ? HierarchyPath(stage.prefabContentsRoot) : "";
                    return TargetResolution.Fail(new ResolveFailure(ResolveFailure.PreviewSceneObject,
                        $"instance_id {id} ('{go.name}') belongs to the environment Unity adds around the prefab in " +
                        "its stage, not to the prefab; edits there are never saved. Address the prefab root '" +
                        rootPath + "' or an object under it."));
                }
                default:
                    return TargetResolution.Fail(new ResolveFailure(ResolveFailure.PreviewSceneObject,
                        $"instance_id {id} ('{go.name}') lives in a preview scene ('{go.scene.name}') that is neither " +
                        "a loaded scene nor the open prefab stage, so typed tools do not edit it."));
            }
        }

        private static TargetResolution ResolveAddress(string address, bool byName, TargetContainer named,
            TargetRequest request, TargetRole role, List<TargetContainer> elsewhere)
        {
            string[] segments = null;
            if (!byName)
            {
                var trimmed = address.Trim('/');
                if (trimmed.Length == 0)
                    return TargetResolution.Fail(new ResolveFailure(NotFoundCode(role), "empty path"));
                segments = trimmed.Split('/');
            }

            var matches = new List<TargetResolution>();
            foreach (var c in TargetContainers.All())
            {
                var go = byName ? FindNameIn(c, address) : FindPathIn(c, segments);
                if (go != null) matches.Add(TargetResolution.Found(go, c));
            }

            if (named != null)
            {
                foreach (var m in matches)
                    if (m.Container.Equals(named)) return m;
                foreach (var m in matches)
                    if (!elsewhere.Contains(m.Container)) elsewhere.Add(m.Container);
                return TargetResolution.Fail(new ResolveFailure(NotFoundCode(role), "not in the named container"));
            }

            if (matches.Count == 0)
                return TargetResolution.Fail(new ResolveFailure(NotFoundCode(role), "no match"));
            if (matches.Count == 1) return matches[0];

            var narrowed = Narrow(matches, request);
            if (narrowed.Count == 1) return narrowed[0];
            return TargetResolution.Fail(Ambiguous(address, byName, narrowed));
        }

        // Narrowing order for an address that matched in several containers:
        // hinted containers, then the open stage, then the active scene.
        private static List<TargetResolution> Narrow(List<TargetResolution> matches, TargetRequest request)
        {
            var set = matches;
            var hinted = set.FindAll(m => m.Container.IsNamedBy(request.PathsHint));
            if (hinted.Count > 0) set = hinted;
            if (set.Count == 1) return set;

            var staged = set.FindAll(m => m.Container.Kind == TargetContainerKind.PrefabStage);
            if (staged.Count > 0) set = staged;
            if (set.Count == 1) return set;

            var active = SceneManager.GetActiveScene();
            var inActive = set.FindAll(m => m.Container.Kind == TargetContainerKind.Scene && m.Container.Scene == active);
            if (inActive.Count > 0) set = inActive;
            return set;
        }

        private static ResolveFailure Ambiguous(string address, bool byName, List<TargetResolution> candidates)
        {
            var listed = new StringBuilder(128);
            var detail = new StringBuilder(256);
            detail.Append("{\"candidates\":[");
            var steps = new List<string>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (i > 0) { listed.Append(", "); detail.Append(','); }
                listed.Append(c.Container.Describe());
                detail.Append("{\"container\":");
                c.Container.AppendJson(detail);
                detail.Append(",\"path\":").Append(BridgeJson.EscapeString(HierarchyPath(c.GameObject)));
                detail.Append(",\"instanceId\":").Append(InstanceId.ToJson(c.GameObject)).Append('}');
                if (!string.IsNullOrEmpty(c.Container.AssetPath))
                    steps.Add($"Retry with scene_path: \"{c.Container.AssetPath}\" to use the one in {c.Container.Describe()}.");
            }
            detail.Append("]}");
            steps.Add("Or, where the tool takes an instance id for this object, pass the candidate's instanceId.");
            return new ResolveFailure(ResolveFailure.AmbiguousPath,
                $"GameObject {(byName ? "name" : "path")} '{address}' matches in {candidates.Count} containers: " +
                listed + ". Name the one to use with scene_path (a loaded scene's path, or the open prefab " +
                "stage's prefab asset path), or put its path in paths_hint.",
                detail.ToString(), steps.ToArray());
        }

        private static ResolveFailure NotFound(long instanceId, string path, string name, TargetRole role,
            TargetContainer named, List<TargetContainer> elsewhere)
        {
            var sb = new StringBuilder(160);
            if (role == TargetRole.Parent)
            {
                sb.Append("Parent GameObject not found");
                if (instanceId != 0) sb.Append(" (parent_instance_id=").Append(instanceId).Append(')');
                else if (!string.IsNullOrEmpty(path)) sb.Append(" at path '").Append(path).Append('\'');
                else if (!string.IsNullOrEmpty(name)) sb.Append(" (name='").Append(name).Append("')");
                sb.Append('.');
            }
            else
            {
                sb.Append("GameObject not found (instance_id=").Append(instanceId)
                  .Append(", path='").Append(path).Append("', name='").Append(name).Append("').");
            }

            if (named != null)
            {
                sb.Append(" Looked in ").Append(named.Describe()).Append(", the container scene_path / scene_name names");
                if (elsewhere.Count > 0)
                {
                    sb.Append("; it exists in ");
                    for (int i = 0; i < elsewhere.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(elsewhere[i].Describe());
                    }
                }
                sb.Append('.');
            }
            else
            {
                var all = TargetContainers.All();
                if (all.Count > 1 || (all.Count == 1 && all[0].Kind == TargetContainerKind.PrefabStage))
                {
                    sb.Append(" Searched ");
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(all[i].Describe());
                    }
                    sb.Append('.');
                    var stage = TargetContainers.OpenStage();
                    if (stage != null)
                        sb.Append(" Paths in the prefab stage start at the prefab root '")
                          .Append(stage.prefabContentsRoot.name).Append("'.");
                }
            }
            return new ResolveFailure(NotFoundCode(role), sb.ToString());
        }

        // A path inside a container. A scene path starts at a scene root. A
        // stage path starts at the prefab root, or at the root of the stage
        // scene when Unity wrapped the prefab in an environment
        // ("Canvas (Environment)/Panel/Child").
        private static GameObject FindPathIn(TargetContainer c, string[] segments)
        {
            if (c.Kind == TargetContainerKind.PrefabStage)
            {
                var root = c.PrefabRoot;
                if (root == null) return null;
                if (root.name == segments[0])
                {
                    var hit = WalkPath(root.transform, segments, 1);
                    if (hit != null) return hit;
                }
                var chain = new List<string>();
                for (var t = root.transform.parent; t != null; t = t.parent) chain.Insert(0, t.name);
                if (chain.Count == 0 || segments.Length <= chain.Count) return null;
                for (int i = 0; i < chain.Count; i++)
                    if (segments[i] != chain[i]) return null;
                if (segments[chain.Count] != root.name) return null;
                return WalkPath(root.transform, segments, chain.Count + 1);
            }

            foreach (var root in c.Scene.GetRootGameObjects())
            {
                if (root.name != segments[0]) continue;
                var resolved = WalkPath(root.transform, segments, 1);
                if (resolved != null) return resolved;
            }
            return null;
        }

        private static GameObject WalkPath(Transform current, string[] segments, int index)
        {
            if (index >= segments.Length) return current.gameObject;
            foreach (Transform child in current)
            {
                if (child.name == segments[index])
                {
                    var resolved = WalkPath(child, segments, index + 1);
                    if (resolved != null) return resolved;
                }
            }
            return null;
        }

        private static GameObject FindNameIn(TargetContainer c, string name)
        {
            if (c.Kind == TargetContainerKind.PrefabStage)
                return c.PrefabRoot == null ? null : FindInHierarchyByName(c.PrefabRoot, name);
            foreach (var root in c.Scene.GetRootGameObjects())
            {
                var found = FindInHierarchyByName(root, name);
                if (found != null) return found;
            }
            return null;
        }

        private static GameObject FindInHierarchyByName(GameObject go, string name)
        {
            if (go.name == name) return go;
            foreach (Transform child in go.transform)
            {
                var found = FindInHierarchyByName(child.gameObject, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
