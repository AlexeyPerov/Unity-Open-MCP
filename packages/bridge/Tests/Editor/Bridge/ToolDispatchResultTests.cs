using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    public static class ToolDispatchResultTests
    {
        [Test]
        public static void Ok_NoOutput_SuccessWithNullOutput()
        {
            var result = ToolDispatchResult.Ok();
            Assert.IsTrue(result.Success);
            Assert.IsNull(result.Output);
            Assert.IsNull(result.ErrorCode);
            Assert.IsNull(result.ErrorMessage);
        }

        [Test]
        public static void Ok_WithOutput_SuccessWithOutput()
        {
            var result = ToolDispatchResult.Ok("\"hello\"");
            Assert.IsTrue(result.Success);
            Assert.AreEqual("\"hello\"", result.Output);
        }

        [Test]
        public static void Fail_SetsErrorFields()
        {
            var result = ToolDispatchResult.Fail("test_error", "Something went wrong");
            Assert.IsFalse(result.Success);
            Assert.IsNull(result.Output);
            Assert.AreEqual("test_error", result.ErrorCode);
            Assert.AreEqual("Something went wrong", result.ErrorMessage);
        }

        // --- FromRefusalBody: a tool's own {"error":{...}} body is a failure ---

        [Test]
        public static void FromRefusalBody_PlainError_IsFailedWithCodeAndMessage()
        {
            var body = "{\"error\":{\"code\":\"play_mode_required\",\"message\":\"Enter \\\"play\\\" mode first.\"}}";
            var result = ToolDispatchResult.FromRefusalBody(body);
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.IsFalse(result.PartialCommit, "a refusal committed nothing");
            Assert.IsNull(result.Output);
            Assert.AreEqual("play_mode_required", result.ErrorCode);
            Assert.AreEqual("Enter \"play\" mode first.", result.ErrorMessage);
            Assert.IsNull(result.ErrorDetailJson);
            Assert.IsNull(result.NextSteps);
            Assert.AreEqual(body, result.RefusalBody);
        }

        [Test]
        public static void FromRefusalBody_KeepsErrorMembersSiblingsAndNextSteps()
        {
            var body =
                "{\"error\":{\"code\":\"ambiguous_target\",\"message\":\"2 matches\"," +
                "\"candidates\":[\"A/Play\",\"B/Play\"],\"matches\":[{\"index\":0,\"text\":\"Play, now\"}]}," +
                "\"dirtyScenes\":[\"Assets/A.unity\"]," +
                "\"agentNextSteps\":[\"Pass target_index.\",\"Or narrow with under.\"]}";
            var result = ToolDispatchResult.FromRefusalBody(body);
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("ambiguous_target", result.ErrorCode);
            Assert.AreEqual("2 matches", result.ErrorMessage);
            Assert.AreEqual(
                "{\"candidates\":[\"A/Play\",\"B/Play\"],\"matches\":[{\"index\":0,\"text\":\"Play, now\"}]," +
                "\"dirtyScenes\":[\"Assets/A.unity\"]}",
                result.ErrorDetailJson);
            CollectionAssert.AreEqual(new[] { "Pass target_index.", "Or narrow with under." }, result.NextSteps);

            // The gate envelope carries all of it: detail inside mutation.error,
            // the tool's next steps as the envelope's agentNextSteps.
            var json = BridgeJson.BuildGateEnvelope(GatePolicy.Skipped(result, "mutation_failed"), "enforce", LifecyclePolicy.None);
            StringAssert.Contains("\"mutation\":{\"success\":false,\"output\":null,\"error\":{\"code\":\"ambiguous_target\",\"message\":\"2 matches\",\"candidates\":[\"A/Play\",\"B/Play\"]", json);
            StringAssert.Contains("\"dirtyScenes\":[\"Assets/A.unity\"]}}", json);
            StringAssert.Contains("\"agentNextSteps\":[\"Pass target_index.\",\"Or narrow with under.\"]", json);
            Assert.IsTrue(BridgeJson.IsCompleteJson(json), json);
        }

        [Test]
        public static void FromRefusalBody_SiblingNeverReplacesAnErrorMember()
        {
            var result = ToolDispatchResult.FromRefusalBody(
                "{\"error\":{\"code\":\"x\",\"message\":\"m\",\"detected\":1},\"detected\":2,\"code\":\"other\",\"agentNextSteps\":\"not an array\"}");
            Assert.IsNotNull(result);
            Assert.AreEqual("x", result.ErrorCode);
            Assert.AreEqual("{\"detected\":1,\"agentNextSteps\":\"not an array\"}", result.ErrorDetailJson);
            Assert.IsNull(result.NextSteps);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("ok")]
        [TestCase("\"error\"")]
        [TestCase("[{\"error\":{\"code\":\"x\"}}]")]
        [TestCase("{\"status\":\"ok\",\"count\":2}")]
        // `error` is not an object with a code: an operation's message, a counter.
        [TestCase("{\"kind\":\"wait\",\"error\":\"The game threw\"}")]
        [TestCase("{\"log\":3,\"warning\":1,\"error\":2}")]
        [TestCase("{\"error\":null}")]
        [TestCase("{\"error\":{\"message\":\"no code\"}}")]
        [TestCase("{\"error\":{\"code\":404}}")]
        // Only a top-level `error` is the tool's refusal.
        [TestCase("{\"status\":\"ok\",\"settle\":{\"state\":\"failed\",\"error\":{\"code\":\"x\",\"message\":\"m\"}}}")]
        [TestCase("{\"results\":[{\"error\":{\"code\":\"x\",\"message\":\"m\"}}]}")]
        public static void FromRefusalBody_OtherOutputs_AreNotRefusals(string output)
        {
            Assert.IsNull(ToolDispatchResult.FromRefusalBody(output));
        }

        [Test]
        public static void FromRefusalBody_ErrorNeedNotBeTheFirstKey()
        {
            var result = ToolDispatchResult.FromRefusalBody(
                "{\"status\":\"error\",\"runId\":\"r1\",\"error\":{\"code\":\"run_failed\",\"message\":\"boom\"}}");
            Assert.IsNotNull(result);
            Assert.AreEqual("run_failed", result.ErrorCode);
            Assert.AreEqual("{\"status\":\"error\",\"runId\":\"r1\"}", result.ErrorDetailJson);
        }
    }
}
