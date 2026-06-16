using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Tests
{
    // TEMPORARILY DISABLED (heavy) — re-enable as part of T2.5 (EditMode
    // test-suite speed-up, specs/execution/M12/execution-plan-3-rules-wave2-
    // fixes.md). FindReferences performs full-project reverse-lookup scans of
    // the Standard shader across all assets. [Explicit] excludes from suite
    // runs until optimized; still runnable by name.
    [Explicit]
    public class VerifyGateAdapterFindReferencesTests
    {
        const string TestFolder = "Assets/__BridgeRefTestTmp";
        const string MatPath = TestFolder + "/TestMat.mat";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (!AssetDatabase.IsValidFolder(TestFolder))
                AssetDatabase.CreateFolder("Assets", "__BridgeRefTestTmp");

            var shader = Shader.Find("Standard");
            Assume.That(shader, Is.Not.Null, "Standard shader not found.");

            var mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, MatPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            AssetDatabase.DeleteAsset(TestFolder);
            AssetDatabase.Refresh();
        }

        [Test]
        public void FindReferences_SetsQueriedFields()
        {
            var result = VerifyGateAdapter.FindReferences(MatPath);
            Assert.AreEqual(MatPath, result.QueriedAssetPath);
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(MatPath), result.QueriedAssetGuid);
        }

        [Test]
        public void FindReferences_ByGuid_ReturnsSameAsset()
        {
            var guid = AssetDatabase.AssetPathToGUID(MatPath);
            var result = VerifyGateAdapter.FindReferences(guid);
            Assert.AreEqual(MatPath, result.QueriedAssetPath);
        }

        [Test]
        public void FindReferences_DefaultMaxResults_ReportsTotalCount()
        {
            var shader = Shader.Find("Standard");
            Assume.That(shader, Is.Not.Null);
            var shaderPath = AssetDatabase.GetAssetPath(shader);
            Assume.That(string.IsNullOrEmpty(shaderPath), Is.False);

            var result = VerifyGateAdapter.FindReferences(shaderPath);
            Assert.Greater(result.TotalCount, 0, "Standard shader should have at least one dependent.");
            Assert.LessOrEqual(result.ReferencedBy.Length, result.TotalCount);
        }

        [Test]
        public void FindReferences_MaxResults_Truncates()
        {
            var shader = Shader.Find("Standard");
            Assume.That(shader, Is.Not.Null);
            var shaderPath = AssetDatabase.GetAssetPath(shader);
            Assume.That(string.IsNullOrEmpty(shaderPath), Is.False);

            var full = VerifyGateAdapter.FindReferences(shaderPath);
            Assume.That(full.TotalCount, Is.GreaterThan(1),
                "Need >1 dependents to test truncation.");

            var result = VerifyGateAdapter.FindReferences(shaderPath, maxResults: 1);
            Assert.AreEqual(1, result.ReferencedBy.Length,
                "Should be truncated to 1 entry.");
            Assert.AreEqual(full.TotalCount, result.TotalCount,
                "TotalCount should reflect untruncated total.");
        }

        [Test]
        public void FindReferences_ReferencedByEntries_HavePathAndGuid()
        {
            var shader = Shader.Find("Standard");
            Assume.That(shader, Is.Not.Null);
            var shaderPath = AssetDatabase.GetAssetPath(shader);
            Assume.That(string.IsNullOrEmpty(shaderPath), Is.False);

            var result = VerifyGateAdapter.FindReferences(shaderPath);
            Assert.Greater(result.ReferencedBy.Length, 0);

            foreach (var entry in result.ReferencedBy)
            {
                Assert.IsFalse(string.IsNullOrEmpty(entry.AssetPath),
                    "Entry should have an AssetPath.");
                Assert.IsFalse(string.IsNullOrEmpty(entry.Guid),
                    "Entry should have a Guid.");
                Assert.AreEqual(
                    AssetDatabase.AssetPathToGUID(entry.AssetPath),
                    entry.Guid,
                    "Guid should match AssetPath.");
            }
        }
    }
}
