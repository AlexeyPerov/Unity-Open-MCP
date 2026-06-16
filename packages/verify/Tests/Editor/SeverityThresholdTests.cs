using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Batch;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class SeverityThresholdTests
    {
        // -------------------------------------------------------------------
        // Parse
        // -------------------------------------------------------------------

        [TestCase("never", ExpectedResult = FailSeverity.Never)]
        [TestCase("verbose", ExpectedResult = FailSeverity.Verbose)]
        [TestCase("info", ExpectedResult = FailSeverity.Info)]
        [TestCase("warn", ExpectedResult = FailSeverity.Warn)]
        [TestCase("error", ExpectedResult = FailSeverity.Error)]
        [TestCase("ERROR", ExpectedResult = FailSeverity.Error)]
        [TestCase("Warn", ExpectedResult = FailSeverity.Warn)]
        public FailSeverity Parse_Accepts_KnownValues(string value)
        {
            return SeverityThreshold.Parse(value);
        }

        [TestCase(null)]
        [TestCase("")]
        public void Parse_NullOrEmpty_ReturnsNever(string value)
        {
            Assert.AreEqual(FailSeverity.Never, SeverityThreshold.Parse(value));
        }

        [TestCase("critical")]
        [TestCase("errors")]
        [TestCase(" ")]
        public void Parse_UnknownValue_Throws(string value)
        {
            var ex = Assert.Throws<ArgumentException>(() => SeverityThreshold.Parse(value));
            Assert.That(ex.Message, Does.Contain("Unknown fail-on-severity value"));
            // The guidance must list every accepted value so callers can self-correct.
            foreach (var valid in SeverityThreshold.ValidValues)
            {
                Assert.That(ex.Message, Does.Contain(valid));
            }
        }

        // -------------------------------------------------------------------
        // ToString — round-trips every enum member
        // -------------------------------------------------------------------

        [TestCase(FailSeverity.Never, ExpectedResult = "never")]
        [TestCase(FailSeverity.Verbose, ExpectedResult = "verbose")]
        [TestCase(FailSeverity.Info, ExpectedResult = "info")]
        [TestCase(FailSeverity.Warn, ExpectedResult = "warn")]
        [TestCase(FailSeverity.Error, ExpectedResult = "error")]
        public string ToString_RoundTrips(FailSeverity threshold)
        {
            return SeverityThreshold.ToString(threshold);
        }

        [Test]
        public void Parse_And_ToString_RoundTrip_ForEveryAcceptedValue()
        {
            foreach (var value in SeverityThreshold.ValidValues)
            {
                var parsed = SeverityThreshold.Parse(value);
                Assert.AreEqual(value, SeverityThreshold.ToString(parsed));
            }
        }

        // -------------------------------------------------------------------
        // ShouldFail — threshold × issue-severity matrix
        // -------------------------------------------------------------------

        [Test]
        public void ShouldFail_Never_Always_ReturnsFalse()
        {
            var result = ResultWith(5, 3);
            Assert.False(SeverityThreshold.ShouldFail(FailSeverity.Never, result));
        }

        [Test]
        public void ShouldFail_Error_RequiresAtLeastOneError()
        {
            // Errors present -> fail.
            Assert.True(SeverityThreshold.ShouldFail(FailSeverity.Error, ResultWith(1, 0)));
            Assert.True(SeverityThreshold.ShouldFail(FailSeverity.Error, ResultWith(2, 5)));
            // Only warnings -> must NOT fail at the Error threshold.
            Assert.False(SeverityThreshold.ShouldFail(FailSeverity.Error, ResultWith(0, 5)));
            // Clean -> must not fail.
            Assert.False(SeverityThreshold.ShouldFail(FailSeverity.Error, ResultWith(0, 0)));
        }

        [TestCase(FailSeverity.Warn)]
        [TestCase(FailSeverity.Info)]
        [TestCase(FailSeverity.Verbose)]
        public void ShouldFail_WarnAndBelow_FailsOnErrorsOrWarnings(FailSeverity threshold)
        {
            // Error alone, warning alone, or both -> fail.
            Assert.True(SeverityThreshold.ShouldFail(threshold, ResultWith(1, 0)));
            Assert.True(SeverityThreshold.ShouldFail(threshold, ResultWith(0, 1)));
            Assert.True(SeverityThreshold.ShouldFail(threshold, ResultWith(1, 1)));
            // Clean -> must not fail.
            Assert.False(SeverityThreshold.ShouldFail(threshold, ResultWith(0, 0)));
        }

        [Test]
        public void ShouldFail_NullResult_ReturnsFalse_ForEveryThreshold()
        {
            Assert.False(SeverityThreshold.ShouldFail(FailSeverity.Never, null));
            Assert.False(SeverityThreshold.ShouldFail(FailSeverity.Error, null));
            Assert.False(SeverityThreshold.ShouldFail(FailSeverity.Warn, null));
        }

        [Test]
        public void ShouldFail_ResultWithNullIssues_ReturnsFalse()
        {
            var result = new VerifyResult(null, new string[0], 0);
            // A null Issues list must not crash and must read as "no issues".
            Assert.False(SeverityThreshold.ShouldFail(FailSeverity.Error, result));
            Assert.False(SeverityThreshold.ShouldFail(FailSeverity.Warn, result));
        }

        [Test]
        public void ShouldFail_IgnoresIssues_WhenThresholdIsNever()
        {
            // Even a wall of errors must not fail when the threshold is Never.
            Assert.False(SeverityThreshold.ShouldFail(FailSeverity.Never, ResultWith(100, 100)));
        }

        // -------------------------------------------------------------------
        // helpers
        // -------------------------------------------------------------------

        /// <summary>Builds a VerifyResult with <paramref name="errors"/> Error issues
        /// and <paramref name="warnings"/> Warning issues (all dummy content).</summary>
        private static VerifyResult ResultWith(int errors, int warnings)
        {
            var issues = new List<VerifyIssue>();
            for (int i = 0; i < errors; i++)
            {
                issues.Add(new VerifyIssue("test_rule", VerifySeverity.Error,
                    "Assets/Test.prefab", "test_error", $"error {i}"));
            }
            for (int i = 0; i < warnings; i++)
            {
                issues.Add(new VerifyIssue("test_rule", VerifySeverity.Warning,
                    "Assets/Test.prefab", "test_warn", $"warn {i}"));
            }
            return new VerifyResult(issues, new[] { "test_rule" }, 0);
        }
    }
}
