using NUnit.Framework;
using UnityEngine;
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

        // -------------------------------------------------------------------
        // Regression: code-review finding B4 — find_members with
        // include_signatures:false emitted a stray quote after the unquoted
        // `parameters` array (SerializeMethod) and `isStatic` boolean
        // (SerializeProperty), corrupting the whole response body.
        // `…"parameters":[…]"}` and `…"isStatic":false"}` are unparseable.
        // find_members is neither direct-response nor mutating, so its output
        // is interpolated raw into the gate envelope — the entire body became
        // unparseable, not one member. The response must round-trip through a
        // real JSON parser with include_signatures:false.
        // -------------------------------------------------------------------

        [Test]
        public static void Execute_IncludeSignaturesFalse_Methods_ParseAsJson()
        {
            var result = FindMembersTool.Execute(
                "{\"query\":\"ToString\",\"kind\":\"method\",\"include_signatures\":false," +
                "\"max_results\":5}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            // The whole body must parse — the stray-quote bug made it unparseable.
            var parsed = JsonUtility.FromJson<MembersEnvelope>(result.Output);
            Assert.GreaterOrEqual(parsed.count, 1, "expected at least one ToString method");
            // The member entries must NOT carry a signature field when the flag
            // is off (proves the no-signatures branch ran).
            Assert.IsFalse(result.Output.Contains("\"signature\""),
                "include_signatures:false must not emit a signature field: " + result.Output);
        }

        [Test]
        public static void Execute_IncludeSignaturesFalse_Properties_ParseAsJson()
        {
            // Transform has well-known properties (position, rotation, ...).
            var result = FindMembersTool.Execute(
                "{\"query\":\"position\",\"kind\":\"property\",\"include_signatures\":false," +
                "\"max_results\":5}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            var parsed = JsonUtility.FromJson<MembersEnvelope>(result.Output);
            Assert.GreaterOrEqual(parsed.count, 1, "expected at least one position property");
            Assert.IsFalse(result.Output.Contains("\"signature\""),
                "include_signatures:false must not emit a signature field: " + result.Output);
        }

        [Test]
        public static void Execute_IncludeSignaturesTrue_StillParses()
        {
            // Regression guard: the fix must not break the include_signatures:true
            // path (the default). Both branches must emit valid JSON.
            var result = FindMembersTool.Execute(
                "{\"query\":\"ToString\",\"kind\":\"method\",\"include_signatures\":true," +
                "\"max_results\":5}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            var parsed = JsonUtility.FromJson<MembersEnvelope>(result.Output);
            Assert.GreaterOrEqual(parsed.count, 1);
            Assert.IsTrue(result.Output.Contains("\"signature\""),
                "include_signatures:true must emit a signature field: " + result.Output);
        }

        [System.Serializable]
        private class MembersEnvelope
        {
            public MemberEntry[] members;
            public int count;
            public int truncated;
        }

        [System.Serializable]
        private class MemberEntry
        {
            public string kind;
            public string name;
        }
    }
}
