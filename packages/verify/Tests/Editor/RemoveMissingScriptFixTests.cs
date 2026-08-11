using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Fixes;

namespace UnityOpenMcpVerify.Tests
{
    // M30-polish Plan 2 T2.1 — RemoveMissingScriptFix.FixScene must open the
    // scene additively and close it in a finally block, mirroring
    // ScenePrefabHealth/Scanner.cs. A scene that was NOT already open must be
    // closed after the fix (scene count restored); a scene that WAS already
    // open must stay open.
    [TestFixture]
    public class RemoveMissingScriptFixTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/RemoveMissingScript";

        private RemoveMissingScriptFix fix;

        [SetUp]
        public void SetUp()
        {
            fix = new RemoveMissingScriptFix();
        }

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

        // -------------------------------------------------------------------
        // FixId / CanFix — pure, fast
        // -------------------------------------------------------------------

        [Test]
        public void FixId_IsRemoveMissingScript()
        {
            Assert.AreEqual("remove_missing_script", fix.FixId);
        }

        [Test]
        public void CanFix_MissingScriptIssue_ReturnsTrue()
        {
            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/A.prefab", "missing_script");

            Assert.IsTrue(fix.CanFix(issueId));
        }

        [Test]
        public void CanFix_MissingGuidIssue_ReturnsFalse()
        {
            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/A.prefab", "missing_guid");

            Assert.IsFalse(fix.CanFix(issueId));
        }

        // -------------------------------------------------------------------
        // FixScene — scene lifecycle (the T2.1 fix)
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator FixScene_NotAlreadyOpen_ClosesSceneAfterFix()
        {
            var scenePath = FixtureRoot + "/TestCloseScene.unity";
            yield return CreateEmptyScene(scenePath);

            // Make sure the scene is NOT open before the fix.
            CloseSceneIfOpen(scenePath);
            yield return null;

            int sceneCountBefore = SceneManager.sceneCount;

            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                scenePath, "missing_script");

            var result = fix.Apply(issueId);

            Assert.IsTrue(result.Success,
                $"Fix should succeed. Got: {result.Description}");

            // The scene was not open before — it must be closed after the fix.
            int sceneCountAfter = SceneManager.sceneCount;
            Assert.AreEqual(sceneCountBefore, sceneCountAfter,
                "scene count must be unchanged (the scene we opened was closed)");

