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
                CollectionAssert.Contains(knownCodes, issue.IssueCode,
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

        [UnityTest]
        public System.Collections.IEnumerator Scan_BrokenPptrReference_DetectsError()
        {
            var meshPath = FixtureRoot + "/RefMesh.asset";
            var prefabPath = FixtureRoot + "/RefPrefab.prefab";
            yield return CreatePrefabWithMeshReference(prefabPath, meshPath);

            Assume.That(System.IO.File.Exists(meshPath), Is.True,
                "Mesh asset must exist before breaking");

            var backupPath = meshPath + ".bak";
            var backupMeta = meshPath + ".meta.bak";
            System.IO.File.Move(meshPath, backupPath);
            if (System.IO.File.Exists(meshPath + ".meta"))
                System.IO.File.Move(meshPath + ".meta", backupMeta);
            AssetDatabase.Refresh();
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var hasMissingGuid = sink.Any(i => i.IssueCode == "missing_guid");
            Assert.IsTrue(hasMissingGuid,
                $"Expected 'missing_guid' for broken PPtr reference. " +
                $"Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");

            var guidIssue = sink.First(i => i.IssueCode == "missing_guid");
            Assert.AreEqual(VerifySeverity.Error, guidIssue.Severity,
                "Broken PPtr reference must be Error severity");

            System.IO.File.Move(backupPath, meshPath);
            if (System.IO.File.Exists(backupMeta))
                System.IO.File.Move(backupMeta, meshPath + ".meta");
            AssetDatabase.Refresh();

            yield return CleanupFixture(FixtureRoot);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_HealthyPptrReference_ProducesNoMissingGuid()
        {
            var meshPath = FixtureRoot + "/HealthyMesh.asset";
            var prefabPath = FixtureRoot + "/HealthyRefPrefab.prefab";
            yield return CreatePrefabWithMeshReference(prefabPath, meshPath);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var hasMissingGuid = sink.Any(i => i.IssueCode == "missing_guid");
            Assert.IsFalse(hasMissingGuid,
                "Valid PPtr reference must not produce missing_guid. " +
                $"Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");

            yield return CleanupFixture(FixtureRoot);
        }

        static System.Collections.IEnumerator CreatePrefabWithMeshReference(string prefabPath, string meshPath)
        {
            EnsureDirectory(System.IO.Path.GetDirectoryName(prefabPath));

            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
            mesh.triangles = new[] { 0, 1, 2 };
            AssetDatabase.CreateAsset(mesh, meshPath);

            var go = new GameObject("RefTest");
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            Object.DestroyImmediate(go);
            AssetDatabase.Refresh();
            yield return null;
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
