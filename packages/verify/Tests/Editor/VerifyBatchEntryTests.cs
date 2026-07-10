using NUnit.Framework;
using UnityOpenMcpVerify.Batch;

namespace UnityOpenMcpVerify.Tests
{
    // Covers the VerifyBatchEntry orchestration entry path: arg extraction,
    // operation dispatch routing, and the ParseFlags validation branches.
    //
    // The three operation runners (RunScanAll / RunBaselineCreate /
    // RunRegressionCheck) call the live VerifyRunner, which requires a full
    // Unity project asset scan — those are integration paths exercised by the
    // Validation Suite and the headless -batchmode entry. This test class owns
    // the pure, deterministic surface: how args are sliced, how operations are
    // dispatched, and how every malformed flag surfaces a structured failure
    // envelope. This is where a regression in the batch CLI contract (wrong
    // exit code, missing usage hint, swallowed parse error) would hide.
    public static class VerifyBatchEntryTests
    {
        // -------------------------------------------------------------------
        // Arg extraction (ExtractToolArgs via Execute)
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_NoArgs_ReturnsFailWithUsageHint()
        {
            var (exitCode, json) = VerifyBatchEntry.Execute(new string[0]);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("No tool arguments", json);
            StringAssert.Contains("VerifyBatchEntry.Run", json,
                "usage hint must name the executeMethod entry point.");
        }

        [Test]
        public static void Execute_NoDoubleDash_FallsBackToExecuteMethodSlice()
        {
            // Without a "--" separator, args after "-executeMethod <method>"
            // are treated as tool args. A bare operation with no flags is an
            // unknown op only when the name is wrong; here we pass an unknown
            // op to exercise the fallback slice without hitting a live runner.
            var args = new[]
            {
                "-batchmode",
                "-executeMethod", "UnityOpenMcpVerify.Batch.VerifyBatchEntry.Run",
                "scan_allz", // typo — dispatches to the unknown-op branch
            };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("Unknown operation", json);
            StringAssert.Contains("scan_allz", json);
        }

        // -------------------------------------------------------------------
        // Operation dispatch routing
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_UnknownOperation_ListsValidOperations()
        {
            var args = new[] { "--", "frobnicate" };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("Unknown operation 'frobnicate'", json);
            // The error must list every valid op so an operator can self-correct.
            StringAssert.Contains("scan_all", json);
            StringAssert.Contains("baseline_create", json);
            StringAssert.Contains("regression_check", json);
        }

        // -------------------------------------------------------------------
        // --platform-profile validation
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_ScanAll_InvalidPlatformProfile_Fails()
        {
            var args = new[] { "--", "scan_all", "--platform-profile", "wasm" };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("Invalid platform-profile 'wasm'", json);
            StringAssert.Contains("mobile", json);
            StringAssert.Contains("desktop", json);
        }

        [Test]
        public static void Execute_ScanAll_MissingPlatformProfileValue_Fails()
        {
            // The flag is last with no value following.
            var args = new[] { "--", "scan_all", "--platform-profile" };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("--platform-profile requires a value", json);
        }

        // -------------------------------------------------------------------
        // --fail-on-severity validation
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_ScanAll_InvalidFailOnSeverity_Fails()
        {
            var args = new[] { "--", "scan_all", "--fail-on-severity", "panic" };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("Invalid fail-on-severity 'panic'", json);
        }

        [Test]
        public static void Execute_ScanAll_MissingFailOnSeverityValue_Fails()
        {
            var args = new[] { "--", "scan_all", "--fail-on-severity" };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("--fail-on-severity requires a value", json);
        }

        // -------------------------------------------------------------------
        // --regression-threshold validation
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_RegressionCheck_NonIntegerThreshold_Fails()
        {
            // regression_check also requires --baseline-path; but the parse
            // error for the threshold surfaces first (ParseFlags runs before
            // the baseline-path presence check).
            var args = new[]
            {
                "--", "regression_check",
                "--baseline-path", "baseline.json",
                "--regression-threshold", "lots",
            };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("Invalid regression-threshold 'lots'", json);
        }

