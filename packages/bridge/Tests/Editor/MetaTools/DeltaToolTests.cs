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
    }
}
