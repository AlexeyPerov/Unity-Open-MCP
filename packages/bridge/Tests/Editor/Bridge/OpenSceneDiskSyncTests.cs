using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityOpenMcpBridge.Tests
{
    // A refresh started by a bridge tool must never leave Unity's native
    // "scene(s) changed on disk" dialog behind. The plan is pure; the disk
    // check and the reload are exercised on a scratch scene whose file is
    // changed and restored inside one test, with a recorder in place of the
    // refresh — no test here ever refreshes while a changed scene is open.
    public class OpenSceneDiskSyncTests
    {
        private const string Folder = "Assets/__OpenSceneDiskSyncTests";

        private static OpenSceneDiskSync.OpenScene Open(string path, bool dirty = false, bool loaded = true) =>
            new OpenSceneDiskSync.OpenScene { Path = path, Dirty = dirty, Loaded = loaded };

        private static System.Func<string, OpenSceneDiskSync.DiskState> Disk(Dictionary<string, OpenSceneDiskSync.DiskState> states) =>
            path => states.TryGetValue(path, out var state) ? state : OpenSceneDiskSync.DiskState.Unchanged;

        // ----- the plan (pure) -----

        [Test]
        public void Decide_CleanChangedScene_IsReloaded_DirtyOrMissingRefuses()
        {
            var disk = Disk(new Dictionary<string, OpenSceneDiskSync.DiskState>
            {
                { "Assets/A.unity", OpenSceneDiskSync.DiskState.Changed },
                { "Assets/B.unity", OpenSceneDiskSync.DiskState.Changed },
                { "Assets/C.unity", OpenSceneDiskSync.DiskState.Missing },
                { "Assets/D.unity", OpenSceneDiskSync.DiskState.Unknown },
            });

            var clean = OpenSceneDiskSync.Decide(new[] { Open("Assets/A.unity"), Open(""), Open("Assets/E.unity") },
                disk, false, null, false);
            CollectionAssert.AreEqual(new[] { "Assets/A.unity" }, clean.Reload);
            Assert.IsFalse(clean.Refused, "a clean scene is reloaded, not refused");

            var dirty = OpenSceneDiskSync.Decide(new[] { Open("Assets/A.unity"), Open("Assets/B.unity", dirty: true) },
                disk, false, null, false);
            CollectionAssert.AreEqual(new[] { "Assets/B.unity" }, dirty.DirtyChanged);
            Assert.IsTrue(dirty.Refused);

            var missing = OpenSceneDiskSync.Decide(new[] { Open("Assets/C.unity") }, disk, false, null, false);
            CollectionAssert.AreEqual(new[] { "Assets/C.unity" }, missing.Missing);
            Assert.IsTrue(missing.Refused);

            var unknown = OpenSceneDiskSync.Decide(new[] { Open("Assets/D.unity", dirty: true), Open("Assets/E.unity", dirty: true) },
                disk, false, null, false);
            Assert.IsFalse(unknown.Refused, "unchanged or unknown scenes never refuse (fail-open)");
            Assert.AreEqual(0, unknown.Reload.Count);
        }

        [Test]
        public void Decide_PlayMode_RefusesAChangedScene_Only()
        {
            var disk = Disk(new Dictionary<string, OpenSceneDiskSync.DiskState> { { "Assets/A.unity", OpenSceneDiskSync.DiskState.Changed } });
            Assert.IsTrue(OpenSceneDiskSync.Decide(new[] { Open("Assets/A.unity") }, disk, true, null, false).Refused);
            Assert.IsFalse(OpenSceneDiskSync.Decide(new[] { Open("Assets/E.unity") }, disk, true, null, false).Refused);
        }

        [Test]
        public void Decide_PrefabStage_RefusesOnlyWithUnsavedChangesAndAChangedPrefab()
        {
            var disk = Disk(new Dictionary<string, OpenSceneDiskSync.DiskState> { { "Assets/P.prefab", OpenSceneDiskSync.DiskState.Changed } });
            var none = new OpenSceneDiskSync.OpenScene[0];
            Assert.AreEqual("Assets/P.prefab", OpenSceneDiskSync.Decide(none, disk, false, "Assets/P.prefab", true).PrefabStage);
            // Clean: Unity reloads the stage itself, without a dialog.
            Assert.IsFalse(OpenSceneDiskSync.Decide(none, disk, false, "Assets/P.prefab", false).Refused);
            Assert.IsFalse(OpenSceneDiskSync.Decide(none, disk, false, "Assets/Q.prefab", true).Refused);
        }

        [Test]
        public void Refusal_NamesTheScenesAndTheChoices()
        {
            var disk = Disk(new Dictionary<string, OpenSceneDiskSync.DiskState>
            {
                { "Assets/B.unity", OpenSceneDiskSync.DiskState.Changed },
                { "Assets/P.prefab", OpenSceneDiskSync.DiskState.Changed },
            });
            var plan = OpenSceneDiskSync.Decide(new[] { Open("Assets/B.unity", dirty: true) }, disk, false, "Assets/P.prefab", true);
            var refusal = OpenSceneDiskSync.BuildRefusal(plan, null);

            Assert.IsFalse(refusal.Success);
            Assert.AreEqual("open_scene_changed_on_disk", refusal.ErrorCode);
            StringAssert.Contains("Assets/B.unity", refusal.ErrorMessage);
            StringAssert.Contains("native dialog", refusal.ErrorMessage);
            Assert.IsTrue(BridgeJson.IsCompleteJson(refusal.ErrorDetailJson), refusal.ErrorDetailJson);
            StringAssert.Contains("{\"path\":\"Assets/B.unity\",\"reason\":\"unsaved_changes\"}", refusal.ErrorDetailJson);
            StringAssert.Contains("\"prefabStage\":\"Assets/P.prefab\"", refusal.ErrorDetailJson);

            var steps = string.Join("\n", refusal.NextSteps);
            StringAssert.Contains("unity_open_mcp_scene_save {\"asset_path\":\"Assets/B.unity\"", steps);
            StringAssert.Contains("unity_open_mcp_scene_open {\"asset_path\":\"Assets/B.unity\"", steps);
            StringAssert.Contains("\"ignore_scene_dirty\":true", steps);
            StringAssert.Contains("unity_open_mcp_prefab_save", steps);
            StringAssert.Contains("unity_open_mcp_prefab_close {\"save\":false", steps);

            var play = OpenSceneDiskSync.BuildRefusal(OpenSceneDiskSync.Decide(new[] { Open("Assets/B.unity") }, disk, true, null, false), null);
            StringAssert.Contains("{\"path\":\"Assets/B.unity\",\"reason\":\"play_mode\"}", play.ErrorDetailJson);
            StringAssert.Contains("unity_open_mcp_editor_set_state {\"state\":\"stop\"}", string.Join("\n", play.NextSteps));
        }

        // ----- the disk check and the reload, on a scratch scene -----

        [Test]
        public void DetectionIsAvailableOnThisEditor()
        {
            Assert.IsTrue(OpenSceneDiskSync.DetectionAvailable,
                "AssetDatabase.GetSourceAssetFileHash must resolve; without it a refresh cannot tell an open scene changed on disk");
        }

        [Test]
        public void DiskStateOf_FollowsTheFileAgainstTheLastImport()
        {
            var path = CreateScratchScene("DiskState", out var scene);
            var file = Path.GetFullPath(path);
            var original = File.ReadAllBytes(file);
            var moved = file + ".moved";
            try
            {
                EditorSceneManager.CloseScene(scene, true);
                Assert.AreEqual(OpenSceneDiskSync.DiskState.Unchanged, OpenSceneDiskSync.DiskStateOf(path));

                File.WriteAllText(file, File.ReadAllText(file).Replace("m_Name: Before", "m_Name: After"));
                Assert.AreEqual(OpenSceneDiskSync.DiskState.Changed, OpenSceneDiskSync.DiskStateOf(path));

                File.WriteAllBytes(file, original);
                Assert.AreEqual(OpenSceneDiskSync.DiskState.Unchanged, OpenSceneDiskSync.DiskStateOf(path),
                    "the same content as imported is unchanged, whatever its timestamp");

                File.Move(file, moved);
                Assert.AreEqual(OpenSceneDiskSync.DiskState.Missing, OpenSceneDiskSync.DiskStateOf(path));
                File.Move(moved, file);

                Assert.AreEqual(OpenSceneDiskSync.DiskState.Unknown, OpenSceneDiskSync.DiskStateOf(Folder + "/NotImported.unity"));
            }
            finally
            {
                if (File.Exists(moved)) File.Move(moved, file);
                File.WriteAllBytes(file, original);
                DeleteScratch();
            }
        }

        [Test]
        public void Run_CleanSceneChangedOnDisk_IsClosedForTheRefresh_AndReopenedInPlace()
        {
            var path = CreateScratchScene("Clean", out var scene);
            var file = Path.GetFullPath(path);
            var original = File.ReadAllBytes(file);
            var before = OpenScenePaths();
            SceneManager.SetActiveScene(scene);
            try
            {
                File.WriteAllText(file, File.ReadAllText(file).Replace("m_Name: Before", "m_Name: After"));

                var refreshes = 0;
                var outcome = OpenSceneDiskSync.Run(() =>
                {
                    refreshes++;
                    Assert.IsFalse(SceneManager.GetSceneByPath(path).IsValid(), "the changed scene is not open during the refresh");
                });

                Assert.AreEqual(1, refreshes);
                Assert.IsTrue(outcome.Ran);
                CollectionAssert.AreEqual(new[] { path }, outcome.ReloadedScenes);
                StringAssert.Contains("\"reloadedScenes\":[\"" + path + "\"]", outcome.MembersJson());
                var reopened = SceneManager.GetSceneByPath(path);
                Assert.IsTrue(reopened.IsValid() && reopened.isLoaded, "reopened, loaded as before");
                Assert.IsFalse(reopened.isDirty);
                Assert.AreEqual("After", reopened.GetRootGameObjects()[0].name, "reloaded from the file on disk");
                Assert.AreEqual(reopened, SceneManager.GetActiveScene(), "still the active scene");
                CollectionAssert.AreEqual(before, OpenScenePaths(), "same scenes in the same order");
            }
            finally
            {
                File.WriteAllBytes(file, original);
                DeleteScratch();
            }
        }

        [Test]
        public void Run_DirtySceneChangedOnDisk_IsRefused_AndNothingRuns()
        {
            var path = CreateScratchScene("Dirty", out var scene);
            var file = Path.GetFullPath(path);
            var original = File.ReadAllBytes(file);
            try
            {
                var edit = new GameObject("Unsaved");
                SceneManager.MoveGameObjectToScene(edit, scene);
                EditorSceneManager.MarkSceneDirty(scene);
                File.WriteAllText(file, File.ReadAllText(file).Replace("m_Name: Before", "m_Name: After"));

                var refreshes = 0;
                var outcome = OpenSceneDiskSync.Run(() => refreshes++);

                Assert.AreEqual(0, refreshes, "a refusal runs nothing");
                Assert.IsTrue(outcome.Refused);
                CollectionAssert.AreEqual(new[] { path }, outcome.Plan.DirtyChanged);
                Assert.AreEqual("open_scene_changed_on_disk", outcome.Refusal().ErrorCode);
                Assert.IsTrue(scene.IsValid() && scene.isDirty, "the scene and its edits are untouched");
            }
            finally
            {
                File.WriteAllBytes(file, original);
                DeleteScratch();
            }
        }

        [Test]
        public void RefuseSnippet_ARefreshingSnippet_WhileAnOpenSceneChangedOnDisk()
        {
            var path = CreateScratchScene("Snippet", out _);
            var file = Path.GetFullPath(path);
            var original = File.ReadAllBytes(file);
            const string refreshing = "{\"code\":\"UnityEditor.AssetDatabase.Refresh(); return 1;\",\"paths_hint\":[\"Assets\"]}";
            const string plain = "{\"code\":\"return 1;\",\"paths_hint\":[\"Assets\"]}";
            try
            {
                Assert.IsNull(OpenSceneDiskSync.RefuseSnippet(
                    EffectiveToolContract.Resolve("unity_open_mcp_execute_csharp", refreshing), refreshing),
                    "nothing changed on disk: the snippet runs");

                File.WriteAllText(file, File.ReadAllText(file).Replace("m_Name: Before", "m_Name: After"));

                var refused = OpenSceneDiskSync.RefuseSnippet(
                    EffectiveToolContract.Resolve("unity_open_mcp_execute_csharp", refreshing), refreshing);
                Assert.IsNotNull(refused, "the snippet's refresh would reimport the open scene");
                Assert.AreEqual("open_scene_changed_on_disk", refused.Mutation.ErrorCode);
                StringAssert.Contains(path, refused.Mutation.ErrorMessage);
                StringAssert.Contains("{\"path\":\"" + path + "\",\"reason\":\"changed\"}", refused.Mutation.ErrorDetailJson);
                StringAssert.Contains("unity_open_mcp_assets_refresh", refused.AgentNextSteps[0]);

                Assert.IsNull(OpenSceneDiskSync.RefuseSnippet(
                    EffectiveToolContract.Resolve("unity_open_mcp_execute_csharp", plain), plain),
                    "a snippet that does not refresh is not held back");
            }
            finally
            {
                File.WriteAllBytes(file, original);
                DeleteScratch();
            }
        }

        [Test]
        public void Run_NothingChanged_JustRefreshes()
        {
            var refreshes = 0;
            var outcome = OpenSceneDiskSync.Run(() => refreshes++);
            Assert.AreEqual(1, refreshes);
            Assert.IsTrue(outcome.Ran);
            Assert.AreEqual("\"reloadedScenes\":[]", outcome.MembersJson());
        }

        // ----- scratch helpers -----

        // A saved scene under the scratch folder, opened additively, holding one
        // root GameObject named "Before". Saved as a copy of the active scene
        // first: Unity refuses NewScene(Additive) next to an untitled scene.
        private static string CreateScratchScene(string name, out Scene scene)
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", Folder.Substring("Assets/".Length));
            var path = Folder + "/" + name + ".unity";
            Assert.IsTrue(EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), path, true), "scratch copy saved");
            scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            foreach (var root in scene.GetRootGameObjects()) Object.DestroyImmediate(root);
            var marker = new GameObject("Before");
            SceneManager.MoveGameObjectToScene(marker, scene);
            Assert.IsTrue(EditorSceneManager.SaveScene(scene), "scratch scene saved");
            return path;
        }

        private static void DeleteScratch()
        {
            for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.path.StartsWith(Folder + "/")) EditorSceneManager.CloseScene(s, true);
            }
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        }

        private static List<string> OpenScenePaths()
        {
            var paths = new List<string>();
            for (var i = 0; i < SceneManager.sceneCount; i++) paths.Add(SceneManager.GetSceneAt(i).path);
            return paths;
        }
    }
}
