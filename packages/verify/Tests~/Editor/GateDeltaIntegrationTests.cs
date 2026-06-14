using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class GateDeltaIntegrationTests
    {
        const string FixtureRoot = "Assets/Tests/GateDeltaFixtures";
        const string HealthyPrefab = FixtureRoot + "/DeltaTest.prefab";
        const string BrokenPrefab = FixtureRoot + "/DeltaTest.prefab";

        [UnityTest]
        public System.Collections.IEnumerator Delta_DetectsNewMissingReferences_AfterBadEdit()
        {
            yield return CreateHealthyPrefab(HealthyPrefab);

            var scope = new VerifyScope(new[] { HealthyPrefab });
            var ruleIds = new[] { "missing_references" };

            var checkpoint = VerifyRunner.CreateCheckpoint(scope, ruleIds);
            Assert.IsNotNull(checkpoint.CheckpointId);
            Assert.IsTrue(checkpoint.CheckpointId.StartsWith("cp_"));

            var preValidate = VerifyRunner.RunScoped(scope, ruleIds, VerifyRunMode.Validate);
            var preDelta = ComputeSimpleDelta(checkpoint, preValidate);
            Assert.AreEqual(0, preDelta.newErrors,
                "Healthy prefab should have no new errors vs its own checkpoint");

            yield return BreakPrefab(HealthyPrefab);

            var postValidate = VerifyRunner.RunScoped(scope, ruleIds, VerifyRunMode.Validate);
            var postDelta = ComputeSimpleDelta(checkpoint, postValidate);

            Assert.Greater(postDelta.newErrors, 0,
                $"Delta must detect new errors after breaking the prefab. " +
                $"New errors: {postDelta.newErrors}, new warnings: {postDelta.newWarnings}");

            Assert.Greater(postDelta.newKeys.Count, 0,
                "Delta must contain at least one new issue key");

            foreach (var key in postDelta.newKeys)
            {
                Assert.IsTrue(IssueKey.TryParse(key, out var ruleId, out _, out var path, out _),
                    $"New issue key '{key}' must be parseable");
                Assert.AreEqual("missing_references", ruleId,
                    $"Expected missing_references rule, got '{ruleId}'");
                Assert.AreEqual(HealthyPrefab, path,
                    $"Issue should reference the broken prefab path");
            }

            yield return CleanupFixture(FixtureRoot);
        }

        [UnityTest]
        public System.Collections.IEnumerator Delta_DetectsResolvedErrors_AfterFix()
        {
            yield return CreateHealthyPrefab(HealthyPrefab);

            var scope = new VerifyScope(new[] { HealthyPrefab });
            var ruleIds = new[] { "missing_references" };

            var checkpoint = VerifyRunner.CreateCheckpoint(scope, ruleIds);

            yield return BreakPrefab(HealthyPrefab);

            var brokenValidate = VerifyRunner.RunScoped(scope, ruleIds, VerifyRunMode.Validate);
            var brokenDelta = ComputeSimpleDelta(checkpoint, brokenValidate);
            Assume.That(brokenDelta.newErrors, Is.GreaterThan(0),
                "Prefab should be broken before fix attempt");

            yield return RestorePrefab(HealthyPrefab);

            var fixedValidate = VerifyRunner.RunScoped(scope, ruleIds, VerifyRunMode.Validate);
            var fixedDelta = ComputeSimpleDelta(checkpoint, fixedValidate);

            Assert.AreEqual(0, fixedDelta.newErrors,
                "After restoring, there should be no new errors vs the original checkpoint");

            yield return CleanupFixture(FixtureRoot);
        }

        [UnityTest]
        public System.Collections.IEnumerator Delta_BothRules_OnBrokenFixture()
        {
            const string srcPath = "Assets/Fixtures/BrokenRefFixture.prefab";
            Assume.That(System.IO.File.Exists(srcPath), Is.True,
                $"Fixture missing: {srcPath}");

            yield return null;

            var scope = new VerifyScope(new[] { srcPath });
            var ruleIds = new[] { "missing_references", "scene_prefab_health" };

            var checkpoint = VerifyRunner.CreateCheckpoint(scope, ruleIds);
            Assert.AreEqual(2, checkpoint.Fingerprints.Count,
                "Both rules should produce fingerprints");

            var postValidate = VerifyRunner.RunScoped(scope, ruleIds, VerifyRunMode.Validate);
            var delta = ComputeSimpleDelta(checkpoint, postValidate);

            Assert.Greater(delta.newErrors + delta.newWarnings, 0,
                "BrokenRefFixture should produce issues in at least one rule");

            yield return null;
        }

        static (int newErrors, int newWarnings, HashSet<string> newKeys) ComputeSimpleDelta(
            CheckpointFingerprint before, VerifyResult after)
        {
            var beforeKeys = new HashSet<string>();
            foreach (var fp in before.Fingerprints.Values)
                foreach (var key in fp.IssueKeys)
                    beforeKeys.Add(key);

            var afterKeys = new HashSet<string>();
            foreach (var issue in after.Issues)
                afterKeys.Add(IssueKey.Build(issue));

            var newKeys = new HashSet<string>(afterKeys);
            newKeys.ExceptWith(beforeKeys);

            int newErrors = 0, newWarnings = 0;
            foreach (var issue in after.Issues)
            {
                if (newKeys.Contains(IssueKey.Build(issue)))
                {
                    if (issue.Severity == VerifySeverity.Error) newErrors++;
                    else newWarnings++;
                }
            }

            return (newErrors, newWarnings, newKeys);
        }

        static System.Collections.IEnumerator CreateHealthyPrefab(string path)
        {
            EnsureDirectory(System.IO.Path.GetDirectoryName(path));
            var go = new GameObject("DeltaTest");
            var mf = go.AddComponent<MeshFilter>();
            var mesh = new Mesh();
            AssetDatabase.CreateAsset(mesh, System.IO.Path.ChangeExtension(path, ".asset"));
            mf.sharedMesh = mesh;
            PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            AssetDatabase.Refresh();
            yield return null;
        }

        static System.Collections.IEnumerator BreakPrefab(string prefabPath)
        {
            var meshPath = System.IO.Path.ChangeExtension(prefabPath, ".asset");
            Assume.That(System.IO.File.Exists(meshPath), Is.True,
                $"Mesh asset must exist to break: {meshPath}");
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.Refresh();
            yield return null;
        }

        static System.Collections.IEnumerator RestorePrefab(string prefabPath)
        {
            var meshPath = System.IO.Path.ChangeExtension(prefabPath, ".asset");
            if (System.IO.File.Exists(meshPath))
                yield break;

            var mesh = new Mesh();
            AssetDatabase.CreateAsset(mesh, meshPath);
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
