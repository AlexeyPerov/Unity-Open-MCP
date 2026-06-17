using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    public static class GateDispatchResultTests
    {
        [Test]
        public static void GateDispatchResult_DefaultState()
        {
            var result = new GateDispatchResult();
            Assert.IsNull(result.Mutation);
            Assert.IsFalse(result.GateRan);
            Assert.IsNull(result.CheckpointId);
            Assert.IsNull(result.CategoriesRun);
            Assert.AreEqual(0, result.ValidationDurationMs);
            Assert.IsNull(result.Delta);
            Assert.IsFalse(result.GateFailed);
            Assert.IsNull(result.AgentNextSteps);
        }

        // M13 T4.1/T4.2 — new telemetry fields on the gate result. These carry
        // settle-wait duration and dirty-scene paths into the response envelope.
        [Test]
        public static void GateDispatchResult_LifecycleFields_DefaultZeroAndNull()
        {
            var result = new GateDispatchResult();
            Assert.AreEqual(0, result.SettleMs,
                "SettleMs defaults to 0 (no settle wait performed).");
            Assert.IsNull(result.DirtyScenePaths,
                "DirtyScenePaths defaults to null (guard allowed / not run).");
        }

        [Test]
        public static void GateDispatchResult_LifecycleFields_RoundTrip()
        {
            var result = new GateDispatchResult
            {
                SettleMs = 1234,
                DirtyScenePaths = new[] { "Assets/Scenes/Main.unity" }
            };
            Assert.AreEqual(1234, result.SettleMs);
            Assert.AreEqual(new[] { "Assets/Scenes/Main.unity" }, result.DirtyScenePaths);
        }

        [Test]
        public static void DeltaData_DefaultState()
        {
            var delta = new DeltaData();
            Assert.AreEqual(0, delta.NewErrors);
            Assert.AreEqual(0, delta.NewWarnings);
            Assert.AreEqual(0, delta.ResolvedErrors);
            Assert.AreEqual(0, delta.ResolvedWarnings);
            Assert.IsNull(delta.NewIssueKeys);
            Assert.IsNull(delta.ResolvedIssueKeys);
        }
    }
}
