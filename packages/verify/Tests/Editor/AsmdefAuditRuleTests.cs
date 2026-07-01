using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Rules;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class AsmdefAuditRuleTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/AsmdefAudit";

        private AsmdefAuditRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new AsmdefAuditRule();
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
            Assert.AreEqual("asmdef_audit", rule.Id);
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
        public void Scan_NonAsmdefPath_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Assets/SomeScript.cs" });
            rule.Scan(scope, VerifyRunMode.Full, sink);
            Assert.AreEqual(0, sink.Count);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_HealthyAsmdef_ProducesNoIssues()
        {
            var path = FixtureRoot + "/Healthy.asmdef";
            var asmdef = "{\n    \"name\": \"Healthy.Asmdef\",\n    \"rootNamespace\": \"Healthy\",\n    \"references\": [\n        \"UnityEngine\"\n    ]\n}\n";
            File.WriteAllText(path, asmdef);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var broken = sink.Where(i => i.IssueCode == "broken_asmdef_reference").ToList();
            Assert.AreEqual(0, broken.Count,
                $"Healthy asmdef must not produce broken_asmdef_reference. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_MissingName_ReportsMissingName()
        {
            var path = FixtureRoot + "/NoName.asmdef";
            // No "name" field — Unity cannot compile this.
            var asmdef = "{\n    \"references\": []\n}\n";
            File.WriteAllText(path, asmdef);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var missingName = sink.FirstOrDefault(i => i.IssueCode == "asmdef_missing_name");
            Assert.IsNotNull(missingName,
                $"Expected asmdef_missing_name. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Error, missingName.Severity);
            Assert.AreEqual("asmdef_audit", missingName.RuleId);
            Assert.AreEqual(path, missingName.AssetPath);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_BrokenReference_ReportsBrokenReference()
        {
            var path = FixtureRoot + "/BrokenRef.asmdef";
            // Reference to an assembly that does not exist.
            var asmdef = "{\n    \"name\": \"BrokenRef.Asmdef\",\n    \"references\": [\n        \"This.Assembly.Does.Not.Exist\"\n    ]\n}\n";
            File.WriteAllText(path, asmdef);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var broken = sink.FirstOrDefault(i => i.IssueCode == "broken_asmdef_reference");
            Assert.IsNotNull(broken,
                $"Expected broken_asmdef_reference. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Error, broken.Severity);
            Assert.AreEqual("asmdef_audit", broken.RuleId);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_BrokenGuidReference_ReportsBrokenReference()
        {
            var path = FixtureRoot + "/BrokenGuid.asmdef";
            // GUID reference to nothing.
            var asmdef = "{\n    \"name\": \"BrokenGuid.Asmdef\",\n    \"references\": [\n        \"GUID:1234567890abcdef1234567890abcdef\"\n    ]\n}\n";
            File.WriteAllText(path, asmdef);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var broken = sink.FirstOrDefault(i => i.IssueCode == "broken_asmdef_reference");
            Assert.IsNotNull(broken,
                $"Expected broken_asmdef_reference for unresolved GUID. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_MalformedJson_ReportsMalformed()
        {
            var path = FixtureRoot + "/Malformed.asmdef";
            File.WriteAllText(path, "{ this is not valid json ");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            // Malformed JSON with unbalanced braces is flagged. A document that
            // is structurally OK but semantically garbage may not trip the brace
            // check — the key assertion is that a clearly broken file is not
            // silently treated as healthy (it has no name at minimum).
            Assert.GreaterOrEqual(sink.Count, 1,
                $"Malformed asmdef must produce at least one issue. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_IssuesProduceValidKeys()
        {
            var path = FixtureRoot + "/Keys.asmdef";
            var asmdef = "{\n    \"name\": \"Keys.Asmdef\",\n    \"references\": [\n        \"This.DoesNotExist.Either\"\n    ]\n}\n";
            File.WriteAllText(path, asmdef);
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

        [UnityTest]
        public System.Collections.IEnumerator Scan_AllIssues_HaveCorrectRuleId()
        {
            var path = FixtureRoot + "/RuleId.asmdef";
            var asmdef = "{\n    \"references\": [\"Missing.Assembly\"]\n}\n";
            File.WriteAllText(path, asmdef);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            foreach (var issue in sink)
            {
                Assert.AreEqual("asmdef_audit", issue.RuleId);
                Assert.AreEqual(path, issue.AssetPath);
            }
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
