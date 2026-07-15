using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Fixes;

namespace UnityOpenMcpBridge.Tests
{
    // M30-polish Plan 2 — verify fix-action safety tests.
    //
    // T2.2 — ApplyFixTool.Execute must refuse a non-dry-run apply when no
    //        FixRollback snapshot is active (the batch_execute / direct-dispatch
    //        bypass). The guard lives in ApplyFixTool; the ambient flag is set
    //        by ApplyFixGateRunner.
    // T2.4 — NormalizeRestoredPaths must not mangle sibling dirs like
    //        …/AssetsBackup into AssetsAssetsBackup.
    // T5.1 — gate:"off" on a non-dry-run apply marks RollbackDisabled so the
    //        envelope surfaces a warning that the fix committed with no
    //        auto-rollback.
    [TestFixture]
    public class ApplyFixSafetyTests
    {
        // -------------------------------------------------------------------
        // T2.2 — Non-dry-run apply refused without rollback snapshot
        // -------------------------------------------------------------------

        [Test]
        public void ApplyFix_NonDryRun_WithoutRollbackSnapshot_RefusesWithError()
        {
            // Simulate the batch_execute / direct-dispatch path: ApplyFixTool
            // is called directly (no ApplyFixGateRunner wrapper), so no
            // FixRollback snapshot is active. The guard must refuse.
            Assert.IsFalse(ApplyFixGateRunner.RollbackSnapshotActive,
                "no rollback snapshot should be active outside the gate runner");

            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/__DoesNotExist__.prefab", "missing_guid");

            // Use a fix_id that matches a real provider so the guard is
            // reached (after CanFix passes, before Apply is called).
            var body = "{\"fix_id\":\"relink_broken_guid\",\"issue_id\":\"" + issueId
                + "\",\"dry_run\":false}";

            var result = ApplyFixTool.Execute(body);

            Assert.IsFalse(result.Success,
                "non-dry-run apply without rollback must be refused");
            Assert.AreEqual("rollback_unavailable", result.ErrorCode);
            StringAssert.Contains("gate runner", result.ErrorMessage);
        }

        [Test]
        public void ApplyFix_DryRun_WithoutRollbackSnapshot_StillWorks()
        {
            // Dry-run must work from any dispatch path — it doesn't mutate.
            Assert.IsFalse(ApplyFixGateRunner.RollbackSnapshotActive);

            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/__DoesNotExist__.prefab", "missing_guid");

            var body = "{\"fix_id\":\"relink_broken_guid\",\"issue_id\":\"" + issueId
                + "\",\"dry_run\":true}";

            var result = ApplyFixTool.Execute(body);

            Assert.IsTrue(result.Success,
                "dry-run apply must work without a rollback snapshot");
        }

        // -------------------------------------------------------------------
        // T5.1 — gate:"off" marks RollbackDisabled on a successful apply
        // -------------------------------------------------------------------

        [Test]
        public void ApplyFix_GateOff_SuccessfulApply_MarksRollbackDisabled()
        {
            // gate:"off" skips the delta and never consults the rollback
            // snapshot. A successful apply under this path must surface the
            // rollbackDisabled flag so the agent knows the mutation committed
            // without auto-rollback protection.
            var assetPath = "Assets/Tests/BridgeFixtures/ApplyFixGateOff/GuardOnly.mat";
            EnsureFixtureMaterial(assetPath);
            try
            {
                var issueId = IssueKey.Build(
                    "materials", VerifySeverity.Error,
                    assetPath, "missing_shader");
                var body = "{\"fix_id\":\"test_rollback_corruptor\",\"issue_id\":\"" + issueId
                    + "\",\"dry_run\":false}";

                using var provider = new NoopFixScope();
                var result = ApplyFixGateRunner.Execute(body, "off", new[] { assetPath });

                Assert.IsTrue(result.Mutation.Success,
                    "a succeeding apply under gate:off must still commit. " + result.Mutation.ErrorMessage);
                Assert.IsTrue(result.RollbackDisabled,
                    "gate:off + successful apply must mark RollbackDisabled");
                Assert.IsFalse(result.RolledBack,
                    "gate:off never rolls back (no delta to check)");
                Assert.IsNotNull(result.AgentNextSteps,
                    "rollbackDisabled must carry agentNextSteps recommending manual validate");
            }
            finally
            {
                DeleteAssetIfExists(assetPath);
            }
        }

        [Test]
        public void ApplyFix_GateEnforce_SuccessfulApply_DoesNotMarkRollbackDisabled()
        {
            // The rollbackDisabled flag is specific to gate:"off". Under enforce
            // (the default), the runner keeps the snapshot active and would roll
            // back on new errors — so the flag must NOT be set on a clean pass.
            var assetPath = "Assets/Tests/BridgeFixtures/ApplyFixGateOff/Enforce.mat";
            EnsureFixtureMaterial(assetPath);
            try
            {
                var issueId = IssueKey.Build(
                    "materials", VerifySeverity.Error,
                    assetPath, "missing_shader");
                var body = "{\"fix_id\":\"test_rollback_corruptor\",\"issue_id\":\"" + issueId
                    + "\",\"dry_run\":false}";

                var result = ApplyFixGateRunner.Execute(body, "enforce", new[] { assetPath });

                Assert.IsTrue(result.Mutation.Success, result.Mutation.ErrorMessage);
                Assert.IsFalse(result.RollbackDisabled,
                    "gate:enforce must NOT set rollbackDisabled (snapshot is active)");
                Assert.IsFalse(result.RolledBack,
                    "a clean enforce pass must not roll back");
            }
            finally
            {
                DeleteAssetIfExists(assetPath);
            }
        }

