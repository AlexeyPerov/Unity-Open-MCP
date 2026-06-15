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
