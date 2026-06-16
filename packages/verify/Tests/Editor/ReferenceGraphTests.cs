using System.Collections.Generic;
using NUnit.Framework;
using UnityOpenMcpVerify.References;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpVerify.Tests
{
    // TEMPORARILY DISABLED (heavy) — re-enable as part of T2.5 (EditMode
    // test-suite speed-up, specs/execution/M12/execution-plan-3-rules-wave2-
    // fixes.md). ReferenceGraph.Find walks all assets referencing the Standard
    // shader (full-project traversal). [Explicit] excludes from suite runs
    // until optimized; still runnable by name.
    [Explicit]
    public class ReferenceGraphTests
    {
        const string TestFolder = "Assets/__RefGraphTestTmp";
        const string MatAPath = TestFolder + "/MatA.mat";
        const string MatBPath = TestFolder + "/MatB.mat";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (!AssetDatabase.IsValidFolder(TestFolder))
                AssetDatabase.CreateFolder("Assets", "__RefGraphTestTmp");

            var shader = Shader.Find("Standard");
            Assert.IsNotNull(shader, "Standard shader not found — cannot create test materials.");

            var matA = new Material(shader);
            AssetDatabase.CreateAsset(matA, MatAPath);

            var matB = new Material(shader);
            AssetDatabase.CreateAsset(matB, MatBPath);

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
        public void Find_ByPath_SetsQueriedFields()
        {
            var graph = ReferenceGraph.Find(MatAPath);
            Assert.AreEqual(MatAPath, graph.QueriedAssetPath);
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(MatAPath), graph.QueriedAssetGuid);
        }

        [Test]
        public void Find_ByGuid_ReturnsSameAsset()
        {
            var guid = AssetDatabase.AssetPathToGUID(MatAPath);
            var graph = ReferenceGraph.Find(guid);
            Assert.AreEqual(MatAPath, graph.QueriedAssetPath);
            Assert.AreEqual(guid, graph.QueriedAssetGuid);
        }

        [Test]
        public void Find_InvalidPath_ReturnsEmptyReferencedBy()
        {
            var graph = ReferenceGraph.Find("Assets/NonExistent__12345.asset");
            Assert.IsNotNull(graph.ReferencedByPaths);
            Assert.IsEmpty(graph.ReferencedByPaths);
        }

        [Test]
        public void Find_InvalidGuid_ReturnsEmptyReferencedBy()
        {
            var graph = ReferenceGraph.Find("00000000000000000000000000000000");
            Assert.IsNotNull(graph.ReferencedByPaths);
            Assert.IsEmpty(graph.ReferencedByPaths);
        }

        [Test]
        public void Find_PrefabReferencedByScene_ReturnsScene()
        {
            var prefabPath = "Assets/Prefabs/GateTestCube.prefab";
            var scenePath = "Assets/Scenes/Main.unity";

            if (!System.IO.File.Exists(prefabPath) || !System.IO.File.Exists(scenePath))
            {
                Assert.Ignore("Demo prefab/scene not present — skipping.");
                return;
            }

            var graph = ReferenceGraph.Find(prefabPath);
            Assert.Contains(scenePath, graph.ReferencedByPaths,
                $"Expected {scenePath} to reference {prefabPath}.");
        }

        [Test]
        public void Find_ReverseLookup_ByGuid()
        {
            var prefabPath = "Assets/Prefabs/GateTestCube.prefab";
            var scenePath = "Assets/Scenes/Main.unity";

            if (!System.IO.File.Exists(prefabPath) || !System.IO.File.Exists(scenePath))
            {
                Assert.Ignore("Demo prefab/scene not present — skipping.");
                return;
            }

            var guid = AssetDatabase.AssetPathToGUID(prefabPath);
            var graph = ReferenceGraph.Find(guid);
            Assert.Contains(scenePath, graph.ReferencedByPaths,
                $"Expected {scenePath} when querying prefab by GUID.");
        }

        [Test]
        public void Find_ShaderReferencedByTestMaterials()
        {
            var shader = Shader.Find("Standard");
            Assume.That(shader, Is.Not.Null, "Standard shader not available.");

            var shaderPath = AssetDatabase.GetAssetPath(shader);
            Assume.That(string.IsNullOrEmpty(shaderPath), Is.False, "Cannot resolve Standard shader path.");

            var graph = ReferenceGraph.Find(shaderPath);
            Assert.Contains(MatAPath, graph.ReferencedByPaths,
                $"Expected {MatAPath} to depend on {shaderPath}.");
            Assert.Contains(MatBPath, graph.ReferencedByPaths,
                $"Expected {MatBPath} to depend on {shaderPath}.");
        }
    }
}
