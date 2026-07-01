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
        public System.Collections.IEnumerator Scan_HealthyMaterial_ProducesNoIssues()
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
            var path = FixtureRoot + "/BrokenShader.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);
            AssetDatabase.Refresh();
            yield return null;

            // Overwrite m_Shader with a GUID that does not resolve.
            InjectBrokenGuid(path, "m_Shader", "1234567890abcdef1234567890abcdef");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var missingShader = sink.FirstOrDefault(i => i.IssueCode == "missing_shader");
            Assert.IsNotNull(missingShader,
                $"Expected missing_shader. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Error, missingShader.Severity);
            Assert.AreEqual("materials", missingShader.RuleId);
            Assert.AreEqual(path, missingShader.AssetPath);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_BrokenTextureReference_ReportsMissingTexture()
        {
            var path = FixtureRoot + "/BrokenTexture.mat";
            var mat = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.Refresh();
            yield return null;

            // Inject a broken texture ref into _MainTex's m_Texture field.
            InjectBrokenGuid(path, "m_Texture", "fedcba0987654321fedcba0987654321");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var missingTexture = sink.FirstOrDefault(i => i.IssueCode == "missing_texture");
            Assert.IsNotNull(missingTexture,
                $"Expected missing_texture. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Error, missingTexture.Severity);
            Assert.AreEqual("materials", missingTexture.RuleId);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_IssuesProduceValidKeys()
        {
            var path = FixtureRoot + "/Keys.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);
            AssetDatabase.Refresh();
            yield return null;

            InjectBrokenGuid(path, "m_Shader", "abcdef0123456789abcdef0123456789");
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
        // Fixture helpers
        // -------------------------------------------------------------------

        // Replaces the first `guid: <realGuid>` occurrence on a line containing
        // the target property with a broken GUID, so the material references a
        // shader/texture that does not exist.
        private static void InjectBrokenGuid(string matPath, string propertyMarker, string brokenGuid)
        {
            var yaml = File.ReadAllText(matPath);
            var lines = yaml.Replace("\r\n", "\n").Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(propertyMarker)) continue;
                // The GUID reference lives on this line (PPtr form). Replace the
                // first real GUID we find with the broken one.
                var idx = lines[i].IndexOf("guid: ");
                if (idx < 0) continue;
                var guidStart = idx + "guid: ".Length;
                var guidEnd = guidStart + 32;
                if (guidEnd > lines[i].Length) continue;
                var currentGuid = lines[i].Substring(guidStart, 32);
                // Only replace real (non-zero) GUIDs — built-in shader GUIDs are
                // all-zero and represent valid built-ins.
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
