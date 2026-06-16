using System;
using System.Reflection;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    namespace TestFixtureA
    {
        [BridgeToolType]
        public class Tool_Single
        {
            [BridgeTool("test_single_tool", Enabled = true)]
            public string DoWork() => "ok";
        }
    }

    namespace TestFixtureB
    {
        [BridgeToolType]
        public partial class Tool_Partial
        {
        }

        public partial class Tool_Partial
        {
            [BridgeTool("test_partial_tool_a")]
            public string MethodA() => "a";
        }

        public partial class Tool_Partial
        {
            [BridgeTool("test_partial_tool_b")]
            public string MethodB() => "b";
        }
    }

    namespace TestFixtureC
    {
        [BridgeToolType]
        public class Tool_CollisionFirst
        {
            [BridgeTool("test_collision_tool")]
            public string First() => "first";
        }

        [BridgeToolType]
        public class Tool_CollisionSecond
        {
            [BridgeTool("test_collision_tool")]
            public string Second() => "second";
        }
    }

    namespace TestFixtureD
    {
        [BridgeToolType]
        public class Tool_Disabled
        {
            [BridgeTool("test_disabled_tool", Enabled = false)]
            public string Disabled() => "should not appear";
        }
    }

    namespace TestFixtureE
    {
        [BridgeResourceType]
        public class Resource_Single
        {
            [BridgeResource("Test Resource", "unity-open-mcp://test/resource")]
            public string GetResource() => "{\"test\":true}";
        }
    }

    namespace TestFixtureF
    {
        [BridgeToolType]
        public class Tool_WithParams
        {
            [BridgeTool("test_params_tool")]
            public string DoWork(string name, int count) => $"name={name},count={count}";
        }
    }

    namespace TestFixtureG
    {
        [BridgeResourceType]
        public class Resource_Disabled
        {
            [BridgeResource("Disabled Resource", "unity-open-mcp://test/disabled", Enabled = false)]
            public string GetDisabled() => "should not appear";
        }
    }

    namespace TestFixtureH
    {
        [BridgeToolType]
        public class Tool_Mutating
        {
            [BridgeTool("test_mutating_tool", IsMutating = true, Gate = GateMode.Enforce)]
            public string Mutate() => "mutated";
        }

        [BridgeToolType]
        public class Tool_MutatingWarn
        {
            [BridgeTool("test_mutating_warn_tool", IsMutating = true, Gate = GateMode.Warn)]
            public string MutateWarn() => "warned";
        }
    }

    public class AttributeScannerTests
    {
        [Test]
        public static void Scan_FindsSingleTool()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("test_single_tool"),
                "test_single_tool should be discovered");
        }

        [Test]
        public static void Scan_PartialClass_BothMethodsFound()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("test_partial_tool_a"),
                "test_partial_tool_a from partial class should be discovered");
            Assert.IsTrue(BridgeToolRegistry.Contains("test_partial_tool_b"),
                "test_partial_tool_b from partial class should be discovered");
        }

        [Test]
        public static void Scan_Collision_KeepsFirst()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("test_collision_tool", out var entry));
            Assert.AreEqual("first", entry.Method.Name);
        }

        [Test]
        public static void Scan_DisabledTool_Excluded()
        {
            Assert.IsFalse(BridgeToolRegistry.Contains("test_disabled_tool"),
                "disabled tool should not be registered");
        }

        [Test]
        public static void Scan_ResourceDiscovered()
        {
            var entry = BridgeResourceRegistry.FindByRoute("unity-open-mcp://test/resource");
            Assert.IsNotNull(entry, "test resource should be discovered");
            Assert.AreEqual("Test Resource", entry.Name);
        }

        [Test]
        public static void Scan_DisabledResource_Excluded()
        {
            var entry = BridgeResourceRegistry.FindByRoute("unity-open-mcp://test/disabled");
            Assert.IsNull(entry, "disabled resource should not be registered");
        }

        [Test]
        public static void TryDispatch_WithParams_ExtractsCorrectly()
        {
            var body = "{\"name\":\"hello\",\"count\":42}";
            var result = BridgeToolRegistry.TryDispatch("test_params_tool", body);
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            Assert.AreEqual("name=hello,count=42", result.Output);
        }

        [Test]
        public static void TryDispatch_MissingRequiredParam_ReturnsError()
        {
            var body = "{\"name\":\"hello\"}";
            var result = BridgeToolRegistry.TryDispatch("test_params_tool", body);
            Assert.IsNull(result, "Missing required parameter should return null (which maps to missing_parameter error in the server)");
        }
    }
}
