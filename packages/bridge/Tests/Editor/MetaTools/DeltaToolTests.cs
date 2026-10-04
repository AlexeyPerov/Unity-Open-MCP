using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpVerify;

namespace UnityOpenMcpBridge.Tests
{
    [TestFixture]
    public class DeltaToolTests
    {
        // -------------------------------------------------------------------
        // Item F — a missing checkpoint is NOT a tool failure. Checkpoints are
        // session-scoped (in-memory) and cleared on recompile/domain reload/
        // restart, so returning a hard error would set isError:true on the MCP
        // response and block agent workflows. Instead the tool returns success
        // with an explicit `unavailable` warning + recovery guidance.
        // -------------------------------------------------------------------

        private IVerifyRule[] _originalRules;
        [SetUp]
        public void SetUp()
        {
            CheckpointStore.Clear();
            _originalRules = VerifyRunner.Rules.ToArray();
            VerifyRunner.ClearRules();
        }
        [TearDown]
        public void TearDown()
        {
            CheckpointStore.Clear();
            VerifyRunner.ClearRules();
            foreach (var rule in _originalRules) VerifyRunner.RegisterRule(rule);
        }

        private sealed class DeltaRule : IVerifyRule
        {
            public string Id => "delta_test";
            public bool Throw;
            public bool EmitPartial;
            public string Path = "Assets/Test.prefab";
            public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
            {
                if (EmitPartial) sink.Add(new VerifyIssue(Id, VerifySeverity.Error, Path, "broken", "Broken"));
                if (Throw) throw new InvalidOperationException("Deliberate scan failure");
            }
        }

