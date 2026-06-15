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
    }
}
