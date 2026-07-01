using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Fixes;

namespace UnityOpenMcpVerify.Tests
{
    // M25 Plan 2 — fix_duplicate_guid fix-provider tests.
    //
    // CanFix/Describe/Safe cases are plain [Test]s. The end-to-end re-GUID is
    // a [UnityTest] that forces two assets to share a GUID and verifies Apply
    // with a deterministic regenerateGuid resolves the collision.
    [TestFixture]
    public class FixDuplicateGuidFixTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/FixDuplicateGuid";

        private FixDuplicateGuidFix fix;

        [SetUp]
        public void SetUp()
        {
            fix = new FixDuplicateGuidFix();
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
        public void FixId_IsFixDuplicateGuid()
        {
            Assert.AreEqual("fix_duplicate_guid", fix.FixId);
        }

        [Test]
        public void CanFix_ProjectHealthDuplicateGuid_ReturnsTrue()
        {
            var issueId = IssueKey.Build(
                "project_health", VerifySeverity.Error,
                "Assets/A.mat", "duplicate_guid");
            Assert.IsTrue(fix.CanFix(issueId));
        }

        [Test]
        public void CanFix_OfflineIntegrityDuplicateGuid_ReturnsTrue()
        {
            var issueId = IssueKey.Build(
                "offline_integrity", VerifySeverity.Error,
                "Assets/A.mat", "duplicate_guid");
            Assert.IsTrue(fix.CanFix(issueId));
        }

        [Test]
        public void CanFix_OrphanMeta_ReturnsFalse()
        {
            var issueId = IssueKey.Build(
                "project_health", VerifySeverity.Warning,
                "Assets/A.mat.meta", "orphan_meta");
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
        // Describe — Safe=false always, siblings advertised
        // -------------------------------------------------------------------

        [Test]
        public void Describe_IsNeverSafe()
        {
            var issueId = IssueKey.Build(
                "project_health", VerifySeverity.Error,
                "Assets/A.mat", "duplicate_guid");
            Assert.IsFalse(fix.Describe(issueId).Safe,
                "re-GUIDing silently rewires the asset graph — must be Safe=false");
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
        public void Apply_MalformedRegenerateGuid_Fails()
        {
            var issueId = IssueKey.Build(
                "project_health", VerifySeverity.Error,
                "Assets/__DoesNotExist__.mat", "duplicate_guid");
            var result = fix.Apply(issueId, "not-a-guid");
            Assert.IsFalse(result.Success);
            StringAssert.Contains("not a valid 32-hex", result.Description);
        }

        // -------------------------------------------------------------------
        // Registry wiring
        // -------------------------------------------------------------------

        [Test]
        public void Registry_AdvertisesFixDuplicateGuid()
        {
            CollectionAssert.Contains(
                FixProviderRegistry.AvailableFixIds(),
                "fix_duplicate_guid");
        }

        [Test]
        public void Registry_TryGetFixInfo_DuplicateGuid_ReturnsUnsafeFix()
        {
            var ok = FixProviderRegistry.TryGetFixInfo(
                "project_health", "duplicate_guid",
                out var fixId, out var safe);
            Assert.IsTrue(ok);
            Assert.AreEqual("fix_duplicate_guid", fixId);
            Assert.IsFalse(safe, "fix_duplicate_guid must surface as Safe=false");
        }

        [Test]
        public void Registry_FixesForIssue_ReturnsFixDuplicateGuidOnly()
        {
            var issue = IssueKey.Build(
                "project_health", VerifySeverity.Error,
                "Assets/A.mat", "duplicate_guid");
            CollectionAssert.AreEquivalent(
                new[] { "fix_duplicate_guid" },
                FixProviderRegistry.FixesForIssue(issue));
        }

        // -------------------------------------------------------------------
        // End-to-end — two assets share a GUID, Apply re-GUIDs one
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator Apply_WithDeterministicGuid_ResolvesCollision()
        {
            var matAPath = FixtureRoot + "/DupeA.mat";
            var matBPath = FixtureRoot + "/DupeB.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), matAPath);
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), matBPath);
            AssetDatabase.Refresh();
            yield return null;

            try
            {
                var guidA = AssetDatabase.AssetPathToGUID(matAPath);
                Assume.That(string.IsNullOrEmpty(guidA), Is.False, "matA must have a GUID");

                // Force matB's .meta to carry the SAME guid as matA → collision.
                OverwriteMetaGuid(matBPath, guidA);
                AssetDatabase.ImportAsset(matBPath + ".meta", ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(matBPath, ImportAssetOptions.ForceUpdate);
                yield return null;

                // After the forced collision, both should report guidA (the
                // AssetDatabase GUID->path map may resolve to either, but the
                // on-disk .meta for matB now carries guidA).
                Assume.That(ReadMetaGuid(matBPath), Is.EqualTo(guidA),
                    "matB meta must now carry matA's GUID (collision)");

                var issueId = IssueKey.Build(
                    "project_health", VerifySeverity.Error,
                    matBPath, "duplicate_guid");

                var newGuid = "0123456789abcdef0123456789abcdef";
                var result = fix.Apply(issueId, newGuid);

                Assert.IsTrue(result.Success,
                    $"Apply should succeed. Got: {result.Description}");
                Assert.That(result.TouchedPaths, Does.Contain(matBPath + ".meta"));

                // The re-GUIDed asset must now report the new GUID, not the
                // colliding one.
                Assert.AreEqual(newGuid, ReadMetaGuid(matBPath),
                    "matB meta must carry the regenerated GUID");
                Assert.AreNotEqual(guidA, ReadMetaGuid(matBPath),
                    "collision GUID must be gone from matB");
            }
            finally
            {
                if (AssetDatabase.LoadAssetAtPath<Material>(matAPath) != null)
                    AssetDatabase.DeleteAsset(matAPath);
                if (AssetDatabase.LoadAssetAtPath<Material>(matBPath) != null)
                    AssetDatabase.DeleteAsset(matBPath);
                AssetDatabase.Refresh();
            }
        }

        // -------------------------------------------------------------------
        // Fixture helpers
        // -------------------------------------------------------------------

        private static void OverwriteMetaGuid(string assetPath, string guid)
        {
            var metaPath = assetPath + ".meta";
            var lines = File.ReadAllLines(metaPath);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("guid:"))
                    lines[i] = "guid: " + guid;
            }
            File.WriteAllLines(metaPath, lines);
        }

        private static string ReadMetaGuid(string assetPath)
        {
            var metaPath = assetPath + ".meta";
            if (!File.Exists(metaPath)) return null;
            foreach (var line in File.ReadAllLines(metaPath))
            {
                if (line.StartsWith("guid:"))
                    return line.Substring("guid:".Length).Trim();
            }
            return null;
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
