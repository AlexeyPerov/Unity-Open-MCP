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
    public class MaterialsRuleTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/Materials";

        private MaterialsRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new MaterialsRule();
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
            Assert.AreEqual("materials", rule.Id);
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
        public void Scan_NonMaterialPath_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Assets/SomePrefab.prefab" });
            rule.Scan(scope, VerifyRunMode.Full, sink);
            Assert.AreEqual(0, sink.Count);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_HealthyMaterial_ProducesNoMissingShaderOrTexture()
        {
            var path = FixtureRoot + "/Healthy.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);
            AssetDatabase.Refresh();
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var missing = sink.Where(i => i.IssueCode == "missing_shader" || i.IssueCode == "missing_texture").ToList();
            Assert.AreEqual(0, missing.Count,
                $"Healthy material must not produce missing_shader/missing_texture. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_BrokenShaderReference_ReportsMissingShader()
        {
            // Create a material, then corrupt its m_Shader GUID so Unity falls
            // back to InternalErrorShader on load (the high-value detection the
            // YAML-walk approach missed).
            var path = FixtureRoot + "/BrokenShader.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);
            AssetDatabase.Refresh();
            yield return null;

            InjectBrokenShaderGuid(path, "1234567890abcdef1234567890abcdef");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var missingShader = sink.FirstOrDefault(i => i.IssueCode == "missing_shader");
            Assert.IsNotNull(missingShader,
                $"Expected missing_shader (InternalErrorShader). Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Error, missingShader.Severity);
            Assert.AreEqual("materials", missingShader.RuleId);
            Assert.AreEqual(path, missingShader.AssetPath);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_BuiltinShader_ReportsBuiltinShader()
        {
            var path = FixtureRoot + "/Builtin.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);
            AssetDatabase.Refresh();
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var builtin = sink.FirstOrDefault(i => i.IssueCode == "builtin_shader");
            Assert.IsNotNull(builtin,
                $"Expected builtin_shader for Standard. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Warning, builtin.Severity);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_RenderQueueOverride_ReportsOverride()
        {
            var path = FixtureRoot + "/QueueOverride.mat";
            var mat = new Material(Shader.Find("Standard"));
            mat.renderQueue = 9999; // deliberately off the shader default.
            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.Refresh();
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var overrideIssue = sink.FirstOrDefault(i => i.IssueCode == "render_queue_override");
            Assert.IsNotNull(overrideIssue,
                $"Expected render_queue_override. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_IssuesProduceValidKeys()
        {
            var path = FixtureRoot + "/Keys.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);
            AssetDatabase.Refresh();
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

        [UnityTest]
        public System.Collections.IEnumerator Scan_FullMode_DetectsDuplicateMaterials()
        {
            // Two materials with identical shader + properties → same fingerprint.
            var pathA = FixtureRoot + "/DupA.mat";
            var pathB = FixtureRoot + "/DupB.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), pathA);
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), pathB);
            AssetDatabase.Refresh();
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { pathA, pathB });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var dup = sink.FirstOrDefault(i => i.IssueCode == "duplicate_material");
            Assert.IsNotNull(dup,
                $"Expected duplicate_material. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        // -------------------------------------------------------------------
        // Fixture helpers
        // -------------------------------------------------------------------

        // Replaces the m_Shader GUID with a broken one so the material falls
        // back to InternalErrorShader (the real-world "missing shader" case).
        private static void InjectBrokenShaderGuid(string matPath, string brokenGuid)
        {
            var yaml = File.ReadAllText(matPath);
            var lines = yaml.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("m_Shader:")) continue;
                var idx = lines[i].IndexOf("guid: ");
                if (idx < 0) continue;
                var guidStart = idx + "guid: ".Length;
                var guidEnd = guidStart + 32;
                if (guidEnd > lines[i].Length) continue;
                var currentGuid = lines[i].Substring(guidStart, 32);
                if (currentGuid.StartsWith("0000000000")) continue;
                lines[i] = lines[i].Substring(0, guidStart) + brokenGuid + lines[i].Substring(guidEnd);
                break;
            }
            File.WriteAllText(matPath, string.Join("\n", lines));
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
