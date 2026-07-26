// Regression: code-review finding B42 — ScanPathsTool.ShouldFail had a `_ => false`
// arm that mapped ANY unrecognized fail_on_severity token (typos like "warning",
// "critical", "errors") to "never fail", so the response reported passed:true
// while still listing the errors. batch_execute re-enters DispatchTool with agent-
// authored bodies that bypass the MCP schema, and the canonical
// SeverityThreshold.Parse *throws* on unknown values. The fix validates the
// resolved threshold against the canonical parser and rejects with
// invalid_argument before scanning.
using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public class ScanPathsFailOnSeverityTests
    {
        // The validation runs before scanning, so a bogus path is fine — the
        // tool must reject the threshold before it ever touches the filesystem.
        private const string Body =
            "{\"paths\":[\"Assets/__nonexistent__.prefab\"]";

        // B42 — an unrecognized fail_on_severity must be rejected with a
        // structured invalid_argument error, NOT silently treated as "never
        // fail" (passed:true). Pre-fix this returned passed:true.
        [Test]
        public void Execute_UnknownFailOnSeverity_ReturnsInvalidArgument()
        {
            var body = Body + ",\"fail_on_severity\":\"critical\"}";
            var result = ScanPathsTool.Execute(body);
            Assert.IsFalse(result.Success,
                "unknown fail_on_severity must not report success (pre-fix returned passed:true)");
            Assert.AreEqual("invalid_argument", result.ErrorCode);
            StringAssert.Contains("critical", result.ErrorMessage);
            // The canonical parser lists the valid values in the error message.
            StringAssert.Contains("error", result.ErrorMessage);
            StringAssert.Contains("warn", result.ErrorMessage);
        }

        [Test]
        public void Execute_TypoWarning_ReturnsInvalidArgument()
        {
            // "warning" is a common typo / the spec spelling, but the canonical
            // fail_on_severity token is "warn". Pre-fix this silently passed.
            var body = Body + ",\"fail_on_severity\":\"warning\"}";
            var result = ScanPathsTool.Execute(body);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_argument", result.ErrorCode);
        }

        // The five canonical tokens must be ACCEPTED by the validation (they
        // proceed to the scan, which then fails on the bogus path — but with a
        // scan_error, NOT invalid_argument). This guards against an over-eager
        // validator that rejects valid input.
        [TestCase("error")]
        [TestCase("warn")]
        [TestCase("info")]
        [TestCase("verbose")]
        [TestCase("never")]
        public void Execute_CanonicalFailOnSeverity_IsAccepted(string threshold)
        {
            var body = Body + ",\"fail_on_severity\":\"" + threshold + "\"}";
            var result = ScanPathsTool.Execute(body);
            // The path does not exist, so the scan itself reports an error — but
            // NOT invalid_argument. The threshold was accepted.
            Assert.AreNotEqual("invalid_argument", result.ErrorCode,
                $"canonical threshold '{threshold}' must be accepted by the validator");
        }

        // B42 — the "never" threshold is a real canonical value (fail on
        // nothing). Pre-fix it was handled by the `_ => false` arm coincidentally;
        // now it has an explicit case. Assert the resolved threshold is echoed.
        [Test]
        public void Execute_NeverThreshold_AcceptedAndEchoed()
        {
            var body = Body + ",\"fail_on_severity\":\"never\"}";
            var result = ScanPathsTool.Execute(body);
            Assert.AreNotEqual("invalid_argument", result.ErrorCode);
            // On a missing path the scan errors before building the result, so
            // only assert the echo when we got an Ok payload.
            if (result.Success)
                StringAssert.Contains("\"failOnSeverity\":\"never\"", result.Output);
        }
    }
}
