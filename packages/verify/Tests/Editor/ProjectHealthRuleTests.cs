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
    public class ProjectHealthRuleTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/ProjectHealth";

        private ProjectHealthRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new ProjectHealthRule();
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
            Assert.AreEqual("project_health", rule.Id);
        }

        [Test]
        public void Scan_CheckpointMode_ProducesNoIssues()
        {
            // project_health is Full-scan only — it must no-op in checkpoint /
            // validate mode so a scoped gate does not pay the whole-tree cost.
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Assets/SomeFolder" });
            rule.Scan(scope, VerifyRunMode.Checkpoint, sink);
            Assert.AreEqual(0, sink.Count, "project_health must not run in Checkpoint mode");
        }

        [Test]
        public void Scan_ValidateMode_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Assets/SomeFolder" });
            rule.Scan(scope, VerifyRunMode.Validate, sink);
            Assert.AreEqual(0, sink.Count, "project_health must not run in Validate mode");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_OrphanMeta_Detected()
        {
            // Create a stray .meta with no companion asset.
            var orphanMeta = FixtureRoot + "/OrphanAsset.cs.meta";
            File.WriteAllText(orphanMeta,
                "fileFormatVersion: 2\n" +
                "guid: " + System.Guid.NewGuid().ToString("N") + "\n");
            AssetDatabase.Refresh();
            yield return null;

            try
            {
                var sink = new List<VerifyIssue>();
                var scope = new VerifyScope(new[] { FixtureRoot });
                rule.Scan(scope, VerifyRunMode.Full, sink);

                var orphan = sink.FirstOrDefault(i => i.IssueCode == "orphan_meta");
                Assert.IsNotNull(orphan,
                    $"Expected orphan_meta. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
                Assert.AreEqual(VerifySeverity.Warning, orphan.Severity);
                Assert.AreEqual("project_health", orphan.RuleId);
            }
            finally
            {
                if (File.Exists(orphanMeta)) File.Delete(orphanMeta);
                AssetDatabase.Refresh();
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_IssuesProduceValidKeys()
        {
            var orphanMeta = FixtureRoot + "/KeysOrphan.cs.meta";
            File.WriteAllText(orphanMeta,
                "fileFormatVersion: 2\n" +
                "guid: " + System.Guid.NewGuid().ToString("N") + "\n");
            AssetDatabase.Refresh();
            yield return null;

            try
            {
                var sink = new List<VerifyIssue>();
                var scope = new VerifyScope(new[] { FixtureRoot });
                rule.Scan(scope, VerifyRunMode.Full, sink);

                foreach (var issue in sink)
                {
                    var key = IssueKey.Build(issue);
                    Assert.IsTrue(IssueKey.TryParse(key, out _, out _, out _, out _),
                        $"Issue key '{key}' must be valid");
                }
            }
            finally
            {
                if (File.Exists(orphanMeta)) File.Delete(orphanMeta);
                AssetDatabase.Refresh();
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