        [Test]
        public static void Execute_RegressionCheck_NegativeThreshold_Fails()
        {
            var args = new[]
            {
                "--", "regression_check",
                "--baseline-path", "baseline.json",
                "--regression-threshold", "-1",
            };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("non-negative integer", json);
        }

        [Test]
        public static void Execute_RegressionCheck_MissingThresholdValue_Fails()
        {
            var args = new[]
            {
                "--", "regression_check",
                "--baseline-path", "baseline.json",
                "--regression-threshold",
            };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("requires an integer value", json);
        }

        // -------------------------------------------------------------------
        // --per-category-threshold validation
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_RegressionCheck_PerCategoryThreshold_Malformed_Fails()
        {
            // Missing the '=' separator.
            var args = new[]
            {
                "--", "regression_check",
                "--baseline-path", "baseline.json",
                "--per-category-threshold", "missing_requals",
            };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("Invalid --per-category-threshold 'missing_requals'", json);
            StringAssert.Contains("<ruleId>=<int>", json);
        }

        [Test]
        public static void Execute_RegressionCheck_PerCategoryThreshold_NegativeInt_Fails()
        {
            var args = new[]
            {
                "--", "regression_check",
                "--baseline-path", "baseline.json",
                "--per-category-threshold", "missing_refs=-1",
            };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("non-negative integer", json);
        }

        [Test]
        public static void Execute_RegressionCheck_PerCategoryThreshold_MissingValue_Fails()
        {
            var args = new[]
            {
                "--", "regression_check",
                "--baseline-path", "baseline.json",
                "--per-category-threshold",
            };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("--per-category-threshold requires a value", json);
        }

        // -------------------------------------------------------------------
        // --baseline-path presence (required for baseline_create + regression_check)
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_BaselineCreate_MissingBaselinePath_Fails()
        {
            var args = new[] { "--", "baseline_create" };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("--baseline-path is missing", json);
        }

        [Test]
        public static void Execute_RegressionCheck_MissingBaselinePath_Fails()
        {
            var args = new[] { "--", "regression_check" };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("--baseline-path is missing", json);
        }

        // -------------------------------------------------------------------
        // Unknown-flag rejection
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_ScanAll_UnknownFlag_Fails()
        {
            var args = new[] { "--", "scan_all", "--frobnicate", "yes" };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("Unknown argument '--frobnicate'", json);
        }

        // -------------------------------------------------------------------
        // --output-path / --baseline-path missing-value
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_ScanAll_MissingOutputPathValue_Fails()
        {
            var args = new[] { "--", "scan_all", "--output-path" };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("--output-path requires a value", json);
        }

        [Test]
        public static void Execute_BaselineCreate_MissingBaselinePathValue_Fails()
        {
            var args = new[] { "--", "baseline_create", "--baseline-path" };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            StringAssert.Contains("--baseline-path requires a value", json);
        }

        // -------------------------------------------------------------------
        // Failure-envelope shape: every error path produces a JSON envelope
        // with the operation + exitCode + error fields.
        // -------------------------------------------------------------------

        [Test]
        public static void Fail_EnvelopeCarriesOperationAndExitCode()
        {
            // The usage-fail path has a null operation; the unknown-op path
            // carries the (bad) operation name. Both must serialize as JSON
            // with exitCode = ExitFail (1).
            var args = new[] { "--", "bogus_op" };
            var (exitCode, json) = VerifyBatchEntry.Execute(args);
            Assert.AreEqual(VerifyBatchEntry.ExitFail, exitCode);
            // JsonUtility output is pretty-printed; exitCode appears as a key.
            StringAssert.Contains("\"exitCode\"", json);
            StringAssert.Contains("\"error\"", json);
            StringAssert.Contains("\"operation\"", json);
        }
    }
}
