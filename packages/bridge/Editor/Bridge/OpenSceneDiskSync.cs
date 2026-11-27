using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityOpenMcpBridge
{
    // Open scenes (and the open prefab stage) whose file changed on disk since
    // the AssetDatabase last imported it: a git checkout, a code generator, an
    // agent writing YAML. A refresh that reimports such a scene while it is
    // open makes Unity raise its native "Scene(s) Have Been Modified" decision
    // dialog once the refresh returns, and a prefab stage with unsaved changes
    // raises "Prefab Has Been Changed on Disk". Either blocks the main thread
    // until a human answers it, so no tool can run in the meantime.
    //
    // Every refresh a bridge tool starts goes through Run:
    //   - a clean changed scene is closed before the refresh and reopened in
    //     place after it (loaded or not as before, same order, same active
    //     scene), so it is not open when it is reimported and nothing is lost;
    //   - a dirty changed scene, a changed scene in play mode, an open scene
    //     whose file is gone, or a prefab stage with unsaved changes whose
    //     prefab changed refuses the refresh before it runs
    //     (open_scene_changed_on_disk). A clean prefab stage is left alone:
    //     Unity reloads it itself without asking.
    //
    // "Changed" is the AssetDatabase's own test: the source-file hash it
    // recorded at the last import (AssetDatabase.GetSourceAssetFileHash,
    // internal — the prefab stage's own disk check reads the same value)
    // against Hash128.Compute of the file now. A refresh reimports exactly the
    // files whose content hash moved, so this predicts the dialog — including
    // for a scene opened after its file changed: it was loaded from the new
    // file, but the import is still due and still raises the dialog.
    public static class OpenSceneDiskSync
    {
        internal const string RefusalCode = "open_scene_changed_on_disk";

        internal enum DiskState { Unchanged, Changed, Missing, Unknown }

        internal struct OpenScene
        {
            internal string Path; // "" for an untitled scene
            internal bool Loaded;
            internal bool Dirty;
        }

        internal sealed class Plan
        {
            /// <summary>Clean scenes that changed on disk: reloaded around the refresh.</summary>
            internal readonly List<string> Reload = new List<string>();
            internal readonly List<string> DirtyChanged = new List<string>();
            internal readonly List<string> Missing = new List<string>();
            /// <summary>The prefab stage with unsaved changes whose prefab changed on disk.</summary>
            internal string PrefabStage;
            internal bool PlayMode;

            internal bool Refused => DirtyChanged.Count > 0 || Missing.Count > 0 || PrefabStage != null
                || (PlayMode && Reload.Count > 0);
        }

        /// <summary>Pure: what a refresh would do to the open scenes.</summary>
        internal static Plan Decide(IEnumerable<OpenScene> scenes, Func<string, DiskState> disk, bool isPlaying,
            string prefabStagePath, bool prefabStageDirty)
        {
            var plan = new Plan { PlayMode = isPlaying };
            foreach (var scene in scenes)
            {
                if (string.IsNullOrEmpty(scene.Path)) continue;
                switch (disk(scene.Path))
                {
                    case DiskState.Changed:
                        if (scene.Dirty) plan.DirtyChanged.Add(scene.Path);
                        else plan.Reload.Add(scene.Path);
                        break;
                    case DiskState.Missing:
                        plan.Missing.Add(scene.Path);
                        break;
                }
            }
            if (!string.IsNullOrEmpty(prefabStagePath) && prefabStageDirty && disk(prefabStagePath) == DiskState.Changed)
                plan.PrefabStage = prefabStagePath;
            return plan;
        }

        private static readonly Func<string, Hash128> RecordedHash = ResolveRecordedHash();
        private static bool _warnedUnavailable;

        private static Func<string, Hash128> ResolveRecordedHash()
        {
            try
            {
                var method = typeof(AssetDatabase).GetMethod("GetSourceAssetFileHash",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (method == null || method.ReturnType != typeof(Hash128)) return null;
                return (Func<string, Hash128>)Delegate.CreateDelegate(typeof(Func<string, Hash128>), method);
            }
            catch { return null; }
        }

        // The prefab stage's own "unsaved changes" test (internal; it compares
        // scene dirty ids, which scene.isDirty does not always follow) is the
        // one its "changed on disk" dialog asks.
        private static readonly PropertyInfo StageHasUnsavedChanges = typeof(PrefabStage).GetProperty(
            "hasUnsavedChanges", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static bool HasUnsavedChanges(PrefabStage stage)
        {
            try
            {
                if (StageHasUnsavedChanges?.GetValue(stage) is bool unsaved && unsaved) return true;
            }
            catch { /* fall back to the scene flag */ }
            return stage.scene.isDirty;
        }

        /// <summary>Whether this Editor exposes the hash the check compares against.</summary>
        internal static bool DetectionAvailable => RecordedHash != null;

        /// <summary>
        /// Main thread: whether the next refresh would reimport this asset — its
        /// file's content hash differs from the one the AssetDatabase recorded.
        /// Unknown when the asset is not in the database or the Editor cannot say.
        /// </summary>
        internal static DiskState DiskStateOf(string assetPath)
        {
            if (RecordedHash == null || string.IsNullOrEmpty(assetPath)) return DiskState.Unknown;
            try
            {
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (string.IsNullOrEmpty(guid)) return DiskState.Unknown;
                var recorded = RecordedHash(guid);
                if (!recorded.isValid) return DiskState.Unknown;
                var file = PhysicalPath(assetPath);
                if (!File.Exists(file)) return DiskState.Missing;
                return Hash128.Compute(File.ReadAllBytes(file)) == recorded ? DiskState.Unchanged : DiskState.Changed;
            }
            catch
            {
                return DiskState.Unknown;
            }
        }

        private static string PhysicalPath(string assetPath)
        {
            try
            {
                var physical = FileUtil.GetPhysicalPath(assetPath);
                if (!string.IsNullOrEmpty(physical)) return Path.GetFullPath(physical);
            }
            catch { /* fall through */ }
            return Path.GetFullPath(assetPath);
        }

        // One scene of the hierarchy as it was before the refresh. Scene is the
        // live handle: replaced when the scene is reopened.
        private sealed class Captured
        {
            internal Scene Scene;
            internal string Path;
            internal bool Loaded;
            internal bool Dirty;
            internal bool Active;
        }

        private static List<Captured> CaptureScenes()
        {
            var active = SceneManager.GetActiveScene();
            var list = new List<Captured>(SceneManager.sceneCount);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                list.Add(new Captured
                {
                    Scene = scene,
                    Path = scene.path ?? "",
                    Loaded = scene.isLoaded,
                    Dirty = scene.isDirty,
                    Active = scene == active,
                });
            }
            return list;
        }

        /// <summary>The result of a refresh run through <see cref="Run"/>.</summary>
        internal sealed class Outcome
        {
            internal Plan Plan;
            /// <summary>The refresh ran.</summary>
            internal bool Ran;
            /// <summary>Scenes closed before the refresh and reopened after it.</summary>
            internal readonly List<string> ReloadedScenes = new List<string>();
            /// <summary>Scenes that could not be reopened after the refresh ("path: reason").</summary>
            internal readonly List<string> ReopenErrors = new List<string>();
            /// <summary>Set when a changed scene could not be closed, so the refresh did not run.</summary>
            internal string CloseError;

            internal bool Refused => !Ran;

            /// <summary>`"reloadedScenes":[…]` (plus `"reopenErrors"` when a reopen failed), without a leading comma.</summary>
            internal string MembersJson()
            {
                var sb = new StringBuilder(64);
                sb.Append("\"reloadedScenes\":");
                AppendArray(sb, ReloadedScenes);
                if (ReopenErrors.Count > 0)
                {
                    sb.Append(",\"reopenErrors\":");
                    AppendArray(sb, ReopenErrors);
                }
                return sb.ToString();
            }

            /// <summary>The failed result a refresh tool answers with when the refresh was refused.</summary>
            internal ToolDispatchResult Refusal() => BuildRefusal(Plan, CloseError);
        }

        /// <summary>
        /// Main thread. Runs <paramref name="refresh"/> unless an open scene
        /// changed on disk in a way a reload would lose or cannot do (refused,
        /// nothing ran); clean changed scenes are reloaded around it. Fails open
        /// when the scene setup cannot be read or the Editor cannot tell what
        /// changed.
        /// </summary>
        internal static Outcome Run(Action refresh)
        {
            var outcome = new Outcome();
            List<Captured> scenes;
            try
            {
                if (!DetectionAvailable) WarnUnavailableOnce();
                scenes = CaptureScenes();
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                var open = new List<OpenScene>(scenes.Count);
                foreach (var s in scenes) open.Add(new OpenScene { Path = s.Path, Loaded = s.Loaded, Dirty = s.Dirty });
                outcome.Plan = Decide(open, DiskStateOf, EditorApplication.isPlaying,
                    stage != null ? stage.assetPath : null, stage != null && HasUnsavedChanges(stage));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[unity-open-mcp] Could not check the open scenes against disk before a refresh; " +
                                 "refreshing without it: " + e.Message);
                refresh();
                outcome.Ran = true;
                return outcome;
            }

            if (outcome.Plan.Refused) return outcome;
            if (outcome.Plan.Reload.Count == 0)
            {
                refresh();
                outcome.Ran = true;
                return outcome;
            }
            ReloadAround(scenes, outcome, refresh);
            return outcome;
        }

        /// <summary>
        /// A whole-project refresh that follows a tool's own AssetDatabase work,
        /// which already imported what the tool wrote. Clean scenes that changed
        /// on disk are reloaded around it; when a reload would lose edits the
        /// refresh is skipped with a warning (the call's logs carry it) instead
        /// of failing a call whose own edit already happened.
        /// </summary>
        public static void RefreshAfterEdit(ImportAssetOptions options = ImportAssetOptions.Default)
        {
            var outcome = Run(() => AssetDatabase.Refresh(options));
            if (outcome.Refused)
                Debug.LogWarning("[unity-open-mcp] Skipped the project refresh after this edit: " +
                                 RefusalReason(outcome.Plan, outcome.CloseError) +
                                 " Resolve it, then run unity_open_mcp_assets_refresh.");
            else if (outcome.ReloadedScenes.Count > 0)
                Debug.Log("[unity-open-mcp] Reloaded open scene(s) that changed on disk around the project refresh: " +
                          string.Join(", ", outcome.ReloadedScenes) + ".");
        }

        private static void WarnUnavailableOnce()
        {
            if (_warnedUnavailable) return;
            _warnedUnavailable = true;
            Debug.LogWarning("[unity-open-mcp] This Editor does not expose AssetDatabase.GetSourceAssetFileHash, so a " +
                             "refresh cannot tell whether an open scene changed on disk; Unity may ask about such a scene " +
                             "in a native dialog.");
        }

        // Close the clean changed scenes, refresh, reopen them as they were.
        // A placeholder untitled scene keeps one scene loaded when every loaded
        // scene is being reloaded (the last loaded scene cannot be closed).
        private static void ReloadAround(List<Captured> scenes, Outcome outcome, Action refresh)
        {
            var reload = new HashSet<string>(outcome.Plan.Reload, StringComparer.Ordinal);
            var placeholder = default(Scene);
            var closed = new List<Captured>();
            try
            {
                if (!scenes.Exists(s => s.Loaded && !reload.Contains(s.Path)))
                    placeholder = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                foreach (var s in scenes)
                {
                    if (!reload.Contains(s.Path)) continue;
                    if (!EditorSceneManager.CloseScene(s.Scene, true))
                        throw new InvalidOperationException("EditorSceneManager.CloseScene refused '" + s.Path + "'.");
                    closed.Add(s);
                }
            }
            catch (Exception e)
            {
                // The refresh must not run with a changed scene still open.
                outcome.CloseError = e.Message;
                Reopen(scenes, closed, placeholder, outcome, report: false);
                return;
            }

            try
            {
                refresh();
                outcome.Ran = true;
            }
            finally
            {
                Reopen(scenes, closed, placeholder, outcome, report: true);
            }
        }

        private static void Reopen(List<Captured> scenes, List<Captured> closed, Scene placeholder, Outcome outcome, bool report)
        {
            foreach (var s in closed)
            {
                try
                {
                    s.Scene = EditorSceneManager.OpenScene(s.Path,
                        s.Loaded ? OpenSceneMode.Additive : OpenSceneMode.AdditiveWithoutLoading);
                    if (report) outcome.ReloadedScenes.Add(s.Path);
                }
                catch (Exception e)
                {
                    s.Scene = default;
                    outcome.ReopenErrors.Add(s.Path + ": " + e.Message);
                }
            }

            if (placeholder.IsValid())
            {
                var anotherLoaded = false;
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene != placeholder && scene.isLoaded) { anotherLoaded = true; break; }
                }
                if (anotherLoaded)
                {
                    try { EditorSceneManager.CloseScene(placeholder, true); }
                    catch { /* an extra empty scene is harmless */ }
                }
            }

            // The hierarchy order and the active scene as before.
            var previous = default(Scene);
            foreach (var s in scenes)
            {
                if (!s.Scene.IsValid()) continue;
                if (previous.IsValid())
                {
                    try { EditorSceneManager.MoveSceneAfter(s.Scene, previous); }
                    catch { /* order is cosmetic */ }
                }
                previous = s.Scene;
            }
            var active = scenes.Find(s => s.Active);
            if (active != null && active.Scene.IsValid() && active.Scene.isLoaded)
            {
                try { SceneManager.SetActiveScene(active.Scene); }
                catch { /* keep whatever Unity made active */ }
            }
        }

        /// <summary>
        /// Main thread, before an execute_csharp snippet runs: a snippet that
        /// refreshes or imports (its own AssetDatabase.Refresh / ImportAsset) is
        /// refused while an open scene or the prefab stage changed on disk, clean
        /// or not — the snippet's refresh cannot be wrapped, and assets_refresh
        /// resolves the state first. Null when the snippet may run.
        /// </summary>
        internal static GateDispatchResult RefuseSnippet(ToolRequestContract contract, string body)
        {
            if (contract == null || contract.ToolName != "unity_open_mcp_execute_csharp") return null;
            if (!SnippetClassifier.CallsAssetImport(JsonBody.GetString(JsonBody.TopLevelField(body ?? "{}", "code"), "code")))
                return null;
            Plan plan;
            try
            {
                var scenes = CaptureScenes();
                var open = new List<OpenScene>(scenes.Count);
                foreach (var s in scenes) open.Add(new OpenScene { Path = s.Path, Loaded = s.Loaded, Dirty = s.Dirty });
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                plan = Decide(open, DiskStateOf, EditorApplication.isPlaying,
                    stage != null ? stage.assetPath : null, stage != null && HasUnsavedChanges(stage));
            }
            catch
            {
                return null; // fail open, like the refresh tools
            }
            if (!plan.Refused && plan.Reload.Count == 0) return null;

            var changed = new List<string>(plan.Reload);
            changed.AddRange(plan.DirtyChanged);
            changed.AddRange(plan.Missing);
            if (plan.PrefabStage != null) changed.Add(plan.PrefabStage);
            var refusal = BuildRefusal(plan, null, listCleanChanges: true);
            var steps = new List<string>
            {
                "Run unity_open_mcp_assets_refresh {} first: it reloads a clean changed scene in place (or names the " +
                "choice for one with unsaved edits), then run the snippet again.",
            };
            if (plan.Refused) steps.AddRange(refusal.NextSteps);
            return new GateDispatchResult
            {
                Mutation = ToolDispatchResult.FailWithDetail(RefusalCode,
                    "The snippet was not run: it refreshes or imports assets (" +
                    "AssetDatabase.Refresh / ImportAsset), and open content changed on disk since its last import: " +
                    string.Join(", ", changed) + ". The snippet's refresh would make Unity ask about it in a native " +
                    "dialog that blocks the Editor until a human answers it.",
                    refusal.ErrorDetailJson, steps.ToArray()),
                GateRan = false,
                Outcome = GateOutcome.Skipped,
                SkippedReason = "request_rejected",
                GateFailed = false,
                AgentNextSteps = steps.ToArray(),
            };
        }

        // ---- Refusal --------------------------------------------------------

        internal static string RefusalReason(Plan plan, string closeError)
        {
            var parts = new List<string>();
            if (plan != null)
            {
                if (plan.DirtyChanged.Count > 0)
                    parts.Add("open scene(s) with unsaved changes changed on disk: " + string.Join(", ", plan.DirtyChanged) + ".");
                if (plan.Missing.Count > 0)
                    parts.Add("the file of open scene(s) is gone from disk: " + string.Join(", ", plan.Missing) + ".");
                if (plan.PlayMode && plan.Reload.Count > 0)
                    parts.Add("open scene(s) changed on disk in play mode, where they cannot be reloaded: " +
                              string.Join(", ", plan.Reload) + ".");
                if (plan.PrefabStage != null)
                    parts.Add("the open prefab stage has unsaved changes and its prefab changed on disk: " + plan.PrefabStage + ".");
            }
            if (closeError != null)
                parts.Add("a scene that changed on disk could not be closed for the reload: " + closeError);
            return parts.Count == 0 ? "an open scene changed on disk." : string.Join(" ", parts);
        }

        // listCleanChanges: also name the clean changed scenes (reason
        // "changed") — the snippet preflight refuses for those too.
        internal static ToolDispatchResult BuildRefusal(Plan plan, string closeError, bool listCleanChanges = false)
        {
            var message = "The refresh was not run: " + RefusalReason(plan, closeError) +
                          " Refreshing would make Unity ask in a native dialog (\"Scene(s) Have Been Modified\" / " +
                          "\"Prefab Has Been Changed on Disk\"), which blocks the Editor until a human answers it. " +
                          "A clean open scene that changed on disk is reloaded in place instead.";

            var detail = new StringBuilder(128);
            detail.Append("{\"scenes\":[");
            var first = true;
            void Entry(string path, string reason)
            {
                if (!first) detail.Append(',');
                first = false;
                detail.Append("{\"path\":").Append(BridgeJson.EscapeString(path))
                    .Append(",\"reason\":").Append(BridgeJson.EscapeString(reason)).Append('}');
            }
            if (plan != null)
            {
                foreach (var p in plan.DirtyChanged) Entry(p, "unsaved_changes");
                foreach (var p in plan.Missing) Entry(p, "file_missing");
                if (plan.PlayMode) foreach (var p in plan.Reload) Entry(p, "play_mode");
                else if (listCleanChanges) foreach (var p in plan.Reload) Entry(p, "changed");
            }
            detail.Append("],\"prefabStage\":")
                .Append(plan?.PrefabStage != null ? BridgeJson.EscapeString(plan.PrefabStage) : "null").Append('}');

            return ToolDispatchResult.FailWithDetail(RefusalCode, message, detail.ToString(), NextSteps(plan));
        }

        internal static string[] NextSteps(Plan plan)
        {
            var steps = new List<string>();
            if (plan != null)
            {
                foreach (var path in plan.DirtyChanged)
                {
                    steps.Add("Keep the edits in memory and overwrite the change on disk: " +
                              BridgeJson.SceneCall("unity_open_mcp_scene_save", path, null) + ", then retry.");
                    steps.Add("Or take the version on disk and drop the unsaved edits: " +
                              BridgeJson.SceneCall("unity_open_mcp_scene_open", path, ",\"ignore_scene_dirty\":true") +
                              " (single mode: the other open scenes close too), then retry.");
                }
                foreach (var path in plan.Missing)
                    steps.Add("Save the open scene back to disk (" +
                              BridgeJson.SceneCall("unity_open_mcp_scene_save", path, null) +
                              ") or open another scene (unity_open_mcp_scene_open), then retry.");
                if (plan.PlayMode && plan.Reload.Count > 0)
                    steps.Add("Stop play mode — unity_open_mcp_editor_set_state {\"state\":\"stop\"} — then retry: " +
                              "in edit mode the changed scene is reloaded in place.");
                if (plan.PrefabStage != null)
                {
                    var hint = BridgeJson.EscapeString(plan.PrefabStage);
                    steps.Add("Keep the prefab stage's edits and overwrite the change on disk: unity_open_mcp_prefab_save " +
                              "{\"paths_hint\":[" + hint + "]}, then retry.");
                    steps.Add("Or take the version on disk: unity_open_mcp_prefab_close {\"save\":false,\"paths_hint\":[" +
                              hint + "]}, then retry.");
                }
            }
            if (steps.Count == 0)
                steps.Add("Check the open scenes with unity_open_mcp_scene_list_opened, then retry.");
            return steps.ToArray();
        }

        private static void AppendArray(StringBuilder sb, List<string> values)
        {
            sb.Append('[');
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(values[i]));
            }
            sb.Append(']');
        }
    }
}
