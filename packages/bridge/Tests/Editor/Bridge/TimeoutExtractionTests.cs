using NUnit.Framework;

namespace UnityOpenMcpBridge.Tests
{
    // Covers the timeout-extraction + clamping path that wraps every
    // DirectResponseTool dispatch (including unity_senses_run_tests). The clamp
    // ceiling must match the documented run-tests schema maximum; previously it
    // was 300000, silently truncating a caller's explicit large value below the
    // advertised 600000.
    public class TimeoutExtractionTests
    {
        [Test]
        public void MissingTimeout_ReturnsDefault()
        {
            Assert.AreEqual(
                30000,
                BridgeRequestBody.ExtractTimeoutMs("{}"),
                "tools without a timeout_ms declaration keep the 30s default"
            );
            Assert.AreEqual(30000, BridgeRequestBody.ExtractTimeoutMs(""));
            Assert.AreEqual(30000, BridgeRequestBody.ExtractTimeoutMs(null!));
        }

        [Test]
        public void ExplicitTimeout_IsPassedThrough()
        {
            Assert.AreEqual(60000, BridgeRequestBody.ExtractTimeoutMs("{\"timeout_ms\":60000}"));
        }

        [Test]
        public void CallerOmitsTimeout_SchemaLayer_FillsBeforeBridge()
        {
            // The MCP server's schema-default layer now injects timeout_ms from
            // the tool schema before the request reaches the bridge. This is a
            // behavioural note: the bridge still treats an absent field as 30s,
            // which is correct for tools whose schema default IS 30s.
            // run_tests documents 60000; the MCP layer adds it, so the bridge
            // receives 60000 and returns it verbatim (below MaxTimeoutMs).
            Assert.AreEqual(60000, BridgeRequestBody.ExtractTimeoutMs("{\"timeout_ms\":60000}"));
        }

        [Test]
        public void Timeout_BelowMinimum_ClampedTo1000()
        {
            Assert.AreEqual(1000, BridgeRequestBody.ExtractTimeoutMs("{\"timeout_ms\":0}"));
            Assert.AreEqual(1000, BridgeRequestBody.ExtractTimeoutMs("{\"timeout_ms\":-5}"));
        }

        [Test]
        public void Timeout_AboveMaximum_ClampedToSchemaCeiling_600000()
        {
            // Regression: MaxTimeoutMs was 300000, clamping a documented-max
            // value of 600000 down to 300000. It must now pass through.
            Assert.AreEqual(
                600000,
                BridgeRequestBody.ExtractTimeoutMs("{\"timeout_ms\":600000}"),
                "600000 is the documented run-tests maximum and must survive the clamp"
            );
            Assert.AreEqual(
                600000,
                BridgeRequestBody.ExtractTimeoutMs("{\"timeout_ms\":999999999}"),
                "values beyond the documented maximum clamp down to 600000"
            );
        }

        [Test]
        public void Timeout_ResilientToWhitespaceAndJunk()
        {
            Assert.AreEqual(
                60000,
                BridgeRequestBody.ExtractTimeoutMs("{\"timeout_ms\":   60000   }"),
                "whitespace around the number is tolerated"
            );
            Assert.AreEqual(
                30000,
                BridgeRequestBody.ExtractTimeoutMs("{\"timeout_ms\":\"oops\"}"),
                "non-numeric value falls back to default rather than throwing"
            );
            Assert.AreEqual(
                30000,
                BridgeRequestBody.ExtractTimeoutMs("{\"timeout_ms\":}"),
                "missing number falls back to default"
            );
        }
    }
}
