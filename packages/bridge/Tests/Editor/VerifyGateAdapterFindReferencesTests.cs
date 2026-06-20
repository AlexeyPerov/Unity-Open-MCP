using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityOpenMcpVerify.References;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Tests
{
    public class VerifyGateAdapterFindReferencesTests
    {
        private const string TestFolder = "Assets/__BridgeRefTestTmp";
        private const string MatPath = TestFolder + "/TestMat.mat";
        private const string MatPath2 = TestFolder + "/TestMat2.mat";

        // Scope the reverse-lookup walk to the fixture folder. Without this
        // VerifyGateAdapter.FindReferences -> ReferenceGraph.Find would call
        // AssetDatabase.GetAllAssetPaths() and compute forward dependencies
        // for every package asset in the project (URP shaders, package
        // scripts, demo assets) on every test invocation.
        private static readonly ReferenceGraphOptions ScopedOptions = new ReferenceGraphOptions
        {
            ScanRoots = new List<string> { TestFolder }
        };

        // Resolve to a real .shader asset file (URP/Lit in URP projects).
        // Built-in shaders like "Standard" resolve to the virtual
        // "Resources/unity_builtin_extra" blob, which AssetDatabase.
        // GetDependencies does not report as a dependency — so the
        // reverse-lookup tests cannot trace material→shader edges through
        // them. Falls back to "Standard" only when it resolves to a real file.
        private static Shader _shader;
        private static string _shaderPath;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (!AssetDatabase.IsValidFolder(TestFolder))
                AssetDatabase.CreateFolder("Assets", "__BridgeRefTestTmp");

            ResolveRealAssetShader();
            Assume.That(_shader, Is.Not.Null,
                "No real-asset shader available — skipping fixture (URP/Lit or Standard required).");

            var mat = new Material(_shader);
            AssetDatabase.CreateAsset(mat, MatPath);
            // A second material on the same shader so the MaxResults truncation
            // test has >1 dependent to truncate from.
            var mat2 = new Material(_shader);
            AssetDatabase.CreateAsset(mat2, MatPath2);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private static void ResolveRealAssetShader()
        {
            foreach (var name in new[] { "Universal Render Pipeline/Lit", "Standard" })
            {
                var sh = Shader.Find(name);
                if (sh == null) continue;
                var path = AssetDatabase.GetAssetPath(sh);
                if (string.IsNullOrEmpty(path)) continue;
                if (path == "Resources/unity_builtin_extra") continue;
                if (!File.Exists(path)) continue;
                _shader = sh;
                _shaderPath = path;
                return;
            }
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
            var result = VerifyGateAdapter.FindReferences(MatPath, 100, ScopedOptions);
            Assert.AreEqual(MatPath, result.QueriedAssetPath);
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(MatPath), result.QueriedAssetGuid);
        }

        [Test]
        public void FindReferences_ByGuid_ReturnsSameAsset()
        {
            var guid = AssetDatabase.AssetPathToGUID(MatPath);
            var result = VerifyGateAdapter.FindReferences(guid, 100, ScopedOptions);
            Assert.AreEqual(MatPath, result.QueriedAssetPath);
        }

        [Test]
        public void FindReferences_DefaultMaxResults_ReportsTotalCount()
        {
            Assume.That(_shaderPath, Is.Not.Null,
                "No real-asset shader resolved — cannot test shader reverse lookup.");

            var result = VerifyGateAdapter.FindReferences(_shaderPath, 100, ScopedOptions);
            Assert.Greater(result.TotalCount, 0, "Shader should have at least one dependent (the test material).");
            Assert.LessOrEqual(result.ReferencedBy.Length, result.TotalCount);
        }

        [Test]
        public void FindReferences_MaxResults_Truncates()
        {
            Assume.That(_shaderPath, Is.Not.Null,
                "No real-asset shader resolved — cannot test truncation.");

            var full = VerifyGateAdapter.FindReferences(_shaderPath, 100, ScopedOptions);
            Assume.That(full.TotalCount, Is.GreaterThan(1),
                "Need >1 dependents to test truncation.");

            var result = VerifyGateAdapter.FindReferences(_shaderPath, maxResults: 1, options: ScopedOptions);
            Assert.AreEqual(1, result.ReferencedBy.Length,
                "Should be truncated to 1 entry.");
            Assert.AreEqual(full.TotalCount, result.TotalCount,
                "TotalCount should reflect untruncated total.");
        }

        [Test]
        public void FindReferences_ReferencedByEntries_HavePathAndGuid()
        {
            Assume.That(_shaderPath, Is.Not.Null,
                "No real-asset shader resolved — cannot test entry shape.");

            var result = VerifyGateAdapter.FindReferences(_shaderPath, 100, ScopedOptions);
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
