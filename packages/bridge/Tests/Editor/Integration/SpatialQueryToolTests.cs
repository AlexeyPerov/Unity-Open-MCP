using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    public class SpatialQueryToolTests
    {
        [Test]
        public static void SpatialQueryTool_RegisteredInRegistry()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_senses_spatial_query"),
                "unity_senses_spatial_query should be discovered by the registry");
        }

        [Test]
        public static void SpatialQueryTool_IsNonMutating()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_spatial_query", out var entry));
            Assert.IsFalse(entry.IsMutating,
                "unity_senses_spatial_query should be non-mutating (read-only)");
        }

        [Test]
        public static void SpatialQueryTool_GateIsOff()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_spatial_query", out var entry));
            Assert.AreEqual(GateMode.Off, entry.Gate,
                "unity_senses_spatial_query should have gate off (non-mutating)");
        }

        [Test]
        public static void SpatialQueryTool_HasReadOnlyHint()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_spatial_query", out var entry));
            Assert.IsTrue(entry.ReadOnlyHint,
                "unity_senses_spatial_query should have ReadOnlyHint = true");
        }

        [Test]
        public static void SpatialQuery_UnknownActionReturnsErrorJson()
        {
            var result = BridgeToolRegistry.TryDispatch("unity_senses_spatial_query", "{\"action\":\"bogus\"}");

            Assert.IsNotNull(result, "Dispatch should return a result");
            Assert.IsTrue(result.Success, "Dispatch should succeed (error is in-payload)");
            Assert.IsNotNull(result.Output, "Output should not be null");
            StringAssert.Contains("\"error\"", result.Output,
                "Output should be an error payload");
            StringAssert.Contains("unknown_action", result.Output,
                "Error code should be unknown_action");
        }

        [Test]
        public static void SpatialQuery_MissingActionReturnsErrorJson()
        {
            var result = BridgeToolRegistry.TryDispatch("unity_senses_spatial_query", "{}");

            Assert.IsNotNull(result, "Dispatch should return a result");
            Assert.IsTrue(result.Success, "Dispatch should succeed (error is in-payload)");
            Assert.IsNotNull(result.Output, "Output should not be null");
            StringAssert.Contains("unknown_action", result.Output,
                "Missing action should surface unknown_action");
        }

        [Test]
        public static void SpatialQuery_RaycastMissingOriginReturnsErrorJson()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_senses_spatial_query",
                "{\"action\":\"raycast\",\"direction\":\"0,-1,0\"}");

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("missing_parameter", result.Output);
        }
    }
}
