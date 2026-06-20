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
    }
}
