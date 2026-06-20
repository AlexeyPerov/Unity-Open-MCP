using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class GateDeltaIntegrationTests
    {
        const string FixtureRoot = "Assets/Tests/GateDeltaFixtures";
        const string HealthyPrefab = FixtureRoot + "/DeltaTest.prefab";

        // The fixture folder outlives all tests in this class; each test still
        // (re)creates the prefab it scans because the .bak-swap tests mutate
        // the same path. Hoisting the folder create/destroy avoids the per-test
        // create+Refresh()/delete+Refresh() pair that dominated the runtime.
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            EnsureRulesRegistered();
            EnsureDirectory(FixtureRoot);
        }

        // VerifyRunner.RegisterDefaults runs via [InitializeOnLoadMethod] on
        // domain reload, but in some test-host orderings the static ctor may
        // not have run yet when the first [UnityTest] reaches the runner.
        // Ensure the three default rules are present so CreateCheckpoint /
        // RunScoped see them — otherwise CategoriesRun is empty and every
        // delta assertion silently degrades to "0 fingerprints".
        static void EnsureRulesRegistered()
        {
            if (VerifyRunner.Rules.Count == 0)
            {
                VerifyRunner.RegisterRule(new Rules.MissingReferencesRule());
                VerifyRunner.RegisterRule(new Rules.ScenePrefabHealthRule());
                VerifyRunner.RegisterRule(new Rules.DependenciesRule());
            }
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

            // Restore inline (C# forbids `yield` inside `finally`). Cleanup is
            // also idempotent: CreateHealthyPrefab's ClearFile reap any stale
            // .bak a failed assertion would leave behind.
            yield return RestorePrefab(HealthyPrefab);
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

            // Restore inline (C# forbids `yield` inside `finally`). The next
            // test's CreateHealthyPrefab also reaps stale .bak via ClearFile.
            yield return RestorePrefab(HealthyPrefab);

            var fixedValidate = VerifyRunner.RunScoped(scope, ruleIds, VerifyRunMode.Validate);
            var fixedDelta = ComputeSimpleDelta(checkpoint, fixedValidate);

            Assert.AreEqual(0, fixedDelta.newErrors,
                "After restoring, there should be no new errors vs the original checkpoint");
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

            // The fixture is static, so checkpoint and validate capture the
            // same issue set — the delta vs its own checkpoint is correctly
            // zero (no regression). What matters is that both rules actually
            // ran and produced fingerprints: that proves the multi-rule
            // checkpoint + validate path works end-to-end.
            Assert.AreEqual(2, checkpoint.Fingerprints.Count,
                "Checkpoint should fingerprint both rules");
            CollectionAssert.AreEquivalent(
                new[] { "missing_references", "scene_prefab_health" },
                checkpoint.Fingerprints.Keys,
                "Both requested rule ids should be fingerprinted");

            // And the validate pass against a known-broken fixture should
            // surface the previously-checkpointed issues (proving the rule
            // actually ran, not just registered).
            Assert.Greater(postValidate.Issues.Count, 0,
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
            // Re-create from scratch, clearing any stale state a previous
            // (possibly failed) test left behind: the mesh asset, its .meta,
            // and any .bak backups from BreakPrefab/RestorePrefab. The shared
            // fixture folder outlives tests, so leftover files from a prior
            // run are the most common source of IOException "already exists".
            var meshPath = System.IO.Path.ChangeExtension(path, ".asset");
            var metaPath = meshPath + ".meta";
            ClearFile(meshPath); ClearFile(meshPath + ".bak");
            ClearFile(metaPath); ClearFile(metaPath + ".bak");
            AssetDatabase.Refresh();

            var go = new GameObject("DeltaTest");
            var mf = go.AddComponent<MeshFilter>();
            var mesh = new Mesh();
            AssetDatabase.CreateAsset(mesh, meshPath);
            mf.sharedMesh = mesh;
            PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            AssetDatabase.Refresh();
            yield return null;
        }

        static System.Collections.IEnumerator BreakPrefab(string prefabPath)
        {
            var meshPath = System.IO.Path.ChangeExtension(prefabPath, ".asset");
            var metaPath = meshPath + ".meta";
            Assume.That(System.IO.File.Exists(meshPath), Is.True,
                $"Mesh asset must exist to break: {meshPath}");

            var backupMeshPath = meshPath + ".bak";
            var backupMetaPath = metaPath + ".bak";
            // If a prior run left a stale .bak (test crashed mid-break),
            // restore it first so the File.Move below doesn't collide.
            if (System.IO.File.Exists(backupMeshPath))
            {
                System.IO.File.Delete(backupMeshPath);
                if (System.IO.File.Exists(backupMetaPath))
                    System.IO.File.Delete(backupMetaPath);
            }
            System.IO.File.Move(meshPath, backupMeshPath);
            if (System.IO.File.Exists(metaPath))
                System.IO.File.Move(metaPath, backupMetaPath);
            AssetDatabase.Refresh();
            yield return null;
        }

        static System.Collections.IEnumerator RestorePrefab(string prefabPath)
        {
            var meshPath = System.IO.Path.ChangeExtension(prefabPath, ".asset");
            var metaPath = meshPath + ".meta";
            var backupMeshPath = meshPath + ".bak";
            var backupMetaPath = metaPath + ".bak";

            if (System.IO.File.Exists(meshPath))
                yield break;

            if (System.IO.File.Exists(backupMeshPath))
                System.IO.File.Move(backupMeshPath, meshPath);
            if (System.IO.File.Exists(backupMetaPath))
                System.IO.File.Move(backupMetaPath, metaPath);
            AssetDatabase.Refresh();
            yield return null;
        }

        static void ClearFile(string path)
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
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
