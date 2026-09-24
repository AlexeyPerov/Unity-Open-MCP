using NUnit.Framework;
using UnityOpenMcpBridge.Update;

namespace UnityOpenMcpBridge.Tests.Update
{
    public class LatestVersionCheckTests
    {
        [Test]
        public void ParseRegistryPayload_AcceptsPlainVersion()
        {
            var result = LatestVersionCheck.ParseRegistryPayload("{\"name\":\"unity-open-mcp\",\"version\":\"1.2.3\"}");
            Assert.IsTrue(result.Success);
            Assert.AreEqual("1.2.3", result.Version);
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("{\"version\":\"latest\"}")]
        [TestCase("{\"version\":\"1.2.3-beta.1\"}")]
        public void ParseRegistryPayload_GarbageFailsWithoutFalseCurrent(string payload)
        {
            var result = LatestVersionCheck.ParseRegistryPayload(payload);
            Assert.IsFalse(result.Success);
            Assert.IsNull(result.Version);
            Assert.IsNotEmpty(result.Error);
        }

        [Test]
        public void ContainsExactBridgeTag_RejectsPrefixMatches()
        {
            const string body = "[{\"ref\":\"refs/tags/bridge-v1.2.30\"}]";
            Assert.IsFalse(LatestVersionCheck.ContainsExactBridgeTag(body, "1.2.3"));
            Assert.IsTrue(LatestVersionCheck.ContainsExactBridgeTag(body, "1.2.30"));
            Assert.IsTrue(LatestVersionCheck.ContainsExactBridgeTag("[{ \"ref\" : \"refs/tags/bridge-v1.2.3\" }]", "1.2.3"));
            Assert.IsFalse(LatestVersionCheck.ContainsExactBridgeTag("[]", "1.2.3"));
        }
    }
}
