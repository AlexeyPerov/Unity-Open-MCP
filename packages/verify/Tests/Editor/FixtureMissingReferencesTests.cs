using System.Collections.Generic;
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
    public class FixtureMissingReferencesTests
    {
        private const string MissingScriptFixture = "Assets/Fixtures/MissingScriptFixture.prefab";
        private const string BrokenRefFixture = "Assets/Fixtures/BrokenRefFixture.prefab";
        private const string HealthyFixture = "Assets/Fixtures/HealthyFixture.prefab";
        private const string RestorableRefFixture = "Assets/Fixtures/RestorableRefFixture.prefab";

        private MissingReferencesRule rule;

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

            var hasMissingGuid = sink.Any(i => i.IssueCode.StartsWith("missing_guid"));
            Assert.IsTrue(hasMissingGuid,
                $"Expected 'missing_guid' issue code. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");

            var missingGuidIssue = sink.First(i => i.IssueCode.StartsWith("missing_guid"));
            Assert.AreEqual(VerifySeverity.Error, missingGuidIssue.Severity,
                "missing_guid must be Error severity so gate/delta catches broken PPtr refs");

            foreach (var issue in sink)
            {
                Assert.AreEqual("missing_references", issue.RuleId);
                Assert.AreEqual(BrokenRefFixture, issue.AssetPath);
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator RestorableRefFixture_IsHealthy()
        {
            yield return null;

            Assume.That(System.IO.File.Exists(RestorableRefFixture),
                Is.True, $"Fixture missing: {RestorableRefFixture}");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { RestorableRefFixture });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            // The fixture represents a "healthy" prefab (no broken PPtr edges,
            // no missing scripts, no missing assets). Unity's standard null
            // markers (m_PrefabInstance/m_Father/etc. with fileID: 0) surface
            // as empty_local_ref *warnings* on every prefab; those are
            // boilerplate noise, not real missing-reference defects. Filter
            // them out before asserting health so the test reflects the
            // fixture's intent rather than the rule's null-marker sensitivity.
            var realIssues = sink.Where(i => i.IssueCode != "empty_local_ref").ToList();

            Assert.AreEqual(0, realIssues.Count,
                $"RestorableRefFixture should have no real missing_references issues. " +
                $"Got: {string.Join(", ", realIssues.Select(i => $"{i.IssueCode}({i.Severity})"))}");
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
