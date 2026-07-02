using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Fixes;

namespace UnityOpenMcpVerify.Tests
{
    // M25 Plan 2 — reassign_missing_texture fix-provider tests.
    //
    // CanFix/Describe/Safe + Apply-without-target-refuses cases are plain
    // [Test]s. The end-to-end reassign is a [UnityTest] that creates a material
    // with a null _MainTex, applies a real texture, and confirms it's set.
    [TestFixture]
    public class ReassignMissingTextureFixTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/ReassignMissingTexture";

        private ReassignMissingTextureFix fix;

        [SetUp]
        public void SetUp()
        {
            fix = new ReassignMissingTextureFix();
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
        public void FixId_IsReassignMissingTexture()
        {
            Assert.AreEqual("reassign_missing_texture", fix.FixId);
        }

        [Test]
        public void CanFix_MaterialsMissingTexture_ReturnsTrue()
        {
            var issueId = IssueKey.Build(
                "materials", VerifySeverity.Warning,
                "Assets/A.mat", "missing_texture");
            Assert.IsTrue(fix.CanFix(issueId));
        }

        [Test]
        public void CanFix_MissingShader_ReturnsFalse()
        {
            var issueId = IssueKey.Build(
                "materials", VerifySeverity.Error,
                "Assets/A.mat", "missing_shader");
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
        // Describe — Safe=false always
        // -------------------------------------------------------------------

        [Test]
        public void Describe_IsNeverSafe()
        {
            var issueId = IssueKey.Build(
                "materials", VerifySeverity.Warning,
                "Assets/A.mat", "missing_texture");
            Assert.IsFalse(fix.Describe(issueId).Safe,
                "a wrong texture silently changes the material's look — must be Safe=false");
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
        public void Apply_WithoutTargetTexture_Refuses()
        {
            var issueId = IssueKey.Build(
                "materials", VerifySeverity.Warning,
                "Assets/__DoesNotExist__.mat", "missing_texture");
            var result = fix.Apply(issueId);
            Assert.IsFalse(result.Success);
            StringAssert.Contains("target_texture", result.Description);
        }

        // -------------------------------------------------------------------
        // Registry wiring
        // -------------------------------------------------------------------

        [Test]
        public void Registry_AdvertisesReassignMissingTexture()
        {
            CollectionAssert.Contains(
                FixProviderRegistry.AvailableFixIds(),
                "reassign_missing_texture");
        }

        [Test]
        public void Registry_TryGetFixInfo_MissingTexture_ReturnsUnsafeFix()
        {
            var ok = FixProviderRegistry.TryGetFixInfo(
                "materials", "missing_texture",
                out var fixId, out var safe);
            Assert.IsTrue(ok);
            Assert.AreEqual("reassign_missing_texture", fixId);
            Assert.IsFalse(safe, "reassign_missing_texture must surface as Safe=false");
        }

        [Test]
        public void Registry_FixesForIssue_ReturnsReassignMissingTextureOnly()
        {
            var issue = IssueKey.Build(
                "materials", VerifySeverity.Warning,
                "Assets/A.mat", "missing_texture");
            CollectionAssert.AreEquivalent(
                new[] { "reassign_missing_texture" },
                FixProviderRegistry.FixesForIssue(issue));
        }

        // -------------------------------------------------------------------
        // End-to-end — null _MainTex reassigned to a real texture
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator Apply_WithTargetTexture_AssignsToNullSlot()
        {
            var matPath = FixtureRoot + "/MissingTex.mat";
            // CreateAsset on a Texture2D rejects image-file extensions (.png/.jpg)
            // in Unity 6 — use .asset so the in-memory Texture2D persists as a
            // loadable asset without triggering the image-importer guard.
            var texPath = FixtureRoot + "/TargetTex.asset";

            var mat = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(mat, matPath);

            // A freshly-created Standard material has a null _MainTex.
            var tex = new Texture2D(2, 2);
            AssetDatabase.CreateAsset(tex, texPath);
            AssetDatabase.Refresh();
            yield return null;

            try
            {
                Assume.That(mat.GetTexture("_MainTex"), Is.Null,
                    "fresh Standard material must have a null _MainTex");

                var issueId = IssueKey.Build(
                    "materials", VerifySeverity.Warning,
                    matPath, "missing_texture");

                var result = fix.Apply(issueId, texPath);

                Assert.IsTrue(result.Success,
                    $"Apply should succeed. Got: {result.Description}");
                Assert.That(result.TouchedPaths, Does.Contain(matPath));

                // Reload the material from disk to confirm persistence.
                AssetDatabase.ImportAsset(matPath, ImportAssetOptions.ForceUpdate);
                var reloaded = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                Assert.IsNotNull(reloaded.GetTexture("_MainTex"),
                    "_MainTex must be assigned after Apply");
            }
            finally
            {
                if (AssetDatabase.LoadAssetAtPath<Material>(matPath) != null)
                    AssetDatabase.DeleteAsset(matPath);
                if (AssetDatabase.LoadAssetAtPath<Texture>(texPath) != null)
                    AssetDatabase.DeleteAsset(texPath);
                AssetDatabase.Refresh();
            }
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
