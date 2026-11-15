using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine.TestTools;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.ProfilerExt;

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
            // With no Profiler data captured this is a profiler_empty refusal;
            // with data it is a hierarchy payload. It never throws.
            var result = BridgeToolRegistry.TryDispatch("unity_senses_profiler_capture", "{}");

            Assert.IsNotNull(result, "Dispatch should return a result");
            if (result.Success)
                Assert.IsTrue(
                    result.Output.Contains("\"children\"") || result.Output.Contains("\"items\""),
                    "Output should be a hierarchy payload: " + result.Output);
            else
            {
                Assert.AreNotEqual("execution_error", result.ErrorCode, result.ErrorMessage);
                Assert.IsNotNull(result.RefusalBody, "a refusal keeps the tool's own error body");
            }
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
            // answer with the tool's own result (a refusal, warming_up, or status:ok).
            // A1 — a cold capture (profiler just enabled, no frame yet) returns
            // warming_up instead of blocking the editor loop, and leaves the
            // profiler enabled so the agent's retry succeeds.
            var result = BridgeToolRegistry.TryDispatch(
                "unity_senses_profiler_capture_frame",
                "{\"frame_count\":1,\"max_depth\":3,\"max_items\":16}");

            Assert.IsNotNull(result, "Dispatch should return a result");
            if (result.Success)
            {
                var output = result.Output;
                Assert.IsTrue(
                    output.Contains("\"status\":\"ok\"") || output.Contains("\"status\":\"warming_up\""),
                    "profiler_capture_frame output should be a warming_up or status:ok envelope: " + output);
            }
            else
            {
                Assert.AreNotEqual("execution_error", result.ErrorCode, result.ErrorMessage);
                Assert.IsNotNull(result.RefusalBody, "a refusal keeps the tool's own error body");
            }
            // A warming_up answer leaves a warm-up pending; do not leak it.
            Tool_ProfilerCaptureFrame.RestoreWarmUp();
        }

        // -----------------------------------------------------------------
        // Cold capture in Edit Mode: the warm-up must make frames arrive (the
        // Editor target), the retry must capture one and put back exactly the
        // settings the warm-up changed. Before, the warm-up enabled recording
        // only; in Edit Mode with the default Play Mode target no frame ever
        // arrived and every later call answered profiler_empty.
        // -----------------------------------------------------------------

        private const string CaptureFrameArgs = "{\"frame_count\":1,\"max_depth\":2,\"max_items\":8}";

        [UnityTest]
        public IEnumerator ProfilerCaptureFrame_ColdCapture_WarmsUpThenCapturesAndRestores()
        {
            return ColdCaptureRoundTrip(driverEnabledBefore: false);
        }

        [UnityTest]
        public IEnumerator ProfilerCaptureFrame_RecordingWithPlayModeTarget_WarmsUpInsteadOfEmpty()
        {
            return ColdCaptureRoundTrip(driverEnabledBefore: true);
        }

        private static IEnumerator ColdCaptureRoundTrip(bool driverEnabledBefore)
        {
            if (EditorApplication.isPlaying)
                Assert.Ignore("Edit Mode only.");
            bool wasEnabled = ProfilerDriver.enabled;
            bool wasProfileEditor = ProfilerDriver.profileEditor;
            Tool_ProfilerCaptureFrame.RestoreWarmUp();
            ProfilerDriver.enabled = driverEnabledBefore;
            ProfilerDriver.profileEditor = false;
            ProfilerDriver.ClearAllFrames();
            try
            {
                var first = BridgeToolRegistry.TryDispatch("unity_senses_profiler_capture_frame", CaptureFrameArgs);
                Assert.IsTrue(first.Success, first.ErrorMessage ?? first.Output);
                StringAssert.Contains("\"status\":\"warming_up\"", first.Output);
                StringAssert.Contains("\"profilerWasEnabled\":" + (driverEnabledBefore ? "true" : "false"), first.Output);
                Assert.IsTrue(ProfilerDriver.enabled, "the warm-up starts recording");
                Assert.IsTrue(ProfilerDriver.profileEditor, "Edit Mode frames need the Editor target");

                for (int i = 0; i < 300 && ProfilerDriver.lastFrameIndex < 0; i++)
                    yield return null;
                Assert.GreaterOrEqual(ProfilerDriver.lastFrameIndex, 0, "the warm-up records Edit Mode frames");

                var second = BridgeToolRegistry.TryDispatch("unity_senses_profiler_capture_frame", CaptureFrameArgs);
                Assert.IsTrue(second.Success, second.ErrorMessage ?? second.Output);
                StringAssert.Contains("\"status\":\"ok\"", second.Output);
                StringAssert.Contains("\"frames\":[{", second.Output);
                StringAssert.Contains("\"profilerWasEnabled\":" + (driverEnabledBefore ? "true" : "false"), second.Output);
                Assert.AreEqual(driverEnabledBefore, ProfilerDriver.enabled, "recording state restored");
                Assert.IsFalse(ProfilerDriver.profileEditor, "Play Mode target restored");
                Assert.IsFalse(Tool_ProfilerCaptureFrame.TryGetWarmUp(out _), "no warm-up left pending");
            }
            finally
            {
                Tool_ProfilerCaptureFrame.RestoreWarmUp();
                ProfilerDriver.profileEditor = wasProfileEditor;
                ProfilerDriver.enabled = wasEnabled;
            }
        }

        [Test]
        public static void ProfilerCaptureFrame_WarmUpWithoutFrames_GivesUpAfterTimeoutAndRestores()
        {
            bool wasEnabled = ProfilerDriver.enabled;
            bool wasProfileEditor = ProfilerDriver.profileEditor;
            Tool_ProfilerCaptureFrame.RestoreWarmUp();
            ProfilerDriver.enabled = false;
            ProfilerDriver.profileEditor = false;
            ProfilerDriver.ClearAllFrames();
            try
            {
                var first = BridgeToolRegistry.TryDispatch("unity_senses_profiler_capture_frame", CaptureFrameArgs);
                StringAssert.Contains("\"status\":\"warming_up\"", first.Output);
                Assert.IsTrue(Tool_ProfilerCaptureFrame.TryGetWarmUp(out var state));

                // Same editor frame, so still no frame recorded: while the
                // warm-up is young the answer stays warming_up ...
                var again = BridgeToolRegistry.TryDispatch("unity_senses_profiler_capture_frame", CaptureFrameArgs);
                StringAssert.Contains("\"status\":\"warming_up\"", again.Output);

                // ... and once it is older than the timeout the tool gives up.
                Tool_ProfilerCaptureFrame.SetWarmUpForTests(state.DriverWasEnabled, state.ProfileEditorWas,
                    System.DateTime.UtcNow - Tool_ProfilerCaptureFrame.WarmUpTimeout - System.TimeSpan.FromSeconds(1));
                var late = BridgeToolRegistry.TryDispatch("unity_senses_profiler_capture_frame", CaptureFrameArgs);
                Assert.IsFalse(late.Success);
                Assert.AreEqual("profiler_empty", late.ErrorCode);
                Assert.IsFalse(ProfilerDriver.enabled);
                Assert.IsFalse(ProfilerDriver.profileEditor);
                Assert.IsFalse(Tool_ProfilerCaptureFrame.TryGetWarmUp(out _));
            }
            finally
            {
                Tool_ProfilerCaptureFrame.RestoreWarmUp();
                ProfilerDriver.profileEditor = wasProfileEditor;
                ProfilerDriver.enabled = wasEnabled;
            }
        }
    }
}
