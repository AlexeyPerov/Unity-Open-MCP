using System.Collections.Generic;
using NUnit.Framework;

namespace UnityAgentVerify.Tests
{
    [TestFixture]
    public class VerifyResultTests
    {
        [Test]
        public void Constructor_SetsProperties()
        {
            var issues = new List<VerifyIssue>();
            var result = new VerifyResult(issues, new[] { "rule_a" }, 42L);

            Assert.AreSame(issues, result.Issues);
            Assert.AreEqual(new[] { "rule_a" }, result.CategoriesRun);
            Assert.AreEqual(42L, result.DurationMs);
        }

        [Test]
        public void HasUnknownRules_DefaultFalse()
        {
            var result = new VerifyResult(new List<VerifyIssue>(), new string[0], 0);
            Assert.IsFalse(result.HasUnknownRules);
            Assert.IsEmpty(result.UnknownRuleIds);
            Assert.IsEmpty(result.AvailableRuleIds);
        }

        [Test]
        public void HasUnknownRules_TrueWhenProvided()
        {
            var result = new VerifyResult(
                new List<VerifyIssue>(), new string[0], 0,
                unknownRuleIds: new[] { "ghost" },
                availableRuleIds: new[] { "real" });

            Assert.IsTrue(result.HasUnknownRules);
            Assert.AreEqual(new[] { "ghost" }, result.UnknownRuleIds);
            Assert.AreEqual(new[] { "real" }, result.AvailableRuleIds);
        }
    }

    [TestFixture]
    public class VerifyScopeTests
    {
        [Test]
        public void Constructor_SetsPaths()
        {
            var scope = new VerifyScope(new[] { "Assets/A.prefab" });
            Assert.AreEqual(new[] { "Assets/A.prefab" }, scope.Paths);
            Assert.IsFalse(scope.IncludeDependents);
        }

        [Test]
        public void Constructor_IncludeDependents()
        {
            var scope = new VerifyScope(new[] { "Assets/A.prefab" }, true);
            Assert.IsTrue(scope.IncludeDependents);
        }
    }

    [TestFixture]
    public class CheckpointFingerprintTests
    {
        [Test]
        public void Constructor_SetsProperties()
        {
            var fps = new Dictionary<string, RuleFingerprint>
            {
                ["rule_a"] = new RuleFingerprint(1, 2, new HashSet<string> { "key1" })
            };
            var cp = new CheckpointFingerprint("cp_test", fps);

            Assert.AreEqual("cp_test", cp.CheckpointId);
            Assert.AreEqual(1, cp.Fingerprints.Count);
            Assert.AreEqual(1, cp.Fingerprints["rule_a"].Errors);
            Assert.AreEqual(2, cp.Fingerprints["rule_a"].Warnings);
            Assert.Contains("key1", cp.Fingerprints["rule_a"].IssueKeys);
        }
    }
}
