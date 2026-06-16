using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Rules;

namespace UnityOpenMcpVerify.Tests
{
    // TEMPORARILY DISABLED (heavy) — re-enable as part of T17.3 (EditMode
    // test-suite speed-up, specs/execution/M17/execution-plan-3-editmode-test-perf.md).
    // [UnityTest] coroutines do scene create/save + multiple
    // AssetDatabase.Refresh() calls per test. [Explicit] excludes from suite
    // runs until optimized; still runnable by name.
    [Explicit]
    [TestFixture]
    public class ScenePrefabHealthRuleTests
    {
        const string FixtureRoot = "Assets/Tests/VerifyFixtures/ScenePrefabHealth";

        ScenePrefabHealthRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new ScenePrefabHealthRule();
        }

        [Test]
        public void Id_IsCorrect()
        {
            Assert.AreEqual("scene_prefab_health", rule.Id);
        }

        [Test]
        public void Scan_EmptyPaths_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new string[0]);

            rule.Scan(scope, VerifyRunMode.Checkpoint, sink);

            Assert.AreEqual(0, sink.Count);
        }

        [Test]
        public void Scan_NullPaths_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(null);

            rule.Scan(scope, VerifyRunMode.Checkpoint, sink);

            Assert.AreEqual(0, sink.Count);
        }

        [Test]
        public void Scan_NonexistentPath_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Assets/Nonexistent99999.prefab" });

            rule.Scan(scope, VerifyRunMode.Checkpoint, sink);

            Assert.AreEqual(0, sink.Count);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_Prefab_CheckpointMode_ProducesKeys()
        {
            yield return CreateMinimalPrefab(FixtureRoot + "/TestPrefab.prefab");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { FixtureRoot + "/TestPrefab.prefab" });

            rule.Scan(scope, VerifyRunMode.Checkpoint, sink);

            foreach (var issue in sink)
            {
                Assert.AreEqual("scene_prefab_health", issue.RuleId);
                var key = IssueKey.Build(issue);
                Assert.IsTrue(IssueKey.TryParse(key, out _, out _, out _, out _),
                    $"Issue key '{key}' must be valid");
            }

            yield return CleanupFixture(FixtureRoot);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_Prefab_IssuesUseKnownCodes()
        {
            yield return CreateMinimalPrefab(FixtureRoot + "/TestPrefab.prefab");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { FixtureRoot + "/TestPrefab.prefab" });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            var knownCodes = new HashSet<string>
            {
                "broken_reference", "high_risk_bootstrap", "scene_object_count",
                "component_hotspot", "inactive_expensive", "inactive_heavy",
                "deep_nesting", "override_explosion"
            };

            foreach (var issue in sink)
            {
                CollectionAssert.Contains(knownCodes, issue.IssueCode,
                    $"Issue code '{issue.IssueCode}' must be a known ScenePrefabHealth code");
            }

            yield return CleanupFixture(FixtureRoot);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_Scene_IssuesMatchAssetPath()
        {
            yield return CreateMinimalScene(FixtureRoot + "/TestScene.unity");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { FixtureRoot + "/TestScene.unity" });

            rule.Scan(scope, VerifyRunMode.Validate, sink);

            foreach (var issue in sink)
            {
                Assert.AreEqual(FixtureRoot + "/TestScene.unity", issue.AssetPath);
            }

            yield return CleanupFixture(FixtureRoot);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_SeveritiesAreErrorOrWarning()
        {
            yield return CreateMinimalPrefab(FixtureRoot + "/TestPrefab.prefab");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { FixtureRoot + "/TestPrefab.prefab" });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            foreach (var issue in sink)
            {
                Assert.IsTrue(
                    issue.Severity == VerifySeverity.Error || issue.Severity == VerifySeverity.Warning,
                    $"Severity must be Error or Warning, got {issue.Severity}");
            }

            yield return CleanupFixture(FixtureRoot);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_PackagesPath_Skipped()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Packages/com.unity.render-pipelines.core/Test.prefab" });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            Assert.AreEqual(0, sink.Count);
            yield return null;
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_LibraryPath_Skipped()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Library/cache/something.prefab" });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            Assert.AreEqual(0, sink.Count);
            yield return null;
        }

        static System.Collections.IEnumerator CreateMinimalPrefab(string path)
        {
            EnsureDirectory(System.IO.Path.GetDirectoryName(path));
            var go = new GameObject("VerifyTestPrefab");
#if UNITY_EDITOR
            PrefabUtility.SaveAsPrefabAsset(go, path);
#endif
            Object.DestroyImmediate(go);
            AssetDatabase.Refresh();
            yield return null;
        }

        static System.Collections.IEnumerator CreateMinimalScene(string path)
        {
            EnsureDirectory(System.IO.Path.GetDirectoryName(path));
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Additive);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, path);
            if (UnityEngine.SceneManagement.SceneManager.sceneCount > 1)
                UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.Refresh();
            yield return null;
        }

        static System.Collections.IEnumerator CleanupFixture(string root)
        {
            if (AssetDatabase.IsValidFolder(root))
            {
                AssetDatabase.DeleteAsset(root);
                AssetDatabase.Refresh();
            }
            yield return null;
        }

        static void EnsureDirectory(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                var parent = System.IO.Path.GetDirectoryName(path);
                var name = System.IO.Path.GetFileName(path);
                if (!AssetDatabase.IsValidFolder(parent))
                    EnsureDirectory(parent);
                AssetDatabase.CreateFolder(parent, name);
            }
        }
    }
}
