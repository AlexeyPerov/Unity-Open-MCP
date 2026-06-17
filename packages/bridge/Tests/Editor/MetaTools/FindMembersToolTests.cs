using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public static class FindMembersToolTests
    {
        [Test]
        public static void Execute_EmptyQuery_ReturnsResults()
        {
            var result = FindMembersTool.Execute("{}");
            Assert.IsTrue(result.Success);
            Assert.IsNotNull(result.Output);
            Assert.IsTrue(result.Output.Contains("\"members\""));
        }

        [Test]
        public static void Execute_WithTypeFilter_ReturnsTypes()
        {
            var result = FindMembersTool.Execute("{\"query\":\"Transform\",\"kind\":\"type\",\"max_results\":5}");
            Assert.IsTrue(result.Success);
            Assert.IsTrue(result.Output.Contains("Transform"));
        }

        [Test]
        public static void Execute_MaxResultsClamped()
        {
            var result = FindMembersTool.Execute("{\"query\":\"\",\"max_results\":300}");
            Assert.IsTrue(result.Success);
        }

        // M13 T4.6 — `truncated` must always be present and accurately reflect
        // how many matches were dropped by max_results.
        [Test]
        public static void Execute_AlwaysReportsTruncated()
        {
            var result = FindMembersTool.Execute("{\"query\":\"\",\"max_results\":5}");
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"truncated\":", result.Output);
            StringAssert.Contains("\"count\":", result.Output);
        }

        [Test]
        public static void Execute_CapReached_ReportsNonZeroTruncated()
        {
            // Empty query + all kinds produces far more than 5 matches across
            // UnityEngine + UnityEditor + System; cap at 5 so truncation is
            // guaranteed non-zero.
            var result = FindMembersTool.Execute("{\"query\":\"\",\"kind\":\"all\",\"max_results\":5}");
            Assert.IsTrue(result.Success);
            var output = result.Output;
            StringAssert.Contains("\"count\":5", output);
            // truncated must be > 0 — there are thousands of public Unity APIs.
            StringAssert.Contains("\"truncated\":", output);
            Assert.IsFalse(output.Contains("\"truncated\":0"),
                $"Expected non-zero truncation with max_results:5. Output: {output}");
        }

        [Test]
        public static void Execute_NoCap_TruncatedIsZero()
        {
            // A specific narrow query that yields few matches; truncated should
            // be 0 when nothing is dropped.
            var result = FindMembersTool.Execute(
                "{\"query\":\"FindMembersTool\",\"kind\":\"type\",\"max_results\":50}");
            Assert.IsTrue(result.Success);
            Assert.IsTrue(result.Output.Contains("\"truncated\":0"),
                $"Expected truncated:0 for a narrow query. Output: {result.Output}");
        }
    }
}
