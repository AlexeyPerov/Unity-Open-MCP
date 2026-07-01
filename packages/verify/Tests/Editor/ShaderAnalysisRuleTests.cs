using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Rules;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class ShaderAnalysisRuleTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/ShaderAnalysis";

        private ShaderAnalysisRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new ShaderAnalysisRule();
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

        [Test]
        public void Id_IsCorrect()
        {
            Assert.AreEqual("shader_analysis", rule.Id);
        }

        [Test]
        public void Scan_EmptyPaths_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new string[0]);
            rule.Scan(scope, VerifyRunMode.Full, sink);
            Assert.AreEqual(0, sink.Count);
        }

        [Test]
        public void Scan_NonShaderPath_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Assets/SomePrefab.prefab" });
            rule.Scan(scope, VerifyRunMode.Full, sink);
            Assert.AreEqual(0, sink.Count);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_HealthyShader_ProducesNoCompileError()
        {
            var path = FixtureRoot + "/Healthy.shader";
            File.WriteAllText(path, ValidUnlitShader());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var compileError = sink.FirstOrDefault(i => i.IssueCode == "shader_compile_error");
            Assert.IsNull(compileError,
                $"Healthy shader must not produce shader_compile_error. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_FallbackShader_ReportsFallback()
        {
            var path = FixtureRoot + "/Fallback.shader";
            File.WriteAllText(path, ValidUnlitShaderWithFallback("Diffuse"));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var fallback = sink.FirstOrDefault(i => i.IssueCode == "fallback_shader");
            Assert.IsNotNull(fallback,
                $"Expected fallback_shader. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Warning, fallback.Severity);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_ShaderClassifiesWithoutThrowing()
        {
            // A shader with deliberately broken HLSL. Unity will either flag it
            // as an error shader or surface compile errors — either way the
            // rule must classify without throwing and emit known codes.
            var path = FixtureRoot + "/Broken.shader";
            File.WriteAllText(path, BrokenShader());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            Assert.DoesNotThrow(() =>
            {
                var sink = new List<VerifyIssue>();
                var scope = new VerifyScope(new[] { path });
                rule.Scan(scope, VerifyRunMode.Full, sink);

                foreach (var issue in sink)
                {
                    Assert.AreEqual("shader_analysis", issue.RuleId);
                    Assert.AreEqual(path, issue.AssetPath);
                    Assert.IsTrue(
                        issue.IssueCode == "shader_compile_error" ||
                        issue.IssueCode == "missing_shader_asset" ||
                        issue.IssueCode == "variant_explosion" ||
                        issue.IssueCode == "pass_count_exceeded" ||
                        issue.IssueCode == "fallback_shader" ||
                        issue.IssueCode == "expensive_feature_platform" ||
                        issue.IssueCode == "platform_keyword_mismatch",
                        $"Unexpected issue code: {issue.IssueCode}");
                }
            });
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_IssuesProduceValidKeys()
        {
            var path = FixtureRoot + "/Keys.shader";
            File.WriteAllText(path, ValidUnlitShader());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            foreach (var issue in sink)
            {
                var key = IssueKey.Build(issue);
                Assert.IsTrue(IssueKey.TryParse(key, out _, out _, out _, out _),
                    $"Issue key '{key}' must be valid");
            }
        }

        // -------------------------------------------------------------------
        // Fixture builders
        // -------------------------------------------------------------------

        private static string ValidUnlitShader()
        {
            return "Shader \"Hidden/VerifyHealthyShader\"\n" +
                   "{\n" +
                   "    SubShader\n" +
                   "    {\n" +
                   "        Pass\n" +
                   "        {\n" +
                   "            CGPROGRAM\n" +
                   "            #pragma vertex vert\n" +
                   "            #pragma fragment frag\n" +
                   "            float4 vert(float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }\n" +
                   "            fixed4 frag() : SV_Target { return fixed4(1,1,1,1); }\n" +
                   "            ENDCG\n" +
                   "        }\n" +
                   "    }\n" +
                   "}\n";
        }

        private static string ValidUnlitShaderWithFallback(string fallbackName)
        {
            return "Shader \"Hidden/VerifyFallbackShader\"\n" +
                   "{\n" +
                   "    SubShader\n" +
                   "    {\n" +
                   "        Pass\n" +
                   "        {\n" +
                   "            CGPROGRAM\n" +
                   "            #pragma vertex vert\n" +
                   "            #pragma fragment frag\n" +
                   "            float4 vert(float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }\n" +
                   "            fixed4 frag() : SV_Target { return fixed4(1,1,1,1); }\n" +
                   "            ENDCG\n" +
                   "        }\n" +
                   "    }\n" +
                   "    Fallback \"" + fallbackName + "\"\n" +
                   "}\n";
        }

        private static string BrokenShader()
        {
            return "Shader \"Hidden/VerifyBrokenShader\"\n" +
                   "{\n" +
                   "    SubShader\n" +
                   "    {\n" +
                   "        Pass\n" +
                   "        {\n" +
                   "            CGPROGRAM\n" +
                   "            #pragma vertex vert\n" +
                   "            #pragma fragment frag\n" +
                   "            float4 vert(float4 v : POSITION) : SV_POSITION { return ThisSymbolDoesNotExist(v); }\n" +
                   "            fixed4 frag() : SV_Target { return fixed4(1,1,1,1); }\n" +
                   "            ENDCG\n" +
                   "        }\n" +
                   "    }\n" +
                   "}\n";
        }

        private static void EnsureDirectory(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureDirectory(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
