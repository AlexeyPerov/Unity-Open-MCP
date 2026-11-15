using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.TypedTools
{
    /// <summary>What holds the GameObjects a typed tool can edit.</summary>
    public enum TargetContainerKind
    {
        /// <summary>A scene loaded in the Hierarchy (one that SceneManager lists).</summary>
        Scene,

        /// <summary>The open prefab stage: the prefab's contents loaded into a
        /// preview scene. Paths are relative to the prefab root.</summary>
        PrefabStage,

        /// <summary>A prefab asset edited in place without a stage: the call
        /// names it with in_prefab, and the bridge loads its contents
        /// (PrefabUtility.LoadPrefabContents), runs the tool, saves the asset
        /// and unloads the contents (see <see cref="PrefabAssetEdits"/>).
        /// Paths are relative to the prefab root.</summary>
        PrefabAsset,
    }

    /// <summary>
    /// One place GameObjects live: a loaded scene, the open prefab stage, or
    /// a prefab asset loaded for an in_prefab edit. Equality is by kind +
    /// scene handle, so two lookups of the same scene compare equal.
    /// </summary>
    public sealed class TargetContainer : IEquatable<TargetContainer>
    {
        private TargetContainer(TargetContainerKind kind, Scene scene, string assetPath, GameObject prefabRoot)
        {
            Kind = kind;
            Scene = scene;
            AssetPath = assetPath ?? "";
            PrefabRoot = prefabRoot;
        }

        public TargetContainerKind Kind { get; }

        /// <summary>The Unity scene holding the objects: the loaded scene, or
        /// the stage's preview scene.</summary>
        public Scene Scene { get; }

        /// <summary>The scene's asset path ("" for an untitled scene), or the
        /// prefab asset path of the stage / the in_prefab asset.</summary>
        public string AssetPath { get; }

        /// <summary>The prefab contents root; null for a scene.</summary>
        public GameObject PrefabRoot { get; }

        /// <summary>True for the prefab stage and a prefab asset: the
        /// container holds one prefab's contents under <see cref="PrefabRoot"/>.</summary>
        public bool IsPrefab => Kind != TargetContainerKind.Scene;

        /// <summary>The wire token: "scene", "prefab_stage" or "prefab_asset".</summary>
        public string KindToken => Kind == TargetContainerKind.PrefabStage ? "prefab_stage"
            : Kind == TargetContainerKind.PrefabAsset ? "prefab_asset" : "scene";

        internal static TargetContainer ForScene(Scene scene) =>
            new TargetContainer(TargetContainerKind.Scene, scene, scene.path, null);

        internal static TargetContainer ForStage(PrefabStage stage) =>
            new TargetContainer(TargetContainerKind.PrefabStage, stage.scene, stage.assetPath, stage.prefabContentsRoot);

        internal static TargetContainer ForPrefabAsset(string assetPath, GameObject contentsRoot) =>
            new TargetContainer(TargetContainerKind.PrefabAsset, contentsRoot.scene, assetPath, contentsRoot);

        /// <summary>Prose name for messages: scene 'Assets/A.unity', the
        /// unsaved scene 'Untitled', the prefab stage of 'Assets/P.prefab',
        /// the prefab asset 'Assets/P.prefab'.</summary>
        public string Describe()
        {
            if (Kind == TargetContainerKind.PrefabStage) return "the prefab stage of '" + AssetPath + "'";
            if (Kind == TargetContainerKind.PrefabAsset) return "the prefab asset '" + AssetPath + "'";
            if (!string.IsNullOrEmpty(AssetPath)) return "scene '" + AssetPath + "'";
            return string.IsNullOrEmpty(Scene.name) ? "the untitled scene" : "the unsaved scene '" + Scene.name + "'";
        }

        /// <summary>{"kind":"scene","path":"Assets/A.unity"} — an untitled
        /// scene adds its "name", since its path is empty.</summary>
        public string ToJson()
        {
            var sb = new StringBuilder(80);
            AppendJson(sb);
            return sb.ToString();
        }

        public void AppendJson(StringBuilder sb)
        {
            sb.Append("{\"kind\":\"").Append(KindToken);
            sb.Append("\",\"path\":\"").Append(TypedTargets.Esc(AssetPath)).Append('"');
            if (Kind == TargetContainerKind.Scene && string.IsNullOrEmpty(AssetPath))
                sb.Append(",\"name\":\"").Append(TypedTargets.Esc(Scene.name)).Append('"');
            sb.Append('}');
        }

        /// <summary>Whether a paths_hint names this container: its asset path
        /// itself, or a folder that contains it.</summary>
        internal bool IsNamedBy(IReadOnlyList<string> hints)
        {
            if (hints == null || string.IsNullOrEmpty(AssetPath)) return false;
            var own = Normalize(AssetPath);
            foreach (var raw in hints)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var hint = Normalize(raw);
                if (string.Equals(hint, own, StringComparison.OrdinalIgnoreCase)) return true;
                if (own.StartsWith(hint + "/", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        internal static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimEnd('/');

        public bool Equals(TargetContainer other) =>
            other != null && Kind == other.Kind && Scene == other.Scene;

        public override bool Equals(object obj) => Equals(obj as TargetContainer);

        public override int GetHashCode() => Scene.handle * 2 + (int)Kind;
    }

    /// <summary>
    /// The caller's say in which container a path resolves: the explicit
    /// container (`scene_path` / `scene_name`, or `in_prefab` for a prefab
    /// asset edited in place) and `paths_hint`.
    /// </summary>
    public sealed class TargetRequest
    {
        public static readonly TargetRequest None = new TargetRequest(null, null, null);

        public TargetRequest(string scenePath, string sceneName, IReadOnlyList<string> pathsHint, string prefabPath = null)
        {
            ScenePath = string.IsNullOrWhiteSpace(scenePath) ? null : scenePath.Trim();
            SceneName = string.IsNullOrWhiteSpace(sceneName) ? null : sceneName.Trim();
            PrefabPath = string.IsNullOrWhiteSpace(prefabPath) ? null : TargetContainer.Normalize(prefabPath);
            PathsHint = pathsHint;
        }

        public string ScenePath { get; }
        public string SceneName { get; }

        /// <summary>The prefab asset `in_prefab` names (edited in place, no stage).</summary>
        public string PrefabPath { get; }
        public IReadOnlyList<string> PathsHint { get; }

        /// <summary>True when the call named its container explicitly.</summary>
        public bool NamesContainer => ScenePath != null || SceneName != null || PrefabPath != null;

        /// <summary>The request a typed tool's body carries. What the body
        /// leaves out is taken from the enclosing call (a batch step inherits
        /// the batch's paths_hint), so a tool reads its request this way
        /// whether it runs alone or nested.</summary>
        public static TargetRequest For(string body)
        {
            var own = FromBody(body);
            var outer = TargetScope.Current?.Request;
            return outer == null ? own : own.Over(outer);
        }

        /// <summary>The request of the tool call running now (for tools that
        /// receive typed parameters instead of the body).</summary>
        public static TargetRequest Ambient => TargetScope.Current?.Request ?? None;

        internal static TargetRequest FromBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith("{")) return None;
            var scenePath = TopString(body, "scene_path") ?? TopString(body, "scenePath");
            var sceneName = TopString(body, "scene_name") ?? TopString(body, "sceneName");
            var prefabPath = TopString(body, PrefabAssetEdits.Key);
            var hint = JsonBody.GetStringArray(JsonBody.TopLevelField(body, "paths_hint"), "paths_hint");
            if (scenePath == null && sceneName == null && string.IsNullOrWhiteSpace(prefabPath) &&
                (hint == null || hint.Length == 0)) return None;
            return new TargetRequest(scenePath, sceneName, hint, prefabPath);
        }

        internal TargetRequest Over(TargetRequest outer)
        {
            if (outer == null || ReferenceEquals(outer, None)) return this;
            var scenePath = NamesContainer ? ScenePath : outer.ScenePath;
            var sceneName = NamesContainer ? SceneName : outer.SceneName;
            var prefabPath = NamesContainer ? PrefabPath : outer.PrefabPath;
            var hint = PathsHint != null && PathsHint.Count > 0 ? PathsHint : outer.PathsHint;
            return new TargetRequest(scenePath, sceneName, hint, prefabPath);
        }

        private static string TopString(string body, string key) =>
            JsonBody.GetString(JsonBody.TopLevelField(body, key), key);
    }

    /// <summary>Why a target did not resolve: a stable code, a message, an
    /// optional detail object (members merged into the error) and next
    /// steps. A refusal is a failed call (mutation.success:false).</summary>
    public sealed class ResolveFailure
    {
        public const string GameObjectNotFound = "gameobject_not_found";
        public const string ParentNotFound = "parent_not_found";
        public const string AmbiguousPath = "ambiguous_path";
        public const string PersistentObject = "persistent_object";
        public const string PreviewSceneObject = "preview_scene_object";
        public const string NotAGameObject = "not_a_gameobject";
        public const string SceneNotFound = "scene_not_found";
        public const string ContainerRequired = "container_required";
        public const string InPrefabUnsupported = "in_prefab_unsupported";

        public ResolveFailure(string code, string message, string detailJson = null, string[] nextSteps = null)
        {
            Code = code;
            Message = message ?? "";
            DetailJson = detailJson;
            NextSteps = nextSteps;
        }

        public string Code { get; }
        public string Message { get; }

        /// <summary>A JSON object whose members join the error object, or null.</summary>
        public string DetailJson { get; }
        public string[] NextSteps { get; }

        /// <summary>The address resolved to nothing (as opposed to resolving
        /// ambiguously or to an object the tool must not edit).</summary>
        public bool IsNotFound => Code == GameObjectNotFound || Code == ParentNotFound;

        public ToolDispatchResult ToResult() =>
            ToolDispatchResult.FailWithDetail(Code, Message, DetailJson, NextSteps);

        /// <summary>The {"error":{…},"agentNextSteps":[…]} body string-returning
        /// tools answer with (the registry reads it back as a failed call).</summary>
        public string ToRefusalJson()
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":").Append(BridgeJson.EscapeString(Code));
            sb.Append(",\"message\":").Append(BridgeJson.EscapeString(Message));
            var detail = DetailJson?.Trim();
            if (!string.IsNullOrEmpty(detail) && detail.Length > 2 && detail[0] == '{')
                sb.Append(',').Append(detail, 1, detail.Length - 2);
            sb.Append('}');
            if (NextSteps != null && NextSteps.Length > 0)
            {
                sb.Append(",\"agentNextSteps\":[");
                for (int i = 0; i < NextSteps.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(NextSteps[i]));
                }
                sb.Append(']');
            }
            sb.Append('}');
            return sb.ToString();
        }
    }

    /// <summary>The outcome of one lookup: the GameObject and its container,
    /// or the failure.</summary>
    public struct TargetResolution
    {
        public GameObject GameObject;
        public TargetContainer Container;
        public ResolveFailure Failure;

        public bool Ok => GameObject != null && Failure == null;

        internal static TargetResolution Found(GameObject go, TargetContainer container) =>
            new TargetResolution { GameObject = go, Container = container };

        internal static TargetResolution Fail(ResolveFailure failure) =>
            new TargetResolution { Failure = failure };
    }

    /// <summary>
    /// Request-scoped resolution state of the tool call running on the main
    /// thread. It carries the call's <see cref="TargetRequest"/> to tools
    /// that receive typed parameters (embedded domains), and records every
    /// container a target was resolved or created in, so the dispatcher can
    /// tell which scenes or prefab stage a mutation touched. Scopes nest: a
    /// batch step's scope inherits the batch request and hands what it
    /// touched to the batch scope when it closes.
    /// </summary>
    internal sealed class TargetScope : IDisposable
    {
        private static TargetScope s_Current;
        private readonly TargetScope _outer;
        private readonly List<TargetContainer> _touched = new List<TargetContainer>();
        private readonly List<PrefabAssetEditOutcome> _prefabAssets = new List<PrefabAssetEditOutcome>();
        private bool _disposed;

        private TargetScope(TargetRequest request, TargetScope outer)
        {
            Request = request;
            _outer = outer;
        }

        internal static TargetScope Current => s_Current;

        internal TargetRequest Request { get; }

        internal IReadOnlyList<TargetContainer> Touched => _touched;

        /// <summary>The prefab assets this call edited in place (in_prefab),
        /// one entry per load / save / unload cycle.</summary>
        internal IReadOnlyList<PrefabAssetEditOutcome> PrefabAssets => _prefabAssets;

        internal static TargetScope Enter(string body)
        {
            var own = TargetRequest.FromBody(body);
            var outer = s_Current;
            var scope = new TargetScope(outer == null ? own : own.Over(outer.Request), outer);
            s_Current = scope;
            TypedTargets.ClearLastFailure();
            return scope;
        }

        internal static void Touch(TargetContainer container)
        {
            var scope = s_Current;
            if (scope == null || container == null) return;
            scope.Add(container);
        }

        private void Add(TargetContainer container)
        {
            if (!_touched.Contains(container)) _touched.Add(container);
        }

        internal static void NotePrefabAsset(PrefabAssetEditOutcome outcome)
        {
            var scope = s_Current;
            if (scope == null || outcome == null) return;
            scope._prefabAssets.Add(outcome);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(s_Current, this)) s_Current = _outer;
            if (_outer != null)
            {
                foreach (var c in _touched) _outer.Add(c);
                _outer._prefabAssets.AddRange(_prefabAssets);
            }
        }
    }

    /// <summary>
    /// Enumerates the containers a typed tool can reach and finds which one
    /// holds a GameObject.
    /// </summary>
    internal static class TargetContainers
    {
        /// <summary>The open prefab stage, or null (main stage, or a stage
        /// whose contents are not loaded).</summary>
        internal static PrefabStage OpenStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null || stage.prefabContentsRoot == null || !stage.scene.IsValid()) return null;
            return stage;
        }

        /// <summary>The open prefab stage's container, or null.</summary>
        internal static TargetContainer Stage()
        {
            var stage = OpenStage();
            return stage == null ? null : TargetContainer.ForStage(stage);
        }

        /// <summary>The open prefab stage first (it is what the Hierarchy
        /// shows), then every loaded scene in load order.</summary>
        internal static List<TargetContainer> All()
        {
            var list = new List<TargetContainer>(SceneManager.sceneCount + 1);
            var stage = Stage();
            if (stage != null) list.Add(stage);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.IsValid() && s.isLoaded) list.Add(TargetContainer.ForScene(s));
            }
            return list;
        }

        /// <summary>The container holding <paramref name="go"/>, or null when
        /// a typed tool must not edit it here; <paramref name="reason"/> then
        /// names why: persistent (inside an asset on disk), stage_environment
        /// (the wrapper Unity adds around the prefab in its stage),
        /// preview_scene (any other preview scene), unloaded.</summary>
        internal static TargetContainer Of(GameObject go, out string reason)
        {
            reason = null;
            if (go == null) { reason = "destroyed"; return null; }
            if (EditorUtility.IsPersistent(go)) { reason = "persistent"; return null; }
            var scene = go.scene;
            if (!scene.IsValid()) { reason = "preview_scene"; return null; }
            var edit = PrefabAssetEdits.Current;
            if (edit != null && scene == edit.Container.Scene) return edit.Container;
            var stage = OpenStage();
            if (stage != null && scene == stage.scene)
            {
                if (!IsSelfOrDescendant(go.transform, stage.prefabContentsRoot.transform))
                {
                    reason = "stage_environment";
                    return null;
                }
                return TargetContainer.ForStage(stage);
            }
            if (EditorSceneManager.IsPreviewScene(scene)) { reason = "preview_scene"; return null; }
            if (!scene.isLoaded) { reason = "unloaded"; return null; }
            return TargetContainer.ForScene(scene);
        }

        internal static TargetContainer Of(GameObject go) => Of(go, out _);

        /// <summary>The container a call names with scene_path / scene_name:
        /// a loaded scene, or the open prefab stage by its prefab asset path;
        /// or with in_prefab: the prefab asset loaded for this call. Null with
        /// <paramref name="failure"/> set when the call names one that is not
        /// loaded / open; null without a failure when it names none.</summary>
        internal static TargetContainer Named(TargetRequest request, out ResolveFailure failure)
        {
            failure = null;
            if (request == null || !request.NamesContainer) return null;

            if (request.PrefabPath != null)
            {
                var edit = PrefabAssetEdits.Current;
                if (edit != null && PrefabAssetEdits.SamePath(edit.AssetPath, request.PrefabPath))
                    return edit.Container;
                failure = new ResolveFailure(ResolveFailure.InPrefabUnsupported,
                    $"in_prefab '{request.PrefabPath}' is not loaded for this call: this tool does not edit a prefab " +
                    "asset in place. Use a tool that declares in_prefab, or open the prefab with " +
                    "unity_open_mcp_prefab_open and address the stage object.");
                return null;
            }

            if (request.ScenePath != null)
            {
                var wanted = TargetContainer.Normalize(request.ScenePath);
                var stage = Stage();
                if (stage != null && string.Equals(TargetContainer.Normalize(stage.AssetPath), wanted,
                        StringComparison.OrdinalIgnoreCase))
                    return stage;
                var scene = SceneTargeting.ResolveByPath(wanted);
                if (scene.IsValid() && scene.isLoaded) return TargetContainer.ForScene(scene);
                failure = new ResolveFailure(ResolveFailure.SceneNotFound,
                    wanted.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                        ? $"scene_path '{wanted}' names a prefab that is not open in a prefab stage. To edit the " +
                          "prefab asset in place, pass it as in_prefab instead; or open it with " +
                          "unity_open_mcp_prefab_open first, or name a loaded scene."
                        : $"Target scene not found / not loaded for scene_path '{wanted}'. Use " +
                          "unity_open_mcp_scene_open (OpenSceneMode.Additive) to load it first, or omit scene_path.",
                    ContainersDetail(), null);
                return null;
            }

            var byName = SceneTargeting.ResolveByName(request.SceneName);
            if (byName.IsValid() && byName.isLoaded) return TargetContainer.ForScene(byName);
            failure = new ResolveFailure(ResolveFailure.SceneNotFound,
                $"Target scene not found / not loaded for scene_name '{request.SceneName}'. Use " +
                "unity_open_mcp_scene_open (OpenSceneMode.Additive) to load it first, or omit scene_name.",
                ContainersDetail(), null);
            return null;
        }

        /// <summary>{"containers":[…]} — every reachable container, for
        /// refusals that ask the caller to pick one.</summary>
        internal static string ContainersDetail()
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"containers\":[");
            var all = All();
            for (int i = 0; i < all.Count; i++)
            {
                if (i > 0) sb.Append(',');
                all[i].AppendJson(sb);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        internal static bool IsSelfOrDescendant(Transform t, Transform ancestor)
        {
            for (var cur = t; cur != null; cur = cur.parent)
                if (cur == ancestor) return true;
            return false;
        }

        /// <summary>Whether the auto-save toggle of Prefab Mode is on for the
        /// open stage (Unity then writes stage edits to the prefab asset on a
        /// later Editor update). Null when it cannot be read: the toggle is
        /// not public API, and its project-level gate
        /// (EditorSettings.prefabModeAllowAutoSave) is the fallback.</summary>
        internal static bool? StageAutoSave(PrefabStage stage)
        {
            if (stage == null) return null;
            try
            {
                var prop = typeof(PrefabStage).GetProperty("autoSave",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (prop != null && prop.PropertyType == typeof(bool))
                    return (bool)prop.GetValue(stage);
            }
            catch
            {
                // fall through to the project setting
            }
            try
            {
                if (!EditorSettings.prefabModeAllowAutoSave) return false;
            }
            catch
            {
                // setting unavailable
            }
            return null;
        }

        /// <summary>Instance id + canonical path of a stage's prefab root, for
        /// results that tell the agent how to address the stage.</summary>
        internal static void AppendRoot(StringBuilder sb, PrefabStage stage)
        {
            var root = stage.prefabContentsRoot;
            sb.Append("\"rootPath\":\"").Append(TypedTargets.Esc(TypedTargets.HierarchyPath(root)));
            sb.Append("\",\"rootInstanceId\":").Append(InstanceId.ToJson(root));
        }
    }
}
