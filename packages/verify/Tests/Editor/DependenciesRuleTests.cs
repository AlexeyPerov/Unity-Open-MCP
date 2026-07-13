using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Rules;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class DependenciesRuleTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/Dependencies";

        private DependenciesRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new DependenciesRule();
        }

        // Shared fixture folder lifetime: created once for the fixture,
        // reaped once at teardown. Previously each [UnityTest] created +
        // deleted the folder with a Refresh() on both sides, dominating
        // the runtime for this file.
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

        [Test]
        public void Id_IsCorrect()
        {
            Assert.AreEqual("dependencies", rule.Id);
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
            var scope = new VerifyScope(new[] { "Assets/Nonexistent12345.prefab" });

            rule.Scan(scope, VerifyRunMode.Checkpoint, sink);

            Assert.AreEqual(0, sink.Count);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_PackagesPath_Skipped()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Packages/com.unity.render-pipelines.core/SomeFile.prefab" });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            Assert.AreEqual(0, sink.Count);
            yield return null;
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_AllIssues_HaveCorrectRuleId()
        {
            var prefabPath = FixtureRoot + "/HealthyRef.prefab";
            yield return CreateMinimalPrefab(prefabPath);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            foreach (var issue in sink)
            {
                Assert.AreEqual("dependencies", issue.RuleId);
                Assert.AreEqual(prefabPath, issue.AssetPath);
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_HealthyPrefab_ProducesNoBrokenDependency()
        {
            var prefabPath = FixtureRoot + "/HealthyRef.prefab";
            yield return CreateMinimalPrefab(prefabPath);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            var broken = sink.Where(i => i.IssueCode.StartsWith("broken_dependency")).ToList();
            Assert.AreEqual(0, broken.Count,
                $"Healthy prefab must not produce broken_dependency. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_BrokenGuidEdge_ReportsBrokenDependency()
        {
            // Construct a prefab whose PPtr edge points at a GUID that does not resolve,
            // by injecting a fake external-guid reference into a freshly saved prefab.
            var prefabPath = FixtureRoot + "/BrokenDep.prefab";
            yield return CreateMinimalPrefab(prefabPath);

            InjectBrokenGuidReference(prefabPath, "1234567890abcdef1234567890abcdef");
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            var broken = sink.FirstOrDefault(i => i.IssueCode.StartsWith("broken_dependency"));
            Assert.IsNotNull(broken,
                $"Expected 'broken_dependency' for unresolved forward edge. " +
                $"Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Error, broken.Severity,
                "broken_dependency must be Error severity");
            Assert.AreEqual("dependencies", broken.RuleId);
            Assert.AreEqual(prefabPath, broken.AssetPath);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_IssuesUseKnownCodes()
        {
            var prefabPath = FixtureRoot + "/KnownCodes.prefab";
            yield return CreateMinimalPrefab(prefabPath);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            var knownCodes = new HashSet<string> { "broken_dependency", "dependency_cycle" };
            foreach (var issue in sink)
            {
                CollectionAssert.Contains(knownCodes, issue.IssueCode,
                    $"Issue code '{issue.IssueCode}' must be a known Dependencies code");
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_SeveritiesAreErrorOrWarning()
        {
            var prefabPath = FixtureRoot + "/Sev.prefab";
            yield return CreateMinimalPrefab(prefabPath);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            foreach (var issue in sink)
            {
                Assert.IsTrue(
                    issue.Severity == VerifySeverity.Error || issue.Severity == VerifySeverity.Warning,
                    $"Severity must be Error or Warning, got {issue.Severity}");
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_IssuesProduceValidKeys()
        {
            var prefabPath = FixtureRoot + "/Keys.prefab";
            yield return CreateMinimalPrefab(prefabPath);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            foreach (var issue in sink)
            {
                var key = IssueKey.Build(issue);
                Assert.IsTrue(IssueKey.TryParse(key, out _, out _, out _, out _),
                    $"Issue key '{key}' must be valid");
            }
        }

        // -------------------------------------------------------------------
        // Fixture helpers
        // -------------------------------------------------------------------

        private static System.Collections.IEnumerator CreateMinimalPrefab(string path)
        {
            EnsureDirectory(Path.GetDirectoryName(path));
            var go = new GameObject("VerifyDepsFixture");
#if UNITY_EDITOR
            PrefabUtility.SaveAsPrefabAsset(go, path);
#endif
            Object.DestroyImmediate(go);
            AssetDatabase.Refresh();
            yield return null;
        }

        // Injects an external-guid PPtr edge into a freshly saved prefab YAML
        // pointing at a GUID that does not resolve, so the dependencies rule sees
        // a declared edge whose target asset does not load.
        private static void InjectBrokenGuidReference(string prefabPath, string fakeGuid)
        {
            var yaml = File.ReadAllText(prefabPath);

            // Append a MonoBehaviour-less GameObject reference line carrying an
            // external guid. The dependencies scanner reads `guid: <hex>` declarations
            // regardless of which Unity property holds them, so a bare external edge
            // is enough to model a broken forward dependency.
            const string marker = "  m_Children: []";
            var edge = "  m_Children: []\n" +
                       "  m_BrokenDep:\n" +
                       "    {fileID: 9999, guid: " + fakeGuid + ", type: 3}";

            if (yaml.Contains(marker))
                yaml = new Regex(Regex.Escape(marker)).Replace(yaml, edge, 1);

            File.WriteAllText(prefabPath, yaml);
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
