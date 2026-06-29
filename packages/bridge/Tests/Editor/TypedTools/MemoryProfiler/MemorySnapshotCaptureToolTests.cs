// M20 Plan 7 / T20.7.3 — Memory Profiler snapshot capture EditMode tests.
//
// Gated by UNITY_OPEN_MCP_EXT_MEMORYPROFILER via the owning test asmdef's
// defineConstraints, so the suite only compiles + runs when the
// com.unity.memoryprofiler package is present — matching the compile-gate on
// the tool code under test. When the package is absent neither the tool nor
// this suite compile.
//
// Coverage shape:
//   - registry discovery (the single sense-prefixed tool id) + group membership
//   - the gate/lifecycle contract that makes this a SAFE read-only sense tool:
//       Gate = Off, ReadOnlyHint = true, IsMutating = false,
//       Lifecycle = EditorSettle, Group = "memoryprofiler"
//
// What is deliberately NOT covered here: a real `capture` dispatch. The
// capture is callback-based (MemoryProfilerApi.TryCapture blocks on a reset
// event while pumping editor updates until the snapshot file is written) and
// produces a real .snap file that can be hundreds of MB. Triggering it in an
// EditMode test would be slow, large, and environment-dependent — it stays a
// manual checklist item. The registry/gate/lifecycle surface below is the
// contract that matters for the auto-activation + capability surface; it pins
// that the tool registers as the read-only, gate-free, EditorSettle sense tool
// the docs advertise.
//
// (Do not confuse this with the unity_senses_profiler_memory Senses tool,
// which is a different, lighter profiler-memory read tested in
// Integration/ProfilerToolTests.cs.)
#if UNITY_OPEN_MCP_EXT_MEMORYPROFILER
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests.Extensions.MemoryProfiler
{
    public class MemorySnapshotCaptureToolTests
    {
        // The single catalog tool id this pack must register. Sense-prefixed
        // (unity_senses_*) because it pairs with the profiler senses family
        // rather than the typed-editor authoring surface.
        private const string ToolId = "unity_senses_memory_snapshot_capture";

        [Test]
        public void Registry_CaptureToolDiscovered()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains(ToolId),
                $"Expected memory profiler tool '{ToolId}' to be discovered by " +
                "BridgeToolRegistry when com.unity.memoryprofiler is installed.");
        }

        [Test]
        public void Registry_CaptureToolBelongsToMemoryProfilerGroup()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(ToolId, out var entry),
                $"Tool '{ToolId}' not registered.");
            Assert.AreEqual("memoryprofiler", entry.Group,
                $"Tool '{ToolId}' should belong to the 'memoryprofiler' group.");
        }

        [Test]
        public void Registry_CaptureIsReadOnlyAndGateOff()
        {
            // The capture produces a .snap file but is read-only re: game /
            // project state — Gate = Off, ReadOnlyHint = true, IsMutating =
            // false. This is the contract that lets it auto-activate safely
            // alongside the other senses tools.
            Assert.IsTrue(BridgeToolRegistry.TryGet(ToolId, out var entry));
            Assert.IsFalse(entry.IsMutating,
                "memory_snapshot_capture is read-only re: game/project state.");
            Assert.IsTrue(entry.ReadOnlyHint,
                "memory_snapshot_capture advertises ReadOnlyHint = true.");
            Assert.AreEqual(GateMode.Off, entry.Gate,
                "memory_snapshot_capture is gate-free (it captures, not mutates).");
        }

        [Test]
        public void Registry_CaptureUsesEditorSettleLifecycle()
        {
            // Capture can take seconds; the dispatcher waits for the editor to
            // settle so the snapshot reflects a stable state.
            Assert.IsTrue(BridgeToolRegistry.TryGet(ToolId, out var entry));
            Assert.AreEqual(LifecyclePolicy.EditorSettle, entry.Lifecycle,
                "memory_snapshot_capture uses EditorSettle (capture can take seconds).");
        }
    }
}
#endif
