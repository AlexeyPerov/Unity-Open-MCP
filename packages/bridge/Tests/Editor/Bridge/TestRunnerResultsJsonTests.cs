using System.Collections.Generic;
using NUnit.Framework;
using UnityOpenMcpBridge.TestRunner;

namespace UnityOpenMcpBridge.Tests
{
    // Covers the result-JSON shaping that prevents MCP-client truncation on
    // large suites: includePasses filtering (summary counts stay complete) and
    // per-field length caps on message/stackTrace.
    public class TestRunnerResultsJsonTests
    {
        private static TestResultInfo R(string name, string status, string message = "", string stack = "") =>
            new TestResultInfo
            {
                Name = name,
                Status = status,
                Duration = 0.1,
                Message = message,
                StackTrace = stack,
            };

        [Test]
        public void IncludePasses_True_EmitsAllResults()
        {
            var results = new List<TestResultInfo>
            {
                R("A", "passed"),
                R("B", "failed", "boom"),
            };

            var json = TestRunnerService.BuildResultsJson("run1", "EditMode", results, includePasses: true);

            StringAssert.Contains("\"total\":2", json);
            StringAssert.Contains("\"passed\":1", json);
            StringAssert.Contains("\"failed\":1", json);
            StringAssert.Contains("\"includePasses\":true", json);
            // both names present
            StringAssert.Contains("\"A\"", json);
            StringAssert.Contains("\"B\"", json);
        }

        [Test]
        public void IncludePasses_False_OmitsPassedResults_SummaryUntouched()
        {
            var results = new List<TestResultInfo>
            {
                R("Pass1", "passed"),
                R("Pass2", "passed"),
                R("Fail1", "failed", "assertion failed"),
                R("Weird1", "inconclusive"),
            };

            var json = TestRunnerService.BuildResultsJson("run2", "EditMode", results, includePasses: false);

            // Summary still reports the FULL counts — includePasses only trims
            // the results array, not the summary.
            StringAssert.Contains("\"total\":4", json);
            StringAssert.Contains("\"passed\":2", json);
            StringAssert.Contains("\"failed\":1", json);
            StringAssert.Contains("\"inconclusive\":1", json);
            StringAssert.Contains("\"includePasses\":false", json);

            // Passed names must NOT appear in the results array.
            StringAssert.DoesNotContain("\"Pass1\"", json);
            StringAssert.DoesNotContain("\"Pass2\"", json);
            // Non-passed names must appear.
            StringAssert.Contains("\"Fail1\"", json);
            StringAssert.Contains("\"Weird1\"", json);
        }

        [Test]
        public void LongMessage_IsCappedWithIndicator()
        {
            var longMsg = new string('x', 5000);
            var longStack = new string('y', 8000);

            var results = new List<TestResultInfo>
            {
                R("Big", "failed", longMsg, longStack),
            };

            var json = TestRunnerService.BuildResultsJson("run3", "EditMode", results, includePasses: true);

            // The cap indicator proves truncation happened, and the original
            // (untruncated) tail must not be present.
            StringAssert.Contains("more chars]", json);
            StringAssert.DoesNotContain(new string('x', 3000), json, "message must be shorter than raw input");
            StringAssert.DoesNotContain(new string('y', 5000), json, "stackTrace must be shorter than raw input");
        }

        [Test]
        public void ShortFields_AreNotCapped()
        {
            var results = new List<TestResultInfo>
            {
                R("Small", "failed", "short message", "short stack"),
            };

            var json = TestRunnerService.BuildResultsJson("run4", "EditMode", results, includePasses: true);

            StringAssert.Contains("short message", json);
            StringAssert.Contains("short stack", json);
            StringAssert.DoesNotContain("more chars]", json);
        }

        [Test]
        public void EmptyResults_ValidJson()
        {
            var json = TestRunnerService.BuildResultsJson("run5", "EditMode", new List<TestResultInfo>(), includePasses: false);

            StringAssert.Contains("\"total\":0", json);
            StringAssert.Contains("\"results\":[]", json);
            StringAssert.Contains("\"includePasses\":false", json);
        }

        [Test]
        public void Cap_ShortString_ReturnedAsIs()
        {
            Assert.AreEqual("hi", TestRunnerService.Cap("hi", 100));
            Assert.AreEqual("", TestRunnerService.Cap("", 100));
        }

        [Test]
        public void Cap_LongString_TruncatedWithRemainingCount()
        {
            var input = new string('a', 100);
            var capped = TestRunnerService.Cap(input, 30);

            Assert.IsTrue(capped.StartsWith(new string('a', 30)), "prefix preserved");
            StringAssert.Contains("[70 more chars]", capped);
            Assert.IsTrue(capped.Length < input.Length + 30);
        }
    }
}
