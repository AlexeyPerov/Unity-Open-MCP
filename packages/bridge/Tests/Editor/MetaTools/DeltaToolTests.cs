using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

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

        [SetUp]
        public void SetUp() => CheckpointStore.Clear();
        [TearDown]
        public void TearDown() => CheckpointStore.Clear();

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
        // surfaces checkpointLostOnReload so the agent can distinguish "wiped by
        // reload" from "id was never created".
        // -------------------------------------------------------------------

        [Test]
        public void Execute_EmptyStore_MissingCheckpoint_SurfacesLostOnReload()
        {
            // The store is empty (cleared by SetUp) — simulates a domain reload
            // that wiped the in-memory checkpoints. A delta request must surface
            // the checkpointLostOnReload flag + recommend checkpoint_create.
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
