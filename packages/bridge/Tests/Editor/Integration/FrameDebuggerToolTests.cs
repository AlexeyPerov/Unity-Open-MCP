using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M20 Plan 1 / T20.1.3 — Frame Debugger tool contract tests.
    //
    // The Frame Debugger API is Editor-internal and version-split; the contract
    // we pin here is the registration / read-only / dispatch shape, not the
    // live capture output (which requires a running editor with a rendered
    // frame). The enable/disable/list actions are exercised via the registry
    // dispatch path so the JSON envelope stays stable; live capture runs on CI.
    public class FrameDebuggerToolTests
    {
        [Test]
        public static void FrameDebugger_RegisteredInRegistry()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_senses_frame_debugger"),
                "unity_senses_frame_debugger should be discovered by the registry");
        }

        [Test]
        public static void FrameDebugger_IsReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_frame_debugger", out var entry));
            Assert.IsFalse(entry.IsMutating,
                "frame_debugger should be non-mutating (Editor state change only)");
            Assert.AreEqual(GateMode.Off, entry.Gate,
                "frame_debugger should have gate off (non-mutating)");
            Assert.IsTrue(entry.ReadOnlyHint,
                "frame_debugger should have ReadOnlyHint = true");
        }

        [Test]
        public static void FrameDebugger_GroupIsAgentSenses()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_frame_debugger", out var entry));
            Assert.AreEqual("agent-senses", entry.Group,
                "frame_debugger should map to the agent-senses group");
        }

        [Test]
        public static void FrameDebugger_LifecycleIsNone()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_frame_debugger", out var entry));
            Assert.AreEqual(LifecyclePolicy.None, entry.Lifecycle,
                "frame_debugger should be a direct-response tool (no settle wait)");
        }

        [Test]
        public static void FrameDebugger_RejectsUnknownAction()
        {
            // The tool returns a JSON error string (does not throw), so dispatch
            // reports Success=true with an "error" object in the body.
            var dispatch = BridgeToolRegistry.TryDispatch(
                "unity_senses_frame_debugger",
                "{\"action\":\"bogus\"}");
            Assert.IsNotNull(dispatch, "frame_debugger should dispatch via the registry");
            Assert.IsTrue(dispatch.Success, "frame_debugger should return a JSON result (not throw)");
            Assert.IsNotNull(dispatch.Output, "frame_debugger should return JSON output");
            Assert.IsTrue(dispatch.Output.Contains("\"validation_error\""),
                "frame_debugger with an unknown action should return a validation_error");
        }

        [Test]
        public static void FrameDebugger_DispatchReturnsJsonEnvelope()
        {
            // Dispatch the list action. The Frame Debugger may be unavailable
            // (reflection misses the API on some Unity builds) or have no
            // captured frame — either way the dispatch must succeed and return
            // a JSON envelope (error payload or status:ok). We don't pin the
            // live capture here; CI exercises that path.
            var dispatch = BridgeToolRegistry.TryDispatch(
                "unity_senses_frame_debugger",
                "{\"action\":\"list\",\"max_draw_calls\":8}");
            Assert.IsNotNull(dispatch, "frame_debugger should dispatch via the registry");
            Assert.IsTrue(dispatch.Success, "frame_debugger should return a JSON result (not throw)");
            Assert.IsNotNull(dispatch.Output, "frame_debugger should return JSON output");

            // The envelope must be one of: error (api unavailable / no captured
            // frame) or status:ok (list returned). It must never be empty or
            // a raw thrown exception.
            var output = dispatch.Output;
            Assert.IsTrue(
                output.Contains("\"error\"") || output.Contains("\"status\":\"ok\""),
                "frame_debugger list output should be an error or status:ok envelope: " + output);
        }
    }
}