            Assert.IsFalse(IsSceneOpen(scenePath),
                "the scene must NOT be open after the fix (we opened it, we close it)");
        }

        [UnityTest]
        public System.Collections.IEnumerator FixScene_AlreadyOpen_KeepsSceneOpenAfterFix()
        {
            var scenePath = FixtureRoot + "/TestKeepOpenScene.unity";
            yield return CreateEmptyScene(scenePath);

            // Open the scene additively before the fix — simulating a scene the
            // user already had open.
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            yield return null;

            Assume.That(IsSceneOpen(scenePath), Is.True,
                "scene must be open before the fix");

            int sceneCountBefore = SceneManager.sceneCount;

            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                scenePath, "missing_script");

            var result = fix.Apply(issueId);

            Assert.IsTrue(result.Success,
                $"Fix should succeed. Got: {result.Description}");

            // The scene was already open — it must stay open after the fix.
            int sceneCountAfter = SceneManager.sceneCount;
            Assert.AreEqual(sceneCountBefore, sceneCountAfter,
                "scene count must be unchanged (the scene was already open)");

            Assert.IsTrue(IsSceneOpen(scenePath),
                "the scene must still be open after the fix (it was open before)");

            // Clean up: close the scene we opened.
            CloseSceneIfOpen(scenePath);
            yield return null;
        }

        // -------------------------------------------------------------------
        // FindOpenScene — case-insensitive open-scene resolution (regression)
        // -------------------------------------------------------------------

        // The regression: an exact-match `==` + GetSceneByPath lookup failed to
        // recognise an already-open scene when assetPath's casing differed from
        // Unity's canonical recorded path (macOS/Windows case-insensitive FS).
        // wasOpen was then false → a duplicate additive copy was opened, saved
        // (overwriting disk), and closed, leaving the originally-open scene
        // stale so the user's next save reverted the fix. FindOpenScene compares
        // OrdinalIgnoreCase so a case-mismatched query still resolves to the
        // loaded handle. Tested platform-independently: we control the query
        // string, the scene is genuinely loaded under its canonical path.
        [UnityTest]
        public System.Collections.IEnumerator FindOpenScene_MatchesCaseMismatchedPath()
        {
            var scenePath = FixtureRoot + "/CaseMismatch.unity";
            yield return CreateEmptyScene(scenePath);

            try
            {
                Assume.That(IsSceneOpen(scenePath), Is.True,
                    "fixture scene must be loaded under its canonical path");

                // Canonical query — always matches.
                var canonical = RemoveMissingScriptFix.FindOpenScene(scenePath);
                Assert.IsTrue(canonical.IsValid(),
                    "FindOpenScene must match the canonical path");

                // Case-mismatched query — the regression root cause. Exact-match
                // GetSceneByPath fails (proof below); FindOpenScene must still
                // resolve to the same loaded handle.
                var mismatched = scenePath.ToUpperInvariant();
                Assume.That(mismatched, Is.Not.EqualTo(scenePath),
                    "uppercased path must differ from canonical for the test to be meaningful");

                var resolved = RemoveMissingScriptFix.FindOpenScene(mismatched);
                Assert.IsTrue(resolved.IsValid(),
                    "FindOpenScene must match a case-mismatched path (OrdinalIgnoreCase)");
                Assert.AreEqual(canonical.path, resolved.path,
                    "resolved handle must be the same loaded scene");

                // Proof that the OLD exact-match lookup disagrees — this is the
                // call that returned an invalid scene on a case mismatch and
                // caused the stale-revert. GetSceneByPath is exact-match only.
                var exact = SceneManager.GetSceneByPath(mismatched);
                Assert.IsFalse(exact.IsValid(),
                    "GetSceneByPath is exact-match: a case mismatch must NOT resolve " +
                    "(this is why FixScene now uses FindOpenScene instead)");
            }
            finally
            {
                CloseSceneIfOpen(scenePath);
                if (AssetDatabase.LoadMainAssetAtPath(scenePath) != null)
                    AssetDatabase.DeleteAsset(scenePath);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void FindOpenScene_NonUnityAsset_ReturnsInvalid()
        {
            // The extension guard short-circuits before scanning loaded scenes.
            var resolved = RemoveMissingScriptFix.FindOpenScene("Assets/A.prefab");
            Assert.IsFalse(resolved.IsValid(),
                "FindOpenScene must only resolve .unity assets");
        }

        [Test]
        public void FindOpenScene_NotLoadedPath_ReturnsInvalid()
        {
            var resolved = RemoveMissingScriptFix.FindOpenScene(
                FixtureRoot + "/DoesNotExist.unity");
            Assert.IsFalse(resolved.IsValid(),
                "FindOpenScene must return invalid for a path that is not loaded");
        }

        // -------------------------------------------------------------------
        // Fixture helpers
        // -------------------------------------------------------------------

        private static System.Collections.IEnumerator CreateEmptyScene(string path)
        {
            EnsureDirectory(Path.GetDirectoryName(path));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, path);
            yield return null;
        }

        private static bool IsSceneOpen(string path)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).path == path)
                    return true;
            }
            return false;
        }

        private static void CloseSceneIfOpen(string path)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.path == path)
                {
                    EditorSceneManager.CloseScene(scene, true);
                    return;
                }
            }
        }

        private static void EnsureDirectory(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                var parent = Path.GetDirectoryName(path);
                var name = Path.GetFileName(path);
                if (!AssetDatabase.IsValidFolder(parent))
                    EnsureDirectory(parent);
                AssetDatabase.CreateFolder(parent, name);
            }
        }
    }
}
