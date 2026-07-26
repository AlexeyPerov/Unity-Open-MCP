using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public class SearchAssetsToolTests
    {
        [Test]
        public void Execute_NoFilters_ReturnsMissingParameter()
        {
            var result = SearchAssetsTool.Execute("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'name'", result.ErrorMessage);
            StringAssert.Contains("'component'", result.ErrorMessage);
            StringAssert.Contains("'guid'", result.ErrorMessage);
        }

        [Test]
        public void Execute_OnlyTypeFilter_ReturnsMissingParameter()
        {
            // type alone is not enough — a name/component/guid is required.
            var result = SearchAssetsTool.Execute("{\"type\":\"prefab\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Execute_QueryWithNoMatches_ReturnsEmptyEnvelope()
        {
            // A guid that cannot exist; the result must be a valid empty envelope
            // (matchCount 0, empty matches array, truncated 0), not an error.
            var result = SearchAssetsTool.Execute(
                "{\"guid\":\"0000000000000000000000000000dead\",\"max_results\":5}");
            Assert.IsTrue(result.Success, "empty result is not an error");
            Assert.IsNotNull(result.Output);
            StringAssert.Contains("\"matchCount\":0", result.Output);
            StringAssert.Contains("\"matches\":[]", result.Output);
            StringAssert.Contains("\"truncated\":0", result.Output);
            // Query echoes back so the agent can confirm what was searched.
            StringAssert.Contains("\"guid\":\"0000000000000000000000000000dead\"", result.Output);
        }

        [Test]
        public void Execute_NameQueryEchoesQueryAndReasonsShape()
        {
            // Query with an unlikely name; verify the envelope carries the query
            // and a matches array (may be empty in the demo project).
            var result = SearchAssetsTool.Execute(
                "{\"name\":\"__UnlikelyAssetNameXYZ\",\"max_results\":10}");
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"name\":\"__UnlikelyAssetNameXYZ\"", result.Output);
            // matches is always present (array, possibly empty).
            Assert.IsTrue(result.Output.Contains("\"matches\":["),
                "envelope must carry a matches array");
        }

        // B19 — `max_results: 0` is the server-internal "unlimited" sentinel
        // (emitted by the MCP server when page_size is set so the cursor can
        // walk the full match set). Previously the bridge treated 0 as a hard
        // cap of zero, so every candidate was counted as truncated and zero
        // matches were returned. Now 0 means unlimited: the empty-result case
        // must still be a valid envelope (no truncation of nothing).
        [Test]
        public void Execute_MaxResultsZero_IsUnlimited_NotZeroCap()
        {
            var result = SearchAssetsTool.Execute(
                "{\"guid\":\"0000000000000000000000000000dead\",\"max_results\":0}");
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"matchCount\":0", result.Output);
            StringAssert.Contains("\"truncated\":0", result.Output,
                "0 must not truncate every candidate — it means unlimited, not a cap of zero");
        }

        // B19 — once the cap is reached, only REAL matches beyond the cap count
        // toward `truncated`. The previous form incremented `truncated` for
        // every unexamined candidate, so `matchCount` (= matches.Count +
        // truncated) ballooned to the total candidate count. With a query that
        // cannot match, a tiny cap must still report truncated: 0 (no matches
        // dropped), regardless of how many candidates AssetDatabase enumerated.
        [Test]
        public void Execute_SmallCap_DoesNotCountNonMatchesAsTruncated()
        {
            var result = SearchAssetsTool.Execute(
                "{\"guid\":\"0000000000000000000000000000dead\",\"max_results\":1}");
            Assert.IsTrue(result.Success);
            // No real matches → truncated must be 0, not (candidateCount - 1).
            StringAssert.Contains("\"truncated\":0", result.Output);
            StringAssert.Contains("\"matchCount\":0", result.Output);
        }
    }
}
