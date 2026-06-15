using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    public class ProfilerToolTests
    {
        [Test]
        public static void ProfilerCaptureTool_RegisteredInRegistry()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_agent_profiler_capture"),
                "unity_agent_profiler_capture should be discovered by the registry");
        }

        [Test]
        public static void ProfilerCaptureTool_IsNonMutating()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_agent_profiler_capture", out var entry));
            Assert.IsFalse(entry.IsMutating,
                "unity_agent_profiler_capture should be non-mutating (read-only)");
        }

        [Test]
        public static void ProfilerCaptureTool_GateIsOff()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_agent_profiler_capture", out var entry));
            Assert.AreEqual(GateMode.Off, entry.Gate,
                "unity_agent_profiler_capture should have gate off (non-mutating)");
        }

        [Test]
        public static void ProfilerCaptureTool_HasReadOnlyHint()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_agent_profiler_capture", out var entry));
            Assert.IsTrue(entry.ReadOnlyHint,
                "unity_agent_profiler_capture should have ReadOnlyHint = true");
        }

        [Test]
        public static void ProfilerMemoryTool_RegisteredAndNonMutating()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_agent_profiler_memory"));
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_agent_profiler_memory", out var entry));
            Assert.IsFalse(entry.IsMutating);
            Assert.AreEqual(GateMode.Off, entry.Gate);
            Assert.IsTrue(entry.ReadOnlyHint);
        }

        [Test]
        public static void ProfilerRenderingTool_RegisteredAndNonMutating()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_agent_profiler_rendering"));
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_agent_profiler_rendering", out var entry));
            Assert.IsFalse(entry.IsMutating);
            Assert.AreEqual(GateMode.Off, entry.Gate);
            Assert.IsTrue(entry.ReadOnlyHint);
        }

        [Test]
        public static void ProfilerMemory_DispatchReturnsJson()
        {
            var result = BridgeToolRegistry.TryDispatch("unity_agent_profiler_memory", "{}");

            Assert.IsNotNull(result, "Dispatch should return a result");
            Assert.IsTrue(result.Success, "Dispatch should succeed");
            Assert.IsNotNull(result.Output, "Output should not be null");
            StringAssert.Contains("\"allocatedBytes\"", result.Output,
                "Output should contain allocatedBytes");
        }

        [Test]
        public static void ProfilerRendering_DispatchReturnsJson()
        {
            var result = BridgeToolRegistry.TryDispatch("unity_agent_profiler_rendering", "{}");

            Assert.IsNotNull(result, "Dispatch should return a result");
            Assert.IsTrue(result.Success, "Dispatch should succeed");
            Assert.IsNotNull(result.Output, "Output should not be null");
            StringAssert.Contains("\"renderPipeline\"", result.Output,
                "Output should contain renderPipeline");
            StringAssert.Contains("\"system\"", result.Output,
                "Output should contain a system object");
        }

        [Test]
        public static void ProfilerCapture_DispatchReturnsErrorJsonWhenEmpty()
        {
            // With no Profiler data captured this returns a profiler_empty error,
            // but the dispatch itself must succeed and return JSON.
            var result = BridgeToolRegistry.TryDispatch("unity_agent_profiler_capture", "{}");

            Assert.IsNotNull(result, "Dispatch should return a result");
            Assert.IsTrue(result.Success, "Dispatch should succeed (error is in-payload)");
            Assert.IsNotNull(result.Output, "Output should not be null");
            // Either a profiler_empty error (no data) or a children array (data present).
            Assert.IsTrue(
                result.Output.Contains("\"error\"") || result.Output.Contains("\"children\"") || result.Output.Contains("\"items\""),
                "Output should be either an error payload or a hierarchy payload: " + result.Output);
        }
    }
}
