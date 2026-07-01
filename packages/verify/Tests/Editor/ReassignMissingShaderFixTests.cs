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
    // M25 Plan 2 — reassign_missing_shader fix-provider tests.
    //
    // CanFix/Describe/Safe + Apply-without-target-refuses cases are plain
    // [Test]s. The end-to-end reassign is a [UnityTest] that creates a material
    // whose shader is the InternalErrorShader, applies a real shader by name,
    // and confirms it's set.
    [TestFixture]
    public class ReassignMissingShaderFixTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/ReassignMissingShader";

        private ReassignMissingShaderFix fix;

        [SetUp]
        public void SetUp()
        {
            fix = new ReassignMissingShaderFix();
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
        public void FixId_IsReassignMissingShader()
        {
            Assert.AreEqual("reassign_missing_shader", fix.FixId);
        }

        [Test]
        public void CanFix_MaterialsMissingShader_ReturnsTrue()
        {
            var issueId = IssueKey.Build(
                "materials", VerifySeverity.Error,
                "Assets/A.mat", "missing_shader");
            Assert.IsTrue(fix.CanFix(issueId));
        }

        [Test]
        public void CanFix_MissingTexture_ReturnsFalse()
        {
            var issueId = IssueKey.Build(
                "materials", VerifySeverity.Warning,
                "Assets/A.mat", "missing_texture");
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
                "materials", VerifySeverity.Error,
                "Assets/A.mat", "missing_shader");
            Assert.IsFalse(fix.Describe(issueId).Safe,
                "a wrong shader silently changes rendering — must be Safe=false");
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
        public void Apply_WithoutTargetShader_Refuses()
        {
            var issueId = IssueKey.Build(
                "materials", VerifySeverity.Error,
                "Assets/__DoesNotExist__.mat", "missing_shader");
            var result = fix.Apply(issueId);
            Assert.IsFalse(result.Success);
            StringAssert.Contains("target_shader", result.Description);
        }

        // -------------------------------------------------------------------
        // Registry wiring
        // -------------------------------------------------------------------

        [Test]
        public void Registry_AdvertisesReassignMissingShader()
        {
            CollectionAssert.Contains(
                FixProviderRegistry.AvailableFixIds(),
                "reassign_missing_shader");
        }

        [Test]
        public void Registry_TryGetFixInfo_MissingShader_ReturnsUnsafeFix()
        {
            var ok = FixProviderRegistry.TryGetFixInfo(
                "materials", "missing_shader",
                out var fixId, out var safe);
            Assert.IsTrue(ok);
            Assert.AreEqual("reassign_missing_shader", fixId);
            Assert.IsFalse(safe, "reassign_missing_shader must surface as Safe=false");
        }

        [Test]
        public void Registry_FixesForIssue_ReturnsReassignMissingShaderOnly()
        {
            var issue = IssueKey.Build(
                "materials", VerifySeverity.Error,
                "Assets/A.mat", "missing_shader");
            CollectionAssert.AreEquivalent(
                new[] { "reassign_missing_shader" },
                FixProviderRegistry.FixesForIssue(issue));
        }

        // -------------------------------------------------------------------
        // End-to-end — InternalErrorShader reassigned to a real shader
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator Apply_WithTargetShaderByName_AssignsShader()
        {
            var matPath = FixtureRoot + "/MissingShader.mat";
            // Start the material on Standard, then force it to the error shader
            // to simulate the failure mode the materials rule reports.
            var mat = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(mat, matPath);
            AssetDatabase.Refresh();
            yield return null;

            try
            {
                mat.shader = Shader.Find("Hidden/InternalErrorShader");
                EditorUtility.SetDirty(mat);
                AssetDatabase.SaveAssets();
                Assume.That(mat.shader.name, Is.EqualTo("Hidden/InternalErrorShader"),
                    "material must be on the error shader before apply");

                var issueId = IssueKey.Build(
                    "materials", VerifySeverity.Error,
                    matPath, "missing_shader");

                var result = fix.Apply(issueId, "Standard");

                Assert.IsTrue(result.Success,
                    $"Apply should succeed. Got: {result.Description}");
                Assert.That(result.TouchedPaths, Does.Contain(matPath));

                AssetDatabase.ImportAsset(matPath, ImportAssetOptions.ForceUpdate);
                var reloaded = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                Assert.AreEqual("Standard", reloaded.shader.name,
                    "shader must be Standard after Apply");
            }
            finally
            {
                if (AssetDatabase.LoadAssetAtPath<Material>(matPath) != null)
                    AssetDatabase.DeleteAsset(matPath);
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
