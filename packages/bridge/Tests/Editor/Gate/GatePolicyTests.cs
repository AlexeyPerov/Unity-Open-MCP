using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    [TestFixture]
    public class GatePolicyTests
    {
        // -------------------------------------------------------------------
        // ResolveOutcome — the mutate→gate decision matrix
        // -------------------------------------------------------------------

        [Test]
        public void ResolveOutcome_NewErrors_Enforce_Fails()
        {
            var delta = Delta(errors: 2);
            var (outcome, gateFailed) = GatePolicy.ResolveOutcome(GateMode.Enforce, delta);

            Assert.AreEqual(GateOutcome.Failed, outcome);
            Assert.True(gateFailed, "new errors in Enforce must hard-fail the gate");
        }

        [Test]
        public void ResolveOutcome_NewErrors_Warn_WarnsButDoesNotFail()
        {
            var delta = Delta(errors: 2);
            var (outcome, gateFailed) = GatePolicy.ResolveOutcome(GateMode.Warn, delta);

            Assert.AreEqual(GateOutcome.Warned, outcome);
            Assert.False(gateFailed, "warn mode must not hard-fail on errors");
        }

        [Test]
        public void ResolveOutcome_NewWarnings_Warn_Warns()
        {
            var delta = Delta(warnings: 3);
            var (outcome, gateFailed) = GatePolicy.ResolveOutcome(GateMode.Warn, delta);

            Assert.AreEqual(GateOutcome.Warned, outcome);
            Assert.False(gateFailed);
        }

        [Test]
        public void ResolveOutcome_NewWarnings_Enforce_Passes()
        {
            // In Enforce mode only errors block; warnings alone pass.
            var delta = Delta(warnings: 3);
            var (outcome, gateFailed) = GatePolicy.ResolveOutcome(GateMode.Enforce, delta);

            Assert.AreEqual(GateOutcome.Passed, outcome);
            Assert.False(gateFailed);
        }

        [Test]
        public void ResolveOutcome_NoNewIssues_Passes()
        {
            var delta = Delta();
            var (outcome, gateFailed) = GatePolicy.ResolveOutcome(GateMode.Enforce, delta);

            Assert.AreEqual(GateOutcome.Passed, outcome);
            Assert.False(gateFailed);
        }

        [Test]
        public void ResolveOutcome_ErrorsTakePriority_OverWarnings()
        {
            // Both new errors and warnings present -> the error branch wins.
            var delta = Delta(errors: 1, warnings: 5);
            var (outcome, _) = GatePolicy.ResolveOutcome(GateMode.Enforce, delta);
            Assert.AreEqual(GateOutcome.Failed, outcome);
        }

        // -------------------------------------------------------------------
        // GenerateAgentNextSteps — actionable guidance per outcome
        // -------------------------------------------------------------------

        [Test]
        public void NextSteps_Failed_MentionsErrorCount_AndValidateEdit()
        {
            var delta = Delta(errors: 2, issueKeys: new[] { "missing_references|ERROR|Assets/A.prefab|missing_script" });
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Failed);

            CollectionAssert.IsNotEmpty(steps);
            Assert.That(Join(steps), Does.Contain("2 new error(s)"));
            Assert.That(Join(steps), Does.Contain("validate_edit"));
        }

        [Test]
        public void NextSteps_Failed_MissingScriptKey_SuggestsRemoveMissingScriptFix()
        {
            var delta = Delta(errors: 1, issueKeys: new[] { "missing_references|ERROR|Assets/A.prefab|MISSING_SCRIPT" });
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Failed);

            Assert.That(Join(steps), Does.Contain("remove_missing_script"), "must link the known fix for MISSING_SCRIPT");
            Assert.That(Join(steps), Does.Contain("apply_fix"));
        }

        [Test]
        public void NextSteps_Failed_MissingGuidKey_SuggestsRelinkBrokenGuidFix()
        {
            // T2.4: broken PPtr GUIDs (missing_guid) now have a fix provider —
            // next-step guidance must point at relink_broken_guid.
            var delta = Delta(errors: 1, issueKeys: new[] { "missing_references|ERROR|Assets/A.prefab|missing_guid" });
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Failed);

            Assert.That(Join(steps), Does.Contain("relink_broken_guid"),
                "must link the relink fix for missing_guid");
            Assert.That(Join(steps), Does.Contain("apply_fix"));
        }

        [Test]
        public void NextSteps_Failed_BrokenDependencyKey_SuggestsRelinkBrokenGuidFix()
        {
            // T2.4: forward-graph broken_dependency issues share the same
            // root cause (unresolved external GUID) and use the same fix.
            var delta = Delta(errors: 1, issueKeys: new[] { "dependencies|ERROR|Assets/A.prefab|broken_dependency" });
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Failed);

            Assert.That(Join(steps), Does.Contain("relink_broken_guid"),
                "must link the relink fix for broken_dependency");
        }

        [Test]
        public void NextSteps_Warned_WithErrors_UsesWarnModeLabel()
        {
            var delta = Delta(errors: 1, issueKeys: new[] { "scene_prefab_health|ERROR|Assets/S.unity|hotspot" });
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Warned);

            Assert.That(Join(steps), Does.Contain("warn mode"));
            // A non-MISSING_SCRIPT issue must NOT fabricate a fix suggestion.
            Assert.That(Join(steps), Does.Not.Contain("remove_missing_script"));
        }

        [Test]
        public void NextSteps_Warned_WarningsOnly_SuggestsReviewGuidance()
        {
            var delta = Delta(warnings: 4);
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Warned);

            Assert.That(Join(steps), Does.Contain("4 new warning(s)"));
            Assert.That(Join(steps), Does.Contain("validate_edit"));
        }

        [Test]
        public void NextSteps_Passed_WithResolvedErrors_NotesResolution()
        {
            var delta = Delta(resolvedErrors: 3);
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Passed);

            Assert.That(Join(steps), Does.Contain("3 previously reported error(s) resolved"));
        }

        [Test]
        public void NextSteps_Passed_Clean_ReportsNoNewIssues()
        {
            var delta = Delta();
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Passed);

            Assert.That(Join(steps), Does.Contain("no new issues detected"));
        }

        [Test]
        public void NextSteps_Failed_MalformedIssueKey_DoesNotCrash()
        {
            // A key with fewer than 4 pipe-separated parts must not throw.
            var delta = Delta(errors: 1, issueKeys: new[] { "garbage" });
            var steps = GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Failed);

            CollectionAssert.IsNotEmpty(steps);
            Assert.That(Join(steps), Does.Contain("Review the affected asset"));
        }

        [Test]
        public void NextSteps_Failed_NullIssueKeys_DoesNotCrash()
        {
            var delta = Delta(errors: 1, issueKeys: null);
            Assert.DoesNotThrow(() => GatePolicy.GenerateAgentNextSteps(delta, GateOutcome.Failed));
        }

        // -------------------------------------------------------------------
        // ParseIssueKey — robustness
        // -------------------------------------------------------------------

        [Test]
        public void ParseIssueKey_WellFormed_ParsesAllParts()
        {
            var parsed = GatePolicy.ParseIssueKey("missing_references|ERROR|Assets/A.prefab|MISSING_SCRIPT");

            Assert.NotNull(parsed);
            var p = parsed.Value;
            Assert.AreEqual("missing_references", p.CategoryId);
            Assert.AreEqual("ERROR", p.Severity);
            Assert.AreEqual("Assets/A.prefab", p.AssetPath);
            Assert.AreEqual("MISSING_SCRIPT", p.IssueCode);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("only|three|parts")]
        [TestCase("a|b|c|d|e")]
        public void ParseIssueKey_Malformed_ReturnsNull(string key)
        {
            Assert.Null(GatePolicy.ParseIssueKey(key));
        }

        // -------------------------------------------------------------------
        // ParseMode — string → GateMode
        // -------------------------------------------------------------------

        [TestCase("warn", ExpectedResult = GateMode.Warn)]
        [TestCase("off", ExpectedResult = GateMode.Off)]
        [TestCase("enforce", ExpectedResult = GateMode.Enforce)]
        [TestCase("", ExpectedResult = GateMode.Enforce)]
        [TestCase(null, ExpectedResult = GateMode.Enforce)]
        [TestCase("garbage", ExpectedResult = GateMode.Enforce)]
        [TestCase("WARN", ExpectedResult = GateMode.Enforce, Description = "mode parsing is case-sensitive; unknown → Enforce")]
        public GateMode ParseMode_Maps_KnownAndUnknownValues(string mode)
        {
            return GatePolicy.ParseMode(mode);
        }

        // -------------------------------------------------------------------
        // helpers
        // -------------------------------------------------------------------

        private static DeltaData Delta(
            int errors = 0, int warnings = 0,
            int resolvedErrors = 0,
            string[] issueKeys = null)
        {
            return new DeltaData
            {
                NewErrors = errors,
                NewWarnings = warnings,
                ResolvedErrors = resolvedErrors,
                NewIssueKeys = issueKeys ?? new string[0],
            };
        }

        private static string Join(string[] steps) => string.Join("\n", steps);
    }
}
