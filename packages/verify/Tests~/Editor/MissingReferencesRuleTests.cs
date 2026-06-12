using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityAgentVerify.Tests
{
    [TestFixture]
    public class MissingReferencesRuleTests
    {
        const string FixtureRoot = "Assets/Tests/VerifyFixtures/MissingReferences";

        MissingReferencesRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new MissingReferencesRule();
        }

        [Test]
        public void Id_IsCorrect()
        {
            Assert.AreEqual("missing_references", rule.Id);
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
        public System.Collections.IEnumerator Scan_ExistingPrefab_CheckpointMode_ProducesKeysOnly()
        {
            yield return CreateMinimalPrefab(FixtureRoot + "/TestPrefab.prefab");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { FixtureRoot + "/TestPrefab.prefab" });

            rule.Scan(scope, VerifyRunMode.Checkpoint, sink);

            foreach (var issue in sink)
            {
                Assert.AreEqual("missing_references", issue.RuleId);
                var key = IssueKey.Build(issue);
                Assert.IsTrue(IssueKey.TryParse(key, out _, out _, out _, out _),
                    $"Issue key '{key}' must be valid");
            }

            yield return CleanupFixture(FixtureRoot);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_ExistingPrefab_ValidateMode_ProducesFullIssues()
        {
            yield return CreateMinimalPrefab(FixtureRoot + "/TestPrefab.prefab");

            var sinkCheckpoint = new List<VerifyIssue>();
            var sinkValidate = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { FixtureRoot + "/TestPrefab.prefab" });

            rule.Scan(scope, VerifyRunMode.Checkpoint, sinkCheckpoint);
            rule.Scan(scope, VerifyRunMode.Validate, sinkValidate);

            Assert.IsTrue(sinkValidate.Count >= sinkCheckpoint.Count,
                "Validate mode should find at least as many issues as Checkpoint (fullScan=true)");

            yield return CleanupFixture(FixtureRoot);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_AllIssues_HaveCorrectRuleId()
        {
            yield return CreateMinimalPrefab(FixtureRoot + "/TestPrefab.prefab");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { FixtureRoot + "/TestPrefab.prefab" });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            foreach (var issue in sink)
            {
                Assert.AreEqual("missing_references", issue.RuleId);
            }

            yield return CleanupFixture(FixtureRoot);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_IssuesUseKnownCodes()
        {
            yield return CreateMinimalPrefab(FixtureRoot + "/TestPrefab.prefab");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { FixtureRoot + "/TestPrefab.prefab" });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            var knownCodes = new HashSet<string>
            {
                "missing_fileid_and_guid", "missing_guid", "missing_fileid",
                "missing_local_fileid", "empty_local_ref", "missing_method",
                "type_mismatch", "missing_script", "duplicate_component", "invalid_layer"
            };

            foreach (var issue in sink)
            {
                Assert.Contains(issue.IssueCode, knownCodes,
                    $"Issue code '{issue.IssueCode}' must be a known MissingReferences code");
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

        static System.Collections.IEnumerator CreateMinimalPrefab(string path)
        {
            EnsureDirectory(System.IO.Path.GetDirectoryName(path));
            var go = new GameObject("VerifyTestFixture");
#if UNITY_EDITOR
            PrefabUtility.SaveAsPrefabAsset(go, path);
#endif
            Object.DestroyImmediate(go);
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
