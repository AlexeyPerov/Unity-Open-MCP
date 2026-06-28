using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    public class ProfilerToolTests
    {
        [Test]
        public static void ProfilerCaptureTool_RegisteredInRegistry()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_senses_profiler_capture"),
                "unity_senses_profiler_capture should be discovered by the registry");
        }

        [Test]
        public static void ProfilerCaptureTool_IsNonMutating()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_profiler_capture", out var entry));
            Assert.IsFalse(entry.IsMutating,
                "unity_senses_profiler_capture should be non-mutating (read-only)");
        }

        [Test]
        public static void ProfilerCaptureTool_GateIsOff()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_profiler_capture", out var entry));
            Assert.AreEqual(GateMode.Off, entry.Gate,
                "unity_senses_profiler_capture should have gate off (non-mutating)");
        }

        [Test]
        public static void ProfilerCaptureTool_HasReadOnlyHint()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_profiler_capture", out var entry));
            Assert.IsTrue(entry.ReadOnlyHint,
                "unity_senses_profiler_capture should have ReadOnlyHint = true");
        }

        [Test]
        public static void ProfilerMemoryTool_RegisteredAndNonMutating()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_senses_profiler_memory"));
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_profiler_memory", out var entry));
            Assert.IsFalse(entry.IsMutating);
            Assert.AreEqual(GateMode.Off, entry.Gate);
            Assert.IsTrue(entry.ReadOnlyHint);
        }

        [Test]
        public static void ProfilerRenderingTool_RegisteredAndNonMutating()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_senses_profiler_rendering"));
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_profiler_rendering", out var entry));
            Assert.IsFalse(entry.IsMutating);
            Assert.AreEqual(GateMode.Off, entry.Gate);
            Assert.IsTrue(entry.ReadOnlyHint);
        }

        [Test]
        public static void ProfilerMemory_DispatchReturnsJson()
        {
            var result = BridgeToolRegistry.TryDispatch("unity_senses_profiler_memory", "{}");

            Assert.IsNotNull(result, "Dispatch should return a result");
            Assert.IsTrue(result.Success, "Dispatch should succeed");
            Assert.IsNotNull(result.Output, "Output should not be null");
            StringAssert.Contains("\"allocatedBytes\"", result.Output,
                "Output should contain allocatedBytes");
        }

        [Test]
        public static void ProfilerRendering_DispatchReturnsJson()
        {
            var result = BridgeToolRegistry.TryDispatch("unity_senses_profiler_rendering", "{}");

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
            var result = BridgeToolRegistry.TryDispatch("unity_senses_profiler_capture", "{}");

            Assert.IsNotNull(result, "Dispatch should return a result");
            Assert.IsTrue(result.Success, "Dispatch should succeed (error is in-payload)");
            Assert.IsNotNull(result.Output, "Output should not be null");
            // Either a profiler_empty error (no data) or a children array (data present).
            Assert.IsTrue(
                result.Output.Contains("\"error\"") || result.Output.Contains("\"children\"") || result.Output.Contains("\"items\""),
                "Output should be either an error payload or a hierarchy payload: " + result.Output);
        }

        // -------------------------------------------------------------------
        // M20 Plan 1 / T20.1.4 — single-frame deep profiler capture
        // -------------------------------------------------------------------

        [Test]
        public static void ProfilerCaptureFrame_RegisteredInRegistry()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_senses_profiler_capture_frame"),
                "unity_senses_profiler_capture_frame should be discovered by the registry");
        }

        [Test]
        public static void ProfilerCaptureFrame_IsReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_profiler_capture_frame", out var entry));
            Assert.IsFalse(entry.IsMutating,
                "profiler_capture_frame should be non-mutating (read-only)");
            Assert.AreEqual(GateMode.Off, entry.Gate,
                "profiler_capture_frame should have gate off (non-mutating)");
            Assert.IsTrue(entry.ReadOnlyHint,
                "profiler_capture_frame should have ReadOnlyHint = true");
        }

        [Test]
        public static void ProfilerCaptureFrame_GroupIsAgentSenses()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_profiler_capture_frame", out var entry));
            Assert.AreEqual("agent-senses", entry.Group,
                "profiler_capture_frame should map to the agent-senses group");
        }

        [Test]
        public static void ProfilerCaptureFrame_LifecycleIsNone()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_profiler_capture_frame", out var entry));
            Assert.AreEqual(LifecyclePolicy.None, entry.Lifecycle,
                "profiler_capture_frame should be a direct-response tool (no settle wait)");
        }

        [Test]
        public static void ProfilerCaptureFrame_DispatchReturnsJsonEnvelope()
        {
            // The tool flips the Profiler on if it is off (recording profilerWasEnabled),
            // captures the latest frame(s), and returns the sample tree. In a headless
            // EditMode run the Profiler may capture nothing; either way the dispatch must
            // succeed and return a JSON envelope (error payload or status:ok).
            var result = BridgeToolRegistry.TryDispatch(
                "unity_senses_profiler_capture_frame",
                "{\"frame_count\":1,\"max_depth\":3,\"max_items\":16}");

            Assert.IsNotNull(result, "Dispatch should return a result");
            Assert.IsTrue(result.Success, "Dispatch should succeed (error is in-payload)");
            Assert.IsNotNull(result.Output, "Output should not be null");

            var output = result.Output;
            Assert.IsTrue(
                output.Contains("\"error\"") || output.Contains("\"status\":\"ok\""),
                "profiler_capture_frame output should be an error or status:ok envelope: " + output);
        }
    }
}
