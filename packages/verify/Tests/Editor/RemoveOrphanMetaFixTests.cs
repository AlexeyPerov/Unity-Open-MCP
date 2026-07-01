using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Fixes;

namespace UnityOpenMcpVerify.Tests
{
    // M25 Plan 2 — remove_orphan_meta fix-provider tests.
    //
    // CanFix/Describe/Safe cases are plain [Test]s. The end-to-end delete is a
    // [UnityTest] that creates a .meta with no companion asset and verifies
    // Apply() removes it. The fixture folder is shared and torn down once.
    [TestFixture]
    public class RemoveOrphanMetaFixTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/RemoveOrphanMeta";

        private RemoveOrphanMetaFix fix;

        [SetUp]
        public void SetUp()
        {
            fix = new RemoveOrphanMetaFix();
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
        public void FixId_IsRemoveOrphanMeta()
        {
            Assert.AreEqual("remove_orphan_meta", fix.FixId);
        }

        [Test]
        public void CanFix_ProjectHealthOrphanMeta_ReturnsTrue()
        {
            var issueId = IssueKey.Build(
                "project_health", VerifySeverity.Warning,
                "Assets/Orphan.cs.meta", "orphan_meta");
            Assert.IsTrue(fix.CanFix(issueId));
        }

        [Test]
        public void CanFix_OfflineIntegrityOrphanMeta_ReturnsTrue()
        {
            var issueId = IssueKey.Build(
                "offline_integrity", VerifySeverity.Warning,
                "Assets/Orphan.cs.meta", "orphan_meta");
            Assert.IsTrue(fix.CanFix(issueId));
        }

        [Test]
        public void CanFix_DuplicateGuid_ReturnsFalse()
        {
            // duplicate_guid belongs to fix_duplicate_guid.
            var issueId = IssueKey.Build(
                "project_health", VerifySeverity.Error,
                "Assets/A.mat", "duplicate_guid");
            Assert.IsFalse(fix.CanFix(issueId));
        }

        [Test]
        public void CanFix_MissingScript_ReturnsFalse()
        {
            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/A.prefab", "missing_script");
            Assert.IsFalse(fix.CanFix(issueId));
        }

        [Test]
        public void CanFix_MalformedIssueId_ReturnsFalse()
        {
            Assert.IsFalse(fix.CanFix("garbage"));
            Assert.IsFalse(fix.CanFix(null));
            Assert.IsFalse(fix.CanFix(""));
        }

        // -------------------------------------------------------------------
        // Describe — Safe=true always
        // -------------------------------------------------------------------

        [Test]
        public void Describe_IsAlwaysSafe()
        {
            var issueId = IssueKey.Build(
                "project_health", VerifySeverity.Warning,
                "Assets/Orphan.cs.meta", "orphan_meta");
            Assert.IsTrue(fix.Describe(issueId).Safe,
                "deleting a detached .meta loses no asset data — must be Safe");
        }

        // -------------------------------------------------------------------
        // Apply — argument validation
        // -------------------------------------------------------------------

        [Test]
        public void Apply_MalformedIssueId_Fails()
        {
            var result = fix.Apply("garbage");
            Assert.IsFalse(result.Success);
            StringAssert.Contains("Cannot parse", result.Description);
        }

        [Test]
        public void Apply_NonMetaPath_Fails()
        {
            var issueId = IssueKey.Build(
                "project_health", VerifySeverity.Warning,
                "Assets/NotAMeta.mat", "orphan_meta");
            var result = fix.Apply(issueId);
            Assert.IsFalse(result.Success);
            StringAssert.Contains(".meta", result.Description);
        }

        // -------------------------------------------------------------------
        // Registry wiring
        // -------------------------------------------------------------------

        [Test]
        public void Registry_AdvertisesRemoveOrphanMeta()
        {
            CollectionAssert.Contains(
                FixProviderRegistry.AvailableFixIds(),
                "remove_orphan_meta");
        }

        [Test]
        public void Registry_TryGetFixInfo_OrphanMeta_ReturnsSafeFix()
        {
            var ok = FixProviderRegistry.TryGetFixInfo(
                "project_health", "orphan_meta",
                out var fixId, out var safe);
            Assert.IsTrue(ok);
            Assert.AreEqual("remove_orphan_meta", fixId);
            Assert.IsTrue(safe, "remove_orphan_meta must surface as Safe=true");
        }

        [Test]
        public void Registry_FixesForIssue_ReturnsRemoveOrphanMetaOnly()
        {
            var issue = IssueKey.Build(
                "project_health", VerifySeverity.Warning,
                "Assets/Orphan.cs.meta", "orphan_meta");
            CollectionAssert.AreEquivalent(
                new[] { "remove_orphan_meta" },
                FixProviderRegistry.FixesForIssue(issue));
        }

        // -------------------------------------------------------------------
        // End-to-end — create orphan .meta, Apply deletes it
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator Apply_OrphanMeta_DeletesFile()
        {
            var orphanMeta = FixtureRoot + "/OrphanAsset.cs.meta";
            File.WriteAllText(orphanMeta,
                "fileFormatVersion: 2\nguid: " + System.Guid.NewGuid().ToString("N") + "\n");
            AssetDatabase.Refresh();
            yield return null;

            Assume.That(File.Exists(orphanMeta), Is.True, "orphan meta must exist before apply");

            var issueId = IssueKey.Build(
                "project_health", VerifySeverity.Warning,
                orphanMeta, "orphan_meta");

            var result = fix.Apply(issueId);

            Assert.IsTrue(result.Success, $"Apply should succeed. Got: {result.Description}");
            Assert.That(result.TouchedPaths, Does.Contain(orphanMeta));
            Assert.IsFalse(File.Exists(orphanMeta),
                "orphan .meta must be deleted after Apply");
        }

        [UnityTest]
        public System.Collections.IEnumerator Apply_WhenCompanionExists_NoChange()
        {
            // A .meta whose companion asset exists is NOT orphaned — the fix
            // must refuse rather than delete a meta Unity still needs.
            var assetPath = FixtureRoot + "/HasCompanion.mat";
            AssetDatabase.CreateAsset(new UnityEngine.Material(UnityEngine.Shader.Find("Standard")), assetPath);
            AssetDatabase.Refresh();
            yield return null;

            var metaPath = assetPath + ".meta";
            Assume.That(File.Exists(metaPath), Is.True, "companion meta must exist");

            var issueId = IssueKey.Build(
                "project_health", VerifySeverity.Warning,
                metaPath, "orphan_meta");

            var result = fix.Apply(issueId);

            Assert.IsTrue(result.Success);
            Assert.IsNull(result.TouchedPaths, "no change when companion exists");
            Assert.IsTrue(File.Exists(metaPath), "meta must remain when companion exists");

            // Clean up the real asset we created.
            AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.Refresh();
        }

        // -------------------------------------------------------------------
        // Fixture helpers
        // -------------------------------------------------------------------

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
