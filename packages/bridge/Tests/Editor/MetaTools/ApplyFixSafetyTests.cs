using System.Collections.Generic;
using NUnit.Framework;
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
    }
}
