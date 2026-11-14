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
            Assert.IsFalse(result.Success, "A refusal is a failed dispatch");
            Assert.AreEqual("unknown_action", result.ErrorCode);
            StringAssert.Contains("\"error\"", result.RefusalBody,
                "The gate-free response is the tool's own error payload");
        }

        [Test]
        public static void SpatialQuery_MissingActionReturnsErrorJson()
        {
            var result = BridgeToolRegistry.TryDispatch("unity_senses_spatial_query", "{}");

            Assert.IsNotNull(result, "Dispatch should return a result");
            Assert.IsFalse(result.Success, "A refusal is a failed dispatch");
            Assert.AreEqual("unknown_action", result.ErrorCode,
                "Missing action should surface unknown_action");
        }

        [Test]
        public static void SpatialQuery_RaycastMissingOriginReturnsErrorJson()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_senses_spatial_query",
                "{\"action\":\"raycast\",\"direction\":\"0,-1,0\"}");

            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public static void SpatialQuery_StringInstanceId_DoesNotThrowConvertError()
        {
            // Unity 6000.5+ wire form is a JSON string. ConvertValue must parse
            // it to long — a regression returns execution_error with
            // "String cannot be converted to Int64".
            var result = BridgeToolRegistry.TryDispatch(
                "unity_senses_spatial_query",
                "{\"action\":\"bounds\",\"instance_id\":\"568105589213726936\"}");

            Assert.IsNotNull(result);
            StringAssert.DoesNotContain("cannot be converted to type 'System.Int64'", result.ErrorMessage ?? "");
            // No such GO in an empty test host — target_not_found is the happy path.
            Assert.IsFalse(result.Success);
            Assert.AreEqual("target_not_found", result.ErrorCode);
        }
    }
}