        // -------------------------------------------------------------------
        // T2.4 — NormalizeRestoredPaths (pure unit tests, no Unity fixtures)
        // -------------------------------------------------------------------

        [Test]
        public void Normalize_AssetsRootExactMatch_ReturnsAssets()
        {
            var dataPath = Application.dataPath.Replace('\\', '/');

            var result = ApplyFixGateRunner.NormalizeRestoredPaths(new[] { dataPath });

            Assert.AreEqual(new[] { "Assets" }, result);
        }

        [Test]
        public void Normalize_PathUnderAssets_ReturnsAssetsRelative()
        {
            var dataPath = Application.dataPath.Replace('\\', '/');

            var result = ApplyFixGateRunner.NormalizeRestoredPaths(
                new[] { dataPath + "/Foo.mat" });

            Assert.AreEqual(new[] { "Assets/Foo.mat" }, result);
        }

        [Test]
        public void Normalize_SiblingDirAssetsBackup_LeftUnchanged()
        {
            // The bug: the old bare StartsWith(dataPath) branch matched
            // …/AssetsBackup and produced AssetsAssetsBackup/…. The fix only
            // matches exact dataPath or dataPath + "/".
            var dataPath = Application.dataPath.Replace('\\', '/');
            var sibling = dataPath + "Backup/x.mat";

            var result = ApplyFixGateRunner.NormalizeRestoredPaths(new[] { sibling });

            Assert.AreEqual(new[] { sibling }, result,
                "a sibling dir like AssetsBackup must NOT be mangled into AssetsAssetsBackup");
        }

        [Test]
        public void Normalize_AlreadyRelativePath_LeftUnchanged()
        {
            var result = ApplyFixGateRunner.NormalizeRestoredPaths(
                new[] { "Assets/Foo.mat" });

            Assert.AreEqual(new[] { "Assets/Foo.mat" }, result);
        }

        [Test]
        public void Normalize_NullOrEmpty_ReturnsAsIs()
        {
            Assert.IsNull(ApplyFixGateRunner.NormalizeRestoredPaths(null));
            Assert.AreEqual(new string[0], ApplyFixGateRunner.NormalizeRestoredPaths(new string[0]));
        }

        [Test]
        public void Normalize_MixedPaths_HandlesEachIndependently()
        {
            var dataPath = Application.dataPath.Replace('\\', '/');

            var result = ApplyFixGateRunner.NormalizeRestoredPaths(new[]
            {
                dataPath + "/A.mat",
                dataPath,
                dataPath + "Backup/B.mat",
                "Assets/C.mat",
            });

            Assert.AreEqual(new[]
            {
                "Assets/A.mat",
                "Assets",
                dataPath + "Backup/B.mat",
                "Assets/C.mat",
            }, result);
        }

        // -------------------------------------------------------------------
        // T5.1 helpers — fixture material + test fix provider registration
        // -------------------------------------------------------------------

        private static void EnsureFixtureMaterial(string assetPath)
        {
            EnsureDirectory(Path.GetDirectoryName(assetPath));
            if (AssetDatabase.LoadAssetAtPath<Material>(assetPath) == null)
            {
                AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), assetPath);
                AssetDatabase.Refresh();
            }
        }

        private static void DeleteAssetIfExists(string assetPath)
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(assetPath) != null)
            {
                AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.Refresh();
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

        // Registers a test-only IFixProvider ("test_rollback_corruptor") that
        // succeeds without changing the asset, so the gate=off path completes
        // the apply and the RollbackDisabled flag can be observed. Disposes by
        // clearing + re-registering the production fix set.
        private sealed class NoopFixScope : System.IDisposable
        {
            public NoopFixScope()
            {
                FixProviderRegistry.Clear();
                FixProviderRegistry.Register(new NoopSuccessProvider());
            }

            public void Dispose()
            {
                FixProviderRegistry.Clear();
                FixProviderRegistry.Register(new RemoveMissingScriptFix());
                FixProviderRegistry.Register(new RelinkBrokenGuidFix());
                FixProviderRegistry.Register(new RemoveOrphanMetaFix());
                FixProviderRegistry.Register(new FixDuplicateGuidFix());
                FixProviderRegistry.Register(new ReassignMissingTextureFix());
                FixProviderRegistry.Register(new ReassignMissingShaderFix());
            }
        }

        private class NoopSuccessProvider : IFixProvider
        {
            public string FixId => "test_rollback_corruptor";

            public bool CanFix(string issueId)
            {
                if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var issueCode))
                    return false;
                return ruleId == "materials" && issueCode == "missing_shader";
            }

            public FixDescription Describe(string issueId)
            {
                IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _);
                return new FixDescription
                {
                    FixId = FixId,
                    IssueId = issueId,
                    AssetPath = assetPath,
                    Description = "test-only no-op success provider",
                    Safe = false,
                };
            }

            public FixResult Apply(string issueId)
            {
                return new FixResult
                {
                    Success = true,
                    Description = "test provider: no-op success",
                    TouchedPaths = null,
                };
            }
        }
    }
}
