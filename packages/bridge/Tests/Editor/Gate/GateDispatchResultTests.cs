using NUnit.Framework;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.Console;

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

        // M22 T22.1.3 — per-call `logs` field. Default null (not captured / old
        // surface); the dispatcher sets it to the captured delta (possibly empty)
        // before the envelope is built.
        [Test]
        public static void GateDispatchResult_Logs_DefaultNull()
        {
            var result = new GateDispatchResult();
            Assert.IsNull(result.Logs,
                "Logs defaults to null before the dispatcher attaches a capture.");
        }

        [Test]
        public static void GateDispatchResult_Logs_RoundTrip()
        {
            var result = new GateDispatchResult
            {
                Logs = new System.Collections.Generic.List<LogEntryInfo>
                {
                    new LogEntryInfo { Mode = 4, Message = "warn" } // bit 4 = warning
                }
            };
            Assert.IsNotNull(result.Logs);
            Assert.AreEqual(1, result.Logs.Count);
            Assert.AreEqual("warning", LogEntriesReader.Classify(result.Logs[0].Mode));
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
