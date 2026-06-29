using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpVerify.References;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Tests
{
    public class DependenciesToolTests
    {
        private const string TestFolder = "Assets/__BridgeDepsTestTmp";
        private const string MatPath = TestFolder + "/DepsMat.mat";
        private const string MatPath2 = TestFolder + "/DepsMat2.mat";

        // Resolve to a real .shader asset file (URP/Lit in URP projects,
        // Standard fallback). Built-in shaders resolve to the virtual
        // Resources/unity_builtin_extra blob which AssetDatabase.GetDependencies
        // does not report as a dependency, so the reverse-lookup assertion needs
        // a real shader file to trace material→shader edges.
        private static Shader _shader;
        private static string _shaderPath;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (!AssetDatabase.IsValidFolder(TestFolder))
                AssetDatabase.CreateFolder("Assets", "__BridgeDepsTestTmp");

            ResolveRealAssetShader();
            Assume.That(_shader, Is.Not.Null,
                "No real-asset shader available — skipping fixture (URP/Lit or Standard required).");

            var mat = new Material(_shader);
            AssetDatabase.CreateAsset(mat, MatPath);
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
        public void Execute_ByAssetPath_EchoesQueriedFields()
        {
            var guid = AssetDatabase.AssetPathToGUID(MatPath);
            var body = $"{{\"asset_path\":\"{MatPath}\"}}";
            var result = DependenciesTool.Execute(body);

            Assert.IsTrue(result.Success, "tool should succeed for a real asset path");
            Assert.IsTrue(result.Output.Contains("\"queriedAssetPath\""));
            Assert.IsTrue(result.Output.Contains($"\"queriedAssetGuid\":\"{guid}\""));
        }

        [Test]
        public void Execute_ByGuid_EchoesQueriedFields()
        {
            var guid = AssetDatabase.AssetPathToGUID(MatPath);
            var body = $"{{\"guid\":\"{guid}\"}}";
            var result = DependenciesTool.Execute(body);

            Assert.IsTrue(result.Success);
            Assert.IsTrue(result.Output.Contains($"\"queriedAssetPath\":\"{MatPath}\""));
            Assert.IsTrue(result.Output.Contains($"\"queriedAssetGuid\":\"{guid}\""));
        }

        [Test]
        public void Execute_MissingInput_FailsWithMissingParameter()
        {
            var result = DependenciesTool.Execute("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Execute_UnresolvedGuid_ReturnsAssetNotFound()
        {
            // A syntactically valid but unresolvable GUID.
            var body = "{\"guid\":\"00000000000000001111111111111111\"}";
            var result = DependenciesTool.Execute(body);

            Assert.IsTrue(result.Success, "unresolved input is a clear status, not an error");
            Assert.IsTrue(result.Output.Contains("\"status\":\"asset_not_found\""));
            Assert.IsTrue(result.Output.Contains("\"forwardDependencies\":[]"));
            Assert.IsTrue(result.Output.Contains("\"reverseDependencies\":[]"));
        }

        [Test]
        public void Execute_ForwardDependencies_IncludesShader()
        {
            Assume.That(_shaderPath, Is.Not.Null,
                "No real-asset shader resolved — cannot test forward edge.");
            // A material references its shader — the forward dependency edge
            // should be reported.
            var body = $"{{\"asset_path\":\"{MatPath}\"}}";
            var result = DependenciesTool.Execute(body);

            Assert.IsTrue(result.Success);
            Assert.IsTrue(result.Output.Contains(_shaderPath),
                $"forwardDependencies should include the material's shader ({_shaderPath})");
            // Forward count is reported as a number.
            Assert.IsTrue(result.Output.Contains("\"forwardCount\":"),
                "forwardCount field should always be present");
        }

        [Test]
        public void Execute_ReverseDependencies_IncludesMaterialsForShader()
        {
            Assume.That(_shaderPath, Is.Not.Null,
                "No real-asset shader resolved — cannot test reverse edge.");
            // Reverse lookup of the shader should find at least the two test
            // materials. This is a full-project walk via ReferenceGraph.Find
            // (default options) — the materials we created are guaranteed
            // members of the result set even if other assets also reference
            // the same shader.
            var body = $"{{\"asset_path\":\"{_shaderPath}\"}}";
            var result = DependenciesTool.Execute(body);

            Assert.IsTrue(result.Success);
            Assert.IsTrue(result.Output.Contains(MatPath),
                $"reverseDependencies should include {MatPath}");
            Assert.IsTrue(result.Output.Contains(MatPath2),
                $"reverseDependencies should include {MatPath2}");
        }

        [Test]
        public void Execute_DetailSummary_OmitsRostersKeepsCounts()
        {
            var body = $"{{\"asset_path\":\"{MatPath}\",\"detail\":\"summary\"}}";
            var result = DependenciesTool.Execute(body);

            Assert.IsTrue(result.Success);
            // Counts are still present.
            Assert.IsTrue(result.Output.Contains("\"forwardCount\":"));
            Assert.IsTrue(result.Output.Contains("\"reverseCount\":"));
            // Rosters are empty (no objects in the arrays).
            Assert.IsTrue(result.Output.Contains("\"forwardDependencies\":[]"),
                "summary detail should omit the forward roster");
            Assert.IsTrue(result.Output.Contains("\"reverseDependencies\":[]"),
                "summary detail should omit the reverse roster");
        }

        [Test]
        public void Execute_BrokenForwardGuids_ArrayIsPresent()
        {
            // A healthy material has no broken forward edges, but the array
            // field must always be present so agents can branch on it.
            var body = $"{{\"asset_path\":\"{MatPath}\"}}";
            var result = DependenciesTool.Execute(body);

            Assert.IsTrue(result.Success);
            Assert.IsTrue(result.Output.Contains("\"brokenForwardGuids\":[]"));
            Assert.IsTrue(result.Output.Contains("\"cycles\":[]"));
            Assert.IsTrue(result.Output.Contains("\"detail\":\"normal\""));
            Assert.IsTrue(result.Output.Contains("\"truncated\":0"));
        }
    }
}
