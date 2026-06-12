using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityAgentVerify.Tests
{
    [TestFixture]
    public class FixtureMissingReferencesTests
    {
        const string MissingScriptFixture = "Assets/Fixtures/MissingScriptFixture.prefab";
        const string BrokenRefFixture = "Assets/Fixtures/BrokenRefFixture.prefab";
        const string HealthyFixture = "Assets/Fixtures/HealthyFixture.prefab";

        MissingReferencesRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new MissingReferencesRule();
        }

        [Test]
        public void MissingScriptFixture_Exists()
        {
            Assert.IsTrue(
                System.IO.File.Exists(MissingScriptFixture),
                $"Fixture missing: {MissingScriptFixture}");
        }

        [UnityTest]
        public System.Collections.IEnumerator MissingScriptFixture_DetectsMissingScript()
        {
            yield return null;

            Assume.That(System.IO.File.Exists(MissingScriptFixture),
                Is.True, $"Fixture missing: {MissingScriptFixture}");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { MissingScriptFixture });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            Assert.Greater(sink.Count, 0,
                "MissingScriptFixture must produce at least one missing_references issue");

            var hasScriptIssue = sink.Any(i => i.IssueCode == "missing_script");
            Assert.IsTrue(hasScriptIssue,
                $"Expected 'missing_script' issue code. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");

            foreach (var issue in sink)
            {
                Assert.AreEqual("missing_references", issue.RuleId);
                Assert.AreEqual(MissingScriptFixture, issue.AssetPath);
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator MissingScriptFixture_CheckpointMode_ReportsKeys()
        {
            yield return null;

            Assume.That(System.IO.File.Exists(MissingScriptFixture),
                Is.True, $"Fixture missing: {MissingScriptFixture}");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { MissingScriptFixture });

            rule.Scan(scope, VerifyRunMode.Checkpoint, sink);

            Assert.Greater(sink.Count, 0,
                "Checkpoint mode must report at least one issue for MissingScriptFixture");

            foreach (var issue in sink)
            {
                var key = IssueKey.Build(issue);
                Assert.IsTrue(IssueKey.TryParse(key, out _, out _, out _, out _),
                    $"Issue key '{key}' must be valid");
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator BrokenRefFixture_DetectsMissingGuid()
        {
            yield return null;

            Assume.That(System.IO.File.Exists(BrokenRefFixture),
                Is.True, $"Fixture missing: {BrokenRefFixture}");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { BrokenRefFixture });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            Assert.Greater(sink.Count, 0,
                "BrokenRefFixture must produce at least one missing_references issue");

            foreach (var issue in sink)
            {
                Assert.AreEqual("missing_references", issue.RuleId);
                Assert.AreEqual(BrokenRefFixture, issue.AssetPath);
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator HealthyFixture_PassesMissingReferences()
        {
            yield return null;

            Assume.That(System.IO.File.Exists(HealthyFixture),
                Is.True, $"Fixture missing: {HealthyFixture}");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { HealthyFixture });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            Assert.AreEqual(0, sink.Count,
                $"HealthyFixture should have no missing_references issues. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator BothFixtures_ScanTogether()
        {
            yield return null;

            Assume.That(System.IO.File.Exists(MissingScriptFixture), Is.True);
            Assume.That(System.IO.File.Exists(BrokenRefFixture), Is.True);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { MissingScriptFixture, BrokenRefFixture });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            Assert.Greater(sink.Count, 0);

            var paths = sink.Select(i => i.AssetPath).Distinct().ToList();
            Assert.AreEqual(2, paths.Count, "Issues should span both fixtures");
        }
    }
}
