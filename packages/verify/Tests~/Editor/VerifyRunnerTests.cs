using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class VerifyRunnerTests
    {
        [SetUp]
        public void SetUp()
        {
            VerifyRunner.ClearRules();
        }

        [TearDown]
        public void TearDown()
        {
            VerifyRunner.ClearRules();
        }

        [Test]
        public void RunScoped_UnknownRuleIds_ReturnsAvailableList()
        {
            VerifyRunner.RegisterRule(new StubRule("known_rule"));

            var scope = new VerifyScope(new[] { "Assets/dummy.prefab" });
            var result = VerifyRunner.RunScoped(scope, new[] { "nonexistent_rule" }, VerifyRunMode.Checkpoint);

            Assert.IsTrue(result.HasUnknownRules);
            Assert.AreEqual(1, result.UnknownRuleIds.Length);
            Assert.AreEqual("nonexistent_rule", result.UnknownRuleIds[0]);
            Assert.Contains("known_rule", result.AvailableRuleIds);
        }

        [Test]
        public void RunScoped_MixedKnownAndUnknown_SplitsCorrectly()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));
            VerifyRunner.RegisterRule(new StubRule("rule_b"));

            var scope = new VerifyScope(new[] { "Assets/dummy.prefab" });
            var result = VerifyRunner.RunScoped(scope,
                new[] { "rule_a", "ghost_rule" }, VerifyRunMode.Checkpoint);

            Assert.AreEqual(new[] { "ghost_rule" }, result.UnknownRuleIds);
            Assert.AreEqual(new[] { "rule_a" }, result.CategoriesRun);
        }

        [Test]
        public void RunScoped_NoRuleIds_RunsAllRegistered()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));
            VerifyRunner.RegisterRule(new StubRule("rule_b"));

            var scope = new VerifyScope(new[] { "Assets/dummy.prefab" });
            var result = VerifyRunner.RunScoped(scope, null, VerifyRunMode.Checkpoint);

            Assert.IsFalse(result.HasUnknownRules);
            Assert.AreEqual(2, result.CategoriesRun.Length);
        }

        [Test]
        public void RunScoped_EmptyRuleIds_RunsAllRegistered()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));

            var scope = new VerifyScope(new[] { "Assets/dummy.prefab" });
            var result = VerifyRunner.RunScoped(scope, new string[0], VerifyRunMode.Checkpoint);

            Assert.IsFalse(result.HasUnknownRules);
            Assert.AreEqual(new[] { "rule_a" }, result.CategoriesRun);
        }

        [Test]
        public void RunScoped_DispatchesToCorrectRules()
        {
            var ruleA = new StubRule("rule_a");
            var ruleB = new StubRule("rule_b");
            VerifyRunner.RegisterRule(ruleA);
            VerifyRunner.RegisterRule(ruleB);

            var scope = new VerifyScope(new[] { "Assets/Test.prefab" });
            var result = VerifyRunner.RunScoped(scope, new[] { "rule_b" }, VerifyRunMode.Validate);

            Assert.AreEqual(new[] { "rule_b" }, result.CategoriesRun);
            Assert.AreEqual(1, result.Issues.Count);
            Assert.AreEqual("rule_b", result.Issues[0].RuleId);
        }

        [Test]
        public void RunScoped_ExceptionInRule_DoesNotPropagate()
        {
            VerifyRunner.RegisterRule(new ThrowingRule("crashy"));

            var scope = new VerifyScope(new[] { "Assets/Test.prefab" });
            Assert.DoesNotThrow(() =>
                VerifyRunner.RunScoped(scope, new[] { "crashy" }, VerifyRunMode.Checkpoint));
        }

        [Test]
        public void RunScoped_ExceptionInRule_StillRunsOtherRules()
        {
            VerifyRunner.RegisterRule(new ThrowingRule("crashy"));
            VerifyRunner.RegisterRule(new StubRule("stable"));

            var scope = new VerifyScope(new[] { "Assets/Test.prefab" });
            var result = VerifyRunner.RunScoped(scope, null, VerifyRunMode.Checkpoint);

            Assert.AreEqual(2, result.CategoriesRun.Length);
            Assert.AreEqual(1, result.Issues.Count);
            Assert.AreEqual("stable", result.Issues[0].RuleId);
        }

        [Test]
        public void RunScoped_RecordsDuration()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));

            var scope = new VerifyScope(new[] { "Assets/dummy.prefab" });
            var result = VerifyRunner.RunScoped(scope, null, VerifyRunMode.Checkpoint);

            Assert.GreaterOrEqual(result.DurationMs, 0);
        }

        [Test]
        public void RunScoped_IssuesHaveValidKeys()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));

            var scope = new VerifyScope(new[] { "Assets/dummy.prefab" });
            var result = VerifyRunner.RunScoped(scope, null, VerifyRunMode.Validate);

            foreach (var issue in result.Issues)
            {
                var key = IssueKey.Build(issue);
                Assert.IsTrue(IssueKey.TryParse(key, out _, out _, out _, out _),
                    $"Issue key '{key}' should be parseable");
            }
        }

        [Test]
        public void CreateCheckpoint_ProducesFingerprints()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));
            VerifyRunner.RegisterRule(new StubRule("rule_b"));

            var scope = new VerifyScope(new[] { "Assets/dummy.prefab" });
            var cp = VerifyRunner.CreateCheckpoint(scope, null);

            Assert.IsNotNull(cp.CheckpointId);
            Assert.IsTrue(cp.CheckpointId.StartsWith("cp_"));
            Assert.AreEqual(2, cp.Fingerprints.Count);
            Assert.IsTrue(cp.Fingerprints.ContainsKey("rule_a"));
            Assert.IsTrue(cp.Fingerprints.ContainsKey("rule_b"));
        }

        [Test]
        public void CreateCheckpoint_FingerprintCountsIssues()
        {
            VerifyRunner.RegisterRule(new MultiIssueRule("rule_a"));

            var scope = new VerifyScope(new[] { "Assets/dummy.prefab" });
            var cp = VerifyRunner.CreateCheckpoint(scope, null);

            var fp = cp.Fingerprints["rule_a"];
            Assert.AreEqual(1, fp.Errors);
            Assert.AreEqual(2, fp.Warnings);
            Assert.AreEqual(3, fp.IssueKeys.Count);
        }

        class StubRule : IVerifyRule
        {
            public string Id { get; }
            public StubRule(string id) { Id = id; }

            public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
            {
                sink.Add(new VerifyIssue(Id, VerifySeverity.Warning, "Assets/Test.prefab", "stub_issue", "stub description"));
            }
        }

        class ThrowingRule : IVerifyRule
        {
            public string Id { get; }
            public ThrowingRule(string id) { Id = id; }

            public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
            {
                throw new System.InvalidOperationException("test exception");
            }
        }

        class MultiIssueRule : IVerifyRule
        {
            public string Id { get; }
            public MultiIssueRule(string id) { Id = id; }

            public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
            {
                sink.Add(new VerifyIssue(Id, VerifySeverity.Error, "Assets/A.prefab", "err_1", "error"));
                sink.Add(new VerifyIssue(Id, VerifySeverity.Warning, "Assets/A.prefab", "warn_1", "warning"));
                sink.Add(new VerifyIssue(Id, VerifySeverity.Warning, "Assets/B.prefab", "warn_2", "warning"));
            }
        }
    }
}
