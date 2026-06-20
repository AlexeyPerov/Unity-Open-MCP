using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityOpenMcpVerify.References;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpVerify.Tests
{
    public class ReferenceGraphTests
    {
        const string TestFolder = "Assets/__RefGraphTestTmp";
        const string MatAPath = TestFolder + "/MatA.mat";
        const string MatBPath = TestFolder + "/MatB.mat";

        // Reuse one scoped-options instance across the tests that want to
        // narrow the reverse-dependency walk to the fixture folder. Building
        // the reverse map is the single expensive step in ReferenceGraph.Find
        // — scoping it from AssetDatabase.GetAllAssetPaths() (every URP
        // shader, package script, demo asset) down to two test materials
        // collapses it from seconds to milliseconds.
        static readonly ReferenceGraphOptions ScopedOptions = new ReferenceGraphOptions
        {
            ScanRoots = new List<string> { TestFolder }
        };

        // The shader used to build test materials MUST resolve to a real
        // .shader asset file, because AssetDatabase.GetDependencies() only
        // reports dependencies that live on disk. Built-in shaders (e.g.
        // "Standard", which resolves to the virtual "Resources/unity_builtin_extra"
        // blob) are NOT reported as dependencies, so the reverse-lookup tests
        // cannot see the material→shader edge through them. URP projects ship
        // "Universal Render Pipeline/Lit" as a real file under the package
        // cache; non-URP projects fall back to the legacy "Standard" shader
        // only if it happens to resolve to a real file. Resolve at setup so
        // the whole fixture skips cleanly if neither is available.
        static Shader _shader;
        static string _shaderPath;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (!AssetDatabase.IsValidFolder(TestFolder))
                AssetDatabase.CreateFolder("Assets", "__RefGraphTestTmp");

            ResolveRealAssetShader();
            Assume.That(_shader, Is.Not.Null,
                "No real-asset shader available — skipping fixture (URP/Lit or Standard required).");

            var matA = new Material(_shader);
            AssetDatabase.CreateAsset(matA, MatAPath);

            var matB = new Material(_shader);
            AssetDatabase.CreateAsset(matB, MatBPath);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        static void ResolveRealAssetShader()
        {
            // Prefer URP/Lit (always a real .shader file in URP projects — the
            // demo is URP). Fall back to Standard only when it resolves to a
            // real on-disk path (non-URP / built-in-shaders-extracted setup).
            foreach (var name in new[] { "Universal Render Pipeline/Lit", "Standard" })
            {
                var sh = Shader.Find(name);
                if (sh == null) continue;
                var path = AssetDatabase.GetAssetPath(sh);
                if (string.IsNullOrEmpty(path)) continue;
                // Built-in shaders resolve to this virtual blob, not a file —
                // GetDependencies cannot trace through them.
                if (path == "Resources/unity_builtin_extra") continue;
                if (!System.IO.File.Exists(path)) continue;
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
        public void Find_ByPath_SetsQueriedFields()
        {
            var graph = ReferenceGraph.Find(MatAPath, ScopedOptions);
            Assert.AreEqual(MatAPath, graph.QueriedAssetPath);
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(MatAPath), graph.QueriedAssetGuid);
        }

        [Test]
        public void Find_ByGuid_ReturnsSameAsset()
        {
            var guid = AssetDatabase.AssetPathToGUID(MatAPath);
            var graph = ReferenceGraph.Find(guid, ScopedOptions);
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

            // A prefab referenced by a scene requires scanning the scene's
            // folder; the test fixtures themselves don't contain it.
            var options = new ReferenceGraphOptions
            {
                ScanRoots = new List<string> { "Assets/Scenes" }
            };
            var graph = ReferenceGraph.Find(prefabPath, options);
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

            var options = new ReferenceGraphOptions
            {
                ScanRoots = new List<string> { "Assets/Scenes" }
            };
            var guid = AssetDatabase.AssetPathToGUID(prefabPath);
            var graph = ReferenceGraph.Find(guid, options);
            Assert.Contains(scenePath, graph.ReferencedByPaths,
                $"Expected {scenePath} when querying prefab by GUID.");
        }

        [Test]
        public void Find_ShaderReferencedByTestMaterials()
        {
            Assume.That(_shaderPath, Is.Not.Null,
                "No real-asset shader resolved at fixture setup — cannot test reverse shader lookup.");

            // Scoped scan: only the two test materials are walked, but the
            // shader path still resolves because each material's GetDependencies
            // lists it (it's a real .shader file) and Find records reverse
            // entries for every dependency encountered.
            var graph = ReferenceGraph.Find(_shaderPath, ScopedOptions);
            Assert.Contains(MatAPath, graph.ReferencedByPaths,
                $"Expected {MatAPath} to depend on {_shaderPath}.");
            Assert.Contains(MatBPath, graph.ReferencedByPaths,
                $"Expected {MatBPath} to depend on {_shaderPath}.");
        }
    }
}
