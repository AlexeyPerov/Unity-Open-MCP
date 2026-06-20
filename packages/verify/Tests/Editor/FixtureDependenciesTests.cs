using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Rules;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class FixtureDependenciesTests
    {
        const string BrokenRefFixture = "Assets/Fixtures/BrokenRefFixture.prefab";
        const string HealthyFixture = "Assets/Fixtures/HealthyFixture.prefab";

        DependenciesRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new DependenciesRule();
        }

        [Test]
        public void Fixtures_Exist()
        {
            Assert.IsTrue(System.IO.File.Exists(BrokenRefFixture),
                $"Fixture missing: {BrokenRefFixture}");
            Assert.IsTrue(System.IO.File.Exists(HealthyFixture),
                $"Fixture missing: {HealthyFixture}");
        }

        [UnityTest]
        public System.Collections.IEnumerator BrokenRefFixture_DetectsBrokenDependency()
        {
            yield return null;

            Assume.That(System.IO.File.Exists(BrokenRefFixture), Is.True,
                $"Fixture missing: {BrokenRefFixture}");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { BrokenRefFixture });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            var broken = sink.FirstOrDefault(i => i.IssueCode == "broken_dependency");
            Assert.IsNotNull(broken,
                $"Expected 'broken_dependency' on BrokenRefFixture. " +
                $"Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Error, broken.Severity,
                "broken_dependency must be Error severity so the gate catches it");
            Assert.AreEqual("dependencies", broken.RuleId);
            Assert.AreEqual(BrokenRefFixture, broken.AssetPath);

            // Issue key must round-trip (gate delta depends on this).
            var key = IssueKey.Build(broken);
            Assert.IsTrue(IssueKey.TryParse(key, out _, out _, out _, out _),
                $"Issue key '{key}' must be valid");
        }

        [UnityTest]
        public System.Collections.IEnumerator HealthyFixture_PassesDependencies()
        {
            yield return null;

            Assume.That(System.IO.File.Exists(HealthyFixture), Is.True,
                $"Fixture missing: {HealthyFixture}");

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { HealthyFixture });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            var broken = sink.Where(i => i.IssueCode == "broken_dependency").ToList();
            Assert.AreEqual(0, broken.Count,
                $"HealthyFixture must not produce broken_dependency. " +
                $"Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator BothFixtures_ScanTogether_OnlyBrokenReports()
        {
            yield return null;

            Assume.That(System.IO.File.Exists(BrokenRefFixture), Is.True);
            Assume.That(System.IO.File.Exists(HealthyFixture), Is.True);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { BrokenRefFixture, HealthyFixture });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            // Only the broken fixture should report issues.
            var brokenPaths = sink
                .Where(i => i.IssueCode == "broken_dependency")
                .Select(i => i.AssetPath)
                .Distinct()
                .ToList();
            CollectionAssert.AreEquivalent(new[] { BrokenRefFixture }, brokenPaths);
        }
    }
}
