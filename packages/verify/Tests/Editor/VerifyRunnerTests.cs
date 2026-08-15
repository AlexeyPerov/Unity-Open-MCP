using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityOpenMcpVerify;

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

        // A rule that throws must be distinguishable from a rule that ran
        // clean: previously the exception was swallowed with only a console
        // warning, so an incomplete scan was indistinguishable from a healthy
        // one (a crashing rule produced a vacuous all-clear on both sides of
        // the gate delta).
        [Test]
        public void RunScoped_ExceptionInRule_RecordsRulesFailed()
        {
            VerifyRunner.RegisterRule(new ThrowingRule("crashy"));

            var scope = new VerifyScope(new[] { "Assets/Test.prefab" });
            var result = VerifyRunner.RunScoped(scope, new[] { "crashy" }, VerifyRunMode.Checkpoint);

            Assert.IsTrue(result.HasFailedRules);
            CollectionAssert.AreEquivalent(new[] { "crashy" }, result.RulesFailed);
        }

        [Test]
        public void RunScoped_ExceptionInOneRule_ListsOnlyThatRule()
        {
            VerifyRunner.RegisterRule(new ThrowingRule("crashy"));
            VerifyRunner.RegisterRule(new StubRule("stable"));

            var scope = new VerifyScope(new[] { "Assets/Test.prefab" });
            var result = VerifyRunner.RunScoped(scope, null, VerifyRunMode.Validate);

            Assert.IsTrue(result.HasFailedRules);
            CollectionAssert.AreEquivalent(new[] { "crashy" }, result.RulesFailed);
        }

        [Test]
        public void RunScoped_AllRulesClean_NoRulesFailed()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));

            var scope = new VerifyScope(new[] { "Assets/Test.prefab" });
            var result = VerifyRunner.RunScoped(scope, null, VerifyRunMode.Validate);

            Assert.IsFalse(result.HasFailedRules);
            Assert.AreEqual(0, result.RulesFailed.Length);
        }

        [Test]
        public void CreateCheckpoint_ExceptionInRule_OmitsFingerprint_AndCarriesRulesFailed()
        {
            // A failed rule contributed no issues, so a fingerprint for it
            // would claim a clean baseline it never established. The
            // fingerprint must be omitted and the failure carried on the
            // checkpoint so the gate can distrust its delta.
            VerifyRunner.RegisterRule(new ThrowingRule("crashy"));
            VerifyRunner.RegisterRule(new StubRule("stable"));

            var scope = new VerifyScope(new[] { "Assets/Test.prefab" });
            var cp = VerifyRunner.CreateCheckpoint(scope, null);

            Assert.IsFalse(cp.Fingerprints.ContainsKey("crashy"));
            Assert.IsTrue(cp.Fingerprints.ContainsKey("stable"));
            CollectionAssert.AreEquivalent(new[] { "crashy" }, cp.RulesFailed);
        }

        [Test]
        public void CreateCheckpoint_AllRulesClean_NoRulesFailed()
        {
            VerifyRunner.RegisterRule(new StubRule("rule_a"));

            var scope = new VerifyScope(new[] { "Assets/dummy.prefab" });
            var cp = VerifyRunner.CreateCheckpoint(scope, null);

            Assert.AreEqual(0, cp.RulesFailed.Length);
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

        // The platform profile rides on the scope (default "desktop"; the
        // batch entry threads --platform-profile through so a mobile scan_all
        // actually runs the mobile-gated shader detections).
        [Test]
        public void VerifyScope_PlatformProfile_DefaultsToDesktop_AndCarriesOverride()
        {
            Assert.AreEqual("desktop", new VerifyScope(null).PlatformProfile);
            Assert.AreEqual("desktop", new VerifyScope(null, platformProfile: null).PlatformProfile);
            Assert.AreEqual("mobile", new VerifyScope(null, platformProfile: "mobile").PlatformProfile);
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
