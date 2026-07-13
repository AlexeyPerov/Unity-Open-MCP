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
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/MissingReferences";

        private MissingReferencesRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new MissingReferencesRule();
        }

        // Shared fixture folder lifetime: created once for the whole fixture,
        // torn down once at the end. Per-test create/destroy of the folder was
        // the dominant cost driver (each [UnityTest] created + deleted it with
        // a full AssetDatabase.Refresh() on both sides). Individual assets are
        // recreated inside each test as needed; the folder simply outlives the
        // test and is reaped here.
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
            var prefabPath = FixtureRoot + "/TestPrefab.prefab";
            yield return CreateMinimalPrefab(prefabPath);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });

            rule.Scan(scope, VerifyRunMode.Checkpoint, sink);

            foreach (var issue in sink)
            {
                Assert.AreEqual("missing_references", issue.RuleId);
                var key = IssueKey.Build(issue);
                Assert.IsTrue(IssueKey.TryParse(key, out _, out _, out _, out _),
                    $"Issue key '{key}' must be valid");
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_ExistingPrefab_ValidateMode_ProducesFullIssues()
        {
            var prefabPath = FixtureRoot + "/TestPrefab.prefab";
            yield return CreateMinimalPrefab(prefabPath);

            var sinkCheckpoint = new List<VerifyIssue>();
            var sinkValidate = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });

            rule.Scan(scope, VerifyRunMode.Checkpoint, sinkCheckpoint);
            rule.Scan(scope, VerifyRunMode.Validate, sinkValidate);

            Assert.IsTrue(sinkValidate.Count >= sinkCheckpoint.Count,
                "Validate mode should find at least as many issues as Checkpoint (fullScan=true)");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_AllIssues_HaveCorrectRuleId()
        {
            var prefabPath = FixtureRoot + "/TestPrefab.prefab";
            yield return CreateMinimalPrefab(prefabPath);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            foreach (var issue in sink)
            {
                Assert.AreEqual("missing_references", issue.RuleId);
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_IssuesUseKnownCodes()
        {
            var prefabPath = FixtureRoot + "/TestPrefab.prefab";
            yield return CreateMinimalPrefab(prefabPath);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            var knownCodes = new HashSet<string>
            {
                "missing_fileid_and_guid", "missing_guid", "missing_fileid",
                "missing_local_fileid", "empty_local_ref", "missing_method",
                "type_mismatch", "missing_script", "duplicate_component", "invalid_layer"
            };

            foreach (var issue in sink)
            {
                var bareCode = IssueKey.BareIssueCode(issue.IssueCode);
                CollectionAssert.Contains(knownCodes, bareCode,
                    $"Issue code '{issue.IssueCode}' (bare: '{bareCode}') must be a known MissingReferences code");
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_SeveritiesAreErrorOrWarning()
        {
            var prefabPath = FixtureRoot + "/TestPrefab.prefab";
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
        public System.Collections.IEnumerator Scan_BrokenPptrReference_DetectsError()
        {
            var meshPath = FixtureRoot + "/RefMesh.asset";
            var prefabPath = FixtureRoot + "/RefPrefab.prefab";
            yield return CreatePrefabWithMeshReference(prefabPath, meshPath);

            Assume.That(System.IO.File.Exists(meshPath), Is.True,
                "Mesh asset must exist before breaking");

            var backupPath = meshPath + ".bak";
            var backupMeta = meshPath + ".meta.bak";
            // Clear stale .bak from a prior failed run so File.Move below
            // doesn't collide ("already exists").
            if (System.IO.File.Exists(backupPath)) System.IO.File.Delete(backupPath);
            if (System.IO.File.Exists(backupMeta)) System.IO.File.Delete(backupMeta);
            System.IO.File.Move(meshPath, backupPath);
            if (System.IO.File.Exists(meshPath + ".meta"))
                System.IO.File.Move(meshPath + ".meta", backupMeta);
            AssetDatabase.Refresh();
            yield return null;

            try
            {
                var sink = new List<VerifyIssue>();
                var scope = new VerifyScope(new[] { prefabPath });
                rule.Scan(scope, VerifyRunMode.Full, sink);

                var hasMissingGuid = sink.Any(i => i.IssueCode.StartsWith("missing_guid"));
                Assert.IsTrue(hasMissingGuid,
                    $"Expected 'missing_guid' for broken PPtr reference. " +
                    $"Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");

                var guidIssue = sink.First(i => i.IssueCode.StartsWith("missing_guid"));
                Assert.AreEqual(VerifySeverity.Error, guidIssue.Severity,
                    "Broken PPtr reference must be Error severity");
            }
            finally
            {
                // Always restore so the shared folder stays clean for the next
                // test; do not depend on the test's own cleanup branch running.
                System.IO.File.Move(backupPath, meshPath);
                if (System.IO.File.Exists(backupMeta))
                    System.IO.File.Move(backupMeta, meshPath + ".meta");
                AssetDatabase.Refresh();
            }
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

            var hasMissingGuid = sink.Any(i => i.IssueCode.StartsWith("missing_guid"));
            Assert.IsFalse(hasMissingGuid,
                "Valid PPtr reference must not produce missing_guid. " +
                $"Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        // CreateAsset/SaveAsPrefabAsset already trigger an asset-database
        // refresh internally; the explicit AssetDatabase.Refresh() here only
        // forces the import pipeline to settle before the test body reads the
        // asset back. One Refresh() per create (down from helper + body +
        // cleanup = ~4 per test) is enough.
        private static System.Collections.IEnumerator CreatePrefabWithMeshReference(string prefabPath, string meshPath)
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

        private static System.Collections.IEnumerator CreateMinimalPrefab(string path)
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

        private static void EnsureDirectory(string path)
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