        [Test]
        public void ScanAndValidate_EmitCanonicalIssueIdsForFixes()
        {
            var rule = new DeltaRule { EmitPartial = true, Path = "Assets/A|B%7C.prefab" };
            VerifyRunner.RegisterRule(rule);
            var body = "{\"paths\":[\"Assets\"],\"categories\":[\"delta_test\"]}";
            var key = IssueKey.Build(rule.Id, VerifySeverity.Error, rule.Path, "broken");
            foreach (var result in new[] { ScanPathsTool.Execute(body), ValidateEditTool.Execute(body) })
            {
                Assert.IsTrue(result.Success);
                StringAssert.Contains("\"issueId\":\"" + key + "\"", result.Output);
                CollectionAssert.AreEqual(new[] { rule.Path }, BridgeRequestBody.PathsFromIssueId(key));
            }
        }

        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        public void Execute_IncompleteCheckpointOrValidation_RefusesDelta(bool beforeFailed, bool afterFailed, bool partial)
        {
            var rule = new DeltaRule { Throw = afterFailed, EmitPartial = partial };
            VerifyRunner.RegisterRule(rule);
            var fingerprint = new CheckpointFingerprint("cp_test", new Dictionary<string, RuleFingerprint>
            {
                [rule.Id] = new RuleFingerprint(1, 0, new HashSet<string>
                { IssueKey.Build(rule.Id, VerifySeverity.Error, "Assets/Test.prefab", "broken") })
            }, beforeFailed ? new[] { rule.Id } : null);
            CheckpointStore.Store(new CheckpointStoreEntry
            {
                CheckpointId = "cp_test", Paths = new[] { "Assets/Test.prefab" },
                Categories = new[] { rule.Id }, Fingerprint = fingerprint,
            });
            var result = DeltaTool.Execute("{\"checkpoint_id\":\"cp_test\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("validation_incomplete", result.ErrorCode);
            StringAssert.Contains("\"rulesFailed\":[\"delta_test\"]", result.ErrorDetailJson);
            Assert.IsNull(result.Output, "Incomplete results must never report passed or resolved counts.");
            Assert.IsNotEmpty(result.NextSteps);
        }

        [Test]
        public void Execute_MissingCheckpointIdParameter_StillHardFails()
        {
            // A genuinely missing required parameter is a client bug, not a
            // lost-baseline situation — keep the hard error here.
            var result = DeltaTool.Execute("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Execute_UnknownCheckpoint_ReturnsUnavailableWarningNotError()
        {
            var result = DeltaTool.Execute("{\"checkpoint_id\":\"cp_gone\"}");
            Assert.IsTrue(result.Success,
                "missing checkpoint must not fail the tool call (would block agents via isError)");
            Assert.IsNull(result.ErrorCode);
            Assert.IsNull(result.ErrorMessage);
            StringAssert.Contains("\"unavailable\":true", result.Output);
            StringAssert.Contains("\"passed\":true", result.Output);
            StringAssert.Contains("\"agentNextSteps\":", result.Output);
            StringAssert.Contains("unity_open_mcp_validate_edit", result.Output,
                "recovery guidance should point at a direct-validation fallback");
        }

        [Test]
        public void Execute_UnknownCheckpoint_OutputIsNotAnErrorEnvelope()
        {
            // The MCP server maps { error: {...} } -> isError:true for direct-
            // response tools. The unavailable payload must NOT contain an `error`
            // field at the top level, otherwise it would still block agents.
            var result = DeltaTool.Execute("{\"checkpoint_id\":\"cp_gone\"}");
            StringAssert.DoesNotContain("\"error\"", result.Output);
        }

        // -------------------------------------------------------------------
        // T5.4 — when the store is empty (post-reload), a missing checkpoint
        // surfaces checkpointLostOnReload. The empty-store signal alone cannot
        // distinguish "wiped by reload" from "no checkpoint created this
        // session" (the store tracks no process-lifetime marker), so the
        // warning covers both; the flag still surfaces the reload hypothesis.
        // -------------------------------------------------------------------

        [Test]
        public void Execute_EmptyStore_MissingCheckpoint_SurfacesLostOnReload()
        {
            // The store is empty (cleared by SetUp) — the most likely cause is a
            // domain reload that wiped the in-memory checkpoints (it could also
            // be that none was ever created this session). A delta request must
            // surface the checkpointLostOnReload flag + recommend checkpoint_create.
            Assert.AreEqual(0, CheckpointStore.Count, "store must be empty (post-reload state)");

            var result = DeltaTool.Execute("{\"checkpoint_id\":\"cp_lost_to_reload\"}");

            Assert.IsTrue(result.Success,
                "a lost-on-reload checkpoint must not fail the tool call");
            StringAssert.Contains("\"checkpointLostOnReload\":true", result.Output,
                "empty store must surface checkpointLostOnReload");
            StringAssert.Contains("\"unavailable\":true", result.Output);
            StringAssert.Contains("domain reload", result.Output,
                "warning must explain the reload-loss cause");
            StringAssert.Contains("unity_open_mcp_checkpoint_create", result.Output,
                "agentNextSteps must recommend re-creating the baseline");
        }

        [Test]
        public void Execute_NonEmptyStore_UnknownCheckpoint_NoLostOnReloadFlag()
        {
            // When other checkpoints still exist, a specific unknown id was
            // probably never created (not wiped by reload) — the lostOnReload
            // flag must NOT be set.
            CheckpointStore.Store(new CheckpointStoreEntry
            {
                CheckpointId = "cp_other",
                Timestamp = "2026-01-01T00:00:00.0000000Z",
                Paths = new[] { "Assets" },
                Fingerprint = new CheckpointFingerprint("cp_other", null),
            });
            Assert.AreEqual(1, CheckpointStore.Count);

            var result = DeltaTool.Execute("{\"checkpoint_id\":\"cp_never_existed\"}");

            Assert.IsTrue(result.Success);
            StringAssert.DoesNotContain("\"checkpointLostOnReload\":true", result.Output,
                "non-empty store must NOT set checkpointLostOnReload for a merely-unknown id");
            StringAssert.Contains("\"unavailable\":true", result.Output);
        }
    }
}
