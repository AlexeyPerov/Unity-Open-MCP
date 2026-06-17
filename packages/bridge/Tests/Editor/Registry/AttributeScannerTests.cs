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

    namespace TestFixtureI
    {
        // M13 T4.1 — verifies [BridgeTool(Lifecycle = ...)] flows through the
        // registry scanner onto the BridgeToolEntry so the dispatcher and dirty
        // guard see the declared policy.
        [BridgeToolType]
        public class Tool_WithLifecycle
        {
            [BridgeTool("test_lifecycle_settle_tool",
                IsMutating = true, Gate = GateMode.Enforce,
                Lifecycle = LifecyclePolicy.EditorSettle)]
            public string Settle() => "settled";

            [BridgeTool("test_lifecycle_restart_tool",
                IsMutating = true, Gate = GateMode.Enforce,
                Lifecycle = LifecyclePolicy.RestartThenSettle)]
            public string Restart() => "restarted";
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
            // BridgeToolEntry.Method is the CLR MethodInfo; its Name is the C# method
            // name, not the return value. First-wins policy (ScanAssembly skips a
            // duplicate tool name) means the registered method is Tool_CollisionFirst.First().
            Assert.AreEqual("First", entry.Method.Name);
            // Confirm first-wins semantically: invoking must return the first tool's value.
            var instance = entry.GetInstance();
            Assert.AreEqual("first", entry.Method.Invoke(instance, null));
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
            // Missing a required (non-nullable, no default) parameter must surface as a
            // non-null failed result carrying the missing_parameter code. Returning null
            // is reserved by TryDispatch for unknown-tool only (the HTTP layer maps null
            // -> tool_not_found, and a missing_parameter Fail -> the gate error envelope).
            var body = "{\"name\":\"hello\"}";
            var result = BridgeToolRegistry.TryDispatch("test_params_tool", body);
            Assert.IsNotNull(result, "Missing required parameter must not map to unknown-tool (null)");
            Assert.IsFalse(result.Success, "Dispatch must fail when a required parameter is missing");
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        // M13 T4.1 — [BridgeTool(Lifecycle = ...)] must flow onto the entry so
        // the dispatcher and dirty guard see the declared policy.
        [Test]
        public static void Scan_LifecycleAttribute_FlowsToEntry()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("test_lifecycle_settle_tool", out var settleEntry));
            Assert.AreEqual(LifecyclePolicy.EditorSettle, settleEntry.Lifecycle);

            Assert.IsTrue(BridgeToolRegistry.TryGet("test_lifecycle_restart_tool", out var restartEntry));
            Assert.AreEqual(LifecyclePolicy.RestartThenSettle, restartEntry.Lifecycle);
        }

        [Test]
        public static void Scan_LifecycleDefault_IsNoneWhenOmitted()
        {
            // Tools that don't declare a lifecycle must default to None (read-
            // only safe default), not be inferred from IsMutating.
            Assert.IsTrue(BridgeToolRegistry.TryGet("test_mutating_tool", out var mutatingEntry));
            Assert.AreEqual(LifecyclePolicy.None, mutatingEntry.Lifecycle,
                "Omitted lifecycle defaults to None even on mutating tools — policy is explicit.");
        }
    }
}
