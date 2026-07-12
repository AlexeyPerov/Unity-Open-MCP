using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    // Scene path-identity coverage: set_active / save / unload / get_data
    // resolve opened scenes by asset `path` (path-first), with `name` as a
    // backward-compatible fallback. Also covers the scene_create name-sync
    // hardening — after create to Assets/.../Foo.unity, name-only lookups by
    // the stem "Foo" resolve without a separate open.
    //
    // These tests create real .unity assets under a temp fixture folder and
    // restore the opened-scene stack in teardown so they never leak state into
    // sibling EditMode tests.
    [TestFixture]
    public class ScenesPathIdentityTests
    {
        private const string FixtureRoot = "Assets/Tests/BridgeFixtures/ScenesPathIdentity";

        // Snapshot of the opened-scene paths at SetUp so TearDown can restore
        // the editor to its pre-test stack (close anything we opened additively,
        // without touching scenes the human had open).
        private List<string> _openedBefore;
        private string _activeBefore;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            EnsureDirectory(FixtureRoot);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (AssetDatabase.IsValidFolder(FixtureRoot))
            {
                AssetDatabase.DeleteAsset(FixtureRoot);
                AssetDatabase.Refresh();
            }
        }

        [SetUp]
        public void SetUp()
        {
            _openedBefore = CaptureOpenedPaths();
            _activeBefore = EditorSceneManager.GetActiveScene().path;
        }

        [TearDown]
        public void TearDown()
        {
            // Close every scene we opened that wasn't open at SetUp, then drop
            // any leftover fixture .unity assets. We always keep at least one
            // scene open (Unity refuses an empty scene stack).
            RestoreOpenedScenes(_openedBefore, _activeBefore);
            DeleteFixtureScenes();
        }

        // ----------------------- identity: set_active --------------------

        [Test]
        public void SetActive_UnknownPath_ReturnsSceneNotFound()
        {
            var result = ScenesTools.SetActive(
                "{\"path\":\"Assets/Tests/BridgeFixtures/ScenesPathIdentity/__Nope.unity\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("scene_not_found", result.ErrorCode);
            StringAssert.Contains("__Nope.unity", result.ErrorMessage);
        }

        [Test]
        public void SetActive_ByPath_ResolvesAfterCreateWithoutRelyingOnName()
        {
            var scenePath = CreateAdditiveScene("PathIdent_SetActive", makeDirty: true);
            // Use a path that does NOT match the in-memory name to prove
            // resolution is path-based, not name-based.
            var result = ScenesTools.SetActive($"{{\"path\":\"{scenePath}\"}}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"action\":\"set_active\"", result.Output);
            // The active scene pointer now names our scene.
            StringAssert.Contains("\"activeScene\":\"PathIdent_SetActive\"", result.Output);
        }

        [Test]
        public void SetActive_ByName_AfterCreate_NameSyncWorks()
        {
            // After scene_create, the in-memory name should be synced to the
            // filename stem so name-only set_active resolves.
            var scenePath = CreateAdditiveScene("PathIdent_NameSync", makeDirty: true);
            var result = ScenesTools.SetActive("{\"name\":\"PathIdent_NameSync\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"action\":\"set_active\"", result.Output);
            StringAssert.Contains("\"activeScene\":\"PathIdent_NameSync\"", result.Output);
        }

        [Test]
        public void SetActive_BothPathAndName_PathWinsOverName()
        {
            // Precedence: when both supplied, path resolves; a wrong name must
            // NOT cause failure because path is authoritative.
            var scenePath = CreateAdditiveScene("PathIdent_Prec", makeDirty: true);
            var result = ScenesTools.SetActive(
                "{\"path\":\"" + scenePath + "\",\"name\":\"__WrongName__\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"activeScene\":\"PathIdent_Prec\"", result.Output);
        }

        // ----------------------- identity: save --------------------------

        [Test]
        public void Save_ByPathAsIdentity_SavesBackToOwnPath()
        {
            var scenePath = CreateAdditiveScene("PathIdent_Save", makeDirty: true);
            // `path` matches the opened scene's asset path → identity, not
            // save-as. The scene saves back to its own path.
            var result = ScenesTools.Save($"{{\"path\":\"{scenePath}\"}}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"saved\":true", result.Output);
            // Saved path is the scene's own asset path, not a new destination.
            StringAssert.Contains("\"path\":\"" + scenePath + "\"", result.Output);
        }

        [Test]
        public void Save_ByPathAsIdentity_NotDirty_IsIdempotent()
        {
            var scenePath = CreateAdditiveScene("PathIdent_SaveNoop", makeDirty: false);
            var result = ScenesTools.Save($"{{\"path\":\"{scenePath}\"}}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"saved\":false", result.Output);
        }

        [Test]
        public void Save_SaveAsDestination_DoesNotMatchOpenedScene()
        {
            // `path` does NOT match an opened scene → save-as destination of
            // the active scene. We point it at a fresh fixture path.
            var scenePath = CreateAdditiveScene("PathIdent_SaveAs", makeDirty: true);
            // Make it active so the save-as targets it.
            ScenesTools.SetActive($"{{\"path\":\"{scenePath}\"}}");
            var destPath = FixtureRoot + "/PathIdent_SaveAs_Dest.unity";
            try
            {
                var result = ScenesTools.Save($"{{\"path\":\"{destPath}\"}}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"saved\":true", result.Output);
                StringAssert.Contains("\"path\":\"" + destPath + "\"", result.Output);
            }
            finally
            {
                SafeDeleteAsset(destPath);
            }
        }

        // ----------------------- identity: unload ------------------------

        [Test]
        public void Unload_ByPath_ResolvesAfterCreateWithoutRelyingOnName()
        {
            var scenePath = CreateAdditiveScene("PathIdent_Unload", makeDirty: false);
            var result = ScenesTools.Unload($"{{\"path\":\"{scenePath}\"}}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"action\":\"unloaded\"", result.Output);
        }

        [Test]
        public void Unload_UnknownPath_ReturnsSceneNotFound()
        {
            var result = ScenesTools.Unload(
                "{\"path\":\"Assets/Tests/BridgeFixtures/ScenesPathIdentity/__Nope.unity\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("scene_not_found", result.ErrorCode);
        }

        [Test]
        public void Unload_BothPathAndName_PathWinsOverName()
        {
            var scenePath = CreateAdditiveScene("PathIdent_UnloadPrec", makeDirty: false);
            var result = ScenesTools.Unload(
                "{\"path\":\"" + scenePath + "\",\"name\":\"__WrongName__\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"action\":\"unloaded\"", result.Output);
        }

        // ----------------------- identity: get_data ----------------------

        [Test]
        public void GetData_ByPath_ResolvesOpenedScene()
        {
            var scenePath = CreateAdditiveScene("PathIdent_GetData", makeDirty: true);
            var result = ScenesTools.GetData($"{{\"path\":\"{scenePath}\"}}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"scene\":{", result.Output);
            StringAssert.Contains("PathIdent_GetData", result.Output);
        }

        [Test]
        public void GetData_UnknownPath_ReturnsSceneNotFound()
        {
            var result = ScenesTools.GetData(
                "{\"path\":\"Assets/Tests/BridgeFixtures/ScenesPathIdentity/__Nope.unity\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("scene_not_found", result.ErrorCode);
        }

        [Test]
        public void GetData_BothPathAndName_PathWinsOverName()
        {
            var scenePath = CreateAdditiveScene("PathIdent_GetDataPrec", makeDirty: true);
            var result = ScenesTools.GetData(
                "{\"path\":\"" + scenePath + "\",\"name\":\"__WrongName__\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("PathIdent_GetDataPrec", result.Output);
        }

        // ----------------------- missing-parameter -----------------------

        [Test]
        public void SetActive_NeitherNameNorPath_ReturnsMissingParameter()
        {
            var result = ScenesTools.SetActive("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Unload_NeitherNameNorPath_ReturnsMissingParameter()
        {
            var result = ScenesTools.Unload("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        // ----------------------- duplicate names (path disambiguates) ----

        [Test]
        public void SetActive_DuplicateNames_PathResolvesTheRightOne()
        {
            // Two additive scenes with the same stem in different fixture
            // subfolders. Name-only lookup is ambiguous (returns the first
            // match); path-first lookup must resolve the exact one requested.
            var subA = FixtureRoot + "/DupA";
            var subB = FixtureRoot + "/DupB";
            EnsureDirectory(subA);
            EnsureDirectory(subB);
            var pathA = subA + "/Dup_" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + ".unity";
            var pathB = subB + "/Dup_" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + ".unity";
            ScenesTools.Create("{\"path\":\"" + pathA + "\",\"setup\":\"empty\",\"mode\":\"additive\"}");
            ScenesTools.Create("{\"path\":\"" + pathB + "\",\"setup\":\"empty\",\"mode\":\"additive\"}");

            // Set active by path B — must activate B, not A.
            var result = ScenesTools.SetActive("{\"path\":\"" + pathB + "\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(pathB, EditorSceneManager.GetActiveScene().path,
                "path-first lookup must resolve the exact scene, not the first name match");
            // resolvedBy telemetry confirms path resolution.
            StringAssert.Contains("\"resolvedBy\":\"path\"", result.Output);
        }

        // ----------------------- path normalization ----------------------

        [Test]
        public void SetActive_PathWithBackslashesAndMixedCase_NormalizesAndResolves()
        {
            var scenePath = CreateAdditiveScene("PathIdent_Normalize", makeDirty: false);
            // ResolveOpenedByPath normalizes backslashes → forward slashes and
            // matches case-insensitively. Build a mangled variant of the path.
            var mangled = scenePath.Replace('/', '\\').ToUpperInvariant();
            var result = ScenesTools.SetActive("{\"path\":\"" + mangled + "\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"activeScene\":\"PathIdent_Normalize\"", result.Output);
        }

        // ----------------------- resolvedBy telemetry --------------------

        [Test]
        public void SetActive_ByName_ReportsResolvedByName()
        {
            var scenePath = CreateAdditiveScene("PathIdent_ResolvedByName", makeDirty: false);
            var result = ScenesTools.SetActive("{\"name\":\"PathIdent_ResolvedByName\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"resolvedBy\":\"name\"", result.Output);
        }

        [Test]
        public void Unload_ByPath_ReportsResolvedByPath()
        {
            var scenePath = CreateAdditiveScene("PathIdent_UnloadResolvedBy", makeDirty: false);
            var result = ScenesTools.Unload("{\"path\":\"" + scenePath + "\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"resolvedBy\":\"path\"", result.Output);
        }

        // ----------------------- helpers ---------------------------------

        // Create an additive scene at a unique fixture path, mark it dirty
        // (add a GameObject) when requested, and return its asset path. Uses
        // ScenesTools.Create so the name-sync code path is exercised.
        private string CreateAdditiveScene(string stem, bool makeDirty)
        {
            var scenePath = FixtureRoot + "/" + stem + "_" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + ".unity";
            var result = ScenesTools.Create(
                "{\"path\":\"" + scenePath + "\",\"setup\":\"empty\",\"mode\":\"additive\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            // Verify the create response carries the synced name + path.
            StringAssert.Contains("\"action\":\"created\"", result.Output);

            if (makeDirty)
            {
                // Ensure the new scene is the active scene before adding a GO
                // so the dirty marker lands on it, not on the previous active.
                var setActive = ScenesTools.SetActive($"{{\"path\":\"{scenePath}\"}}");
                Assert.IsTrue(setActive.Success, setActive.ErrorMessage);
                var go = new GameObject("__MCPTest_DirtyMarker_" + stem);
                Assert.IsTrue(EditorSceneManager.GetSceneByPath(scenePath).isDirty,
                    "expected scene to be dirty after adding a GameObject");
            }
            return scenePath;
        }

        private static List<string> CaptureOpenedPaths()
        {
            var paths = new List<string>(SceneManager.sceneCount);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) paths.Add(s.path);
            }
            return paths;
        }

        // Close any scene opened during the test that wasn't in the SetUp
        // snapshot, then restore the original active scene. Always keeps the
        // scene stack non-empty.
        private static void RestoreOpenedScenes(List<string> openedBefore, string activeBefore)
        {
            // Close scenes not in the snapshot.
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                if (!openedBefore.Contains(s.path))
                {
                    if (SceneManager.sceneCount <= 1) break;
                    EditorSceneManager.CloseScene(s, true);
                }
            }
            // Re-open any scene from the snapshot that got closed (defensive).
            foreach (var path in openedBefore)
            {
                if (string.IsNullOrEmpty(path)) continue;
                bool stillOpen = false;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    if (SceneManager.GetSceneAt(i).path == path) { stillOpen = true; break; }
                }
                if (!stillOpen && File.Exists(System.IO.Path.GetFullPath(path)))
                    EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            }
            // Restore the original active scene if it's still open.
            if (!string.IsNullOrEmpty(activeBefore))
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.isLoaded && s.path == activeBefore)
                    {
                        EditorSceneManager.SetActiveScene(s);
                        break;
                    }
                }
            }
        }

        private void DeleteFixtureScenes()
        {
            if (!AssetDatabase.IsValidFolder(FixtureRoot)) return;
            var guids = AssetDatabase.FindAssets("t:Scene", new[] { FixtureRoot });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                SafeDeleteAsset(path);
            }
        }

        private static void SafeDeleteAsset(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                AssetDatabase.DeleteAsset(path);
        }

        private static void EnsureDirectory(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            var lastSlash = assetPath.LastIndexOf('/');
            if (lastSlash <= 0) return;
            var parent = assetPath.Substring(0, lastSlash);
            var name = assetPath.Substring(lastSlash + 1);
            EnsureDirectory(parent);
            if (!AssetDatabase.IsValidFolder(parent)) return;
            if (!AssetDatabase.IsValidFolder(assetPath))
                AssetDatabase.CreateFolder(parent, name);
        }
    }
}
