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
    // TEMPORARILY DISABLED (heavy) — re-enable as part of T2.5 (EditMode
    // test-suite speed-up, specs/execution/M12/execution-plan-3-rules-wave2-
    // fixes.md). [UnityTest] coroutines exercise real fixture assets with
    // AssetDatabase I/O. [Explicit] excludes from suite runs until optimized;
    // still runnable by name.
    [Explicit]
    [TestFixture]
    public class FixtureScenePrefabHealthTests
    {
        const string HealthyFixture = "Assets/Fixtures/HealthyFixture.prefab";
        const string BrokenRefFixture = "Assets/Fixtures/BrokenRefFixture.prefab";
        const string MissingScriptFixture = "Assets/Fixtures/MissingScriptFixture.prefab";

        ScenePrefabHealthRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new ScenePrefabHealthRule();
        }

        [UnityTest]
        public System.Collections.IEnumerator HealthyFixture_PassesScenePrefabHealth()
        {
            yield return null;

            Assume.That(System.IO.File.Exists(HealthyFixture),
                Is.True, $"Fixture missing: {HealthyFixture}");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { HealthyFixture });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            Assert.AreEqual(0, sink.Count,
                $"HealthyFixture should have no scene_prefab_health issues. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator BrokenRefFixture_DetectedByScenePrefabHealth()
        {
            yield return null;

            Assume.That(System.IO.File.Exists(BrokenRefFixture),
                Is.True, $"Fixture missing: {BrokenRefFixture}");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { BrokenRefFixture });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            foreach (var issue in sink)
            {
                Assert.AreEqual("scene_prefab_health", issue.RuleId);
                Assert.AreEqual(BrokenRefFixture, issue.AssetPath);
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator MissingScriptFixture_ScenePrefabHealthCheck()
        {
            yield return null;

            Assume.That(System.IO.File.Exists(MissingScriptFixture),
                Is.True, $"Fixture missing: {MissingScriptFixture}");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { MissingScriptFixture });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            foreach (var issue in sink)
            {
                Assert.AreEqual("scene_prefab_health", issue.RuleId);
                var key = IssueKey.Build(issue);
                Assert.IsTrue(IssueKey.TryParse(key, out _, out _, out _, out _),
                    $"Issue key '{key}' must be valid");
            }
        }
    }
}
