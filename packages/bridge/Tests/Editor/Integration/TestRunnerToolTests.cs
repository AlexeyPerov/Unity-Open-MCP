using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    public class TestRunnerToolTests
    {
        [Test]
        public static void RunTestsTool_RegisteredInRegistry()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_agent_run_tests"),
                "unity_agent_run_tests should be discovered when the test framework is present");
        }

        [Test]
        public static void RunTestsTool_IsNonMutating()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_agent_run_tests", out var entry));
            Assert.IsFalse(entry.IsMutating,
                "unity_agent_run_tests should be non-mutating (read-only)");
        }

        [Test]
        public static void RunTestsTool_GateIsOff()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_agent_run_tests", out var entry));
            Assert.AreEqual(GateMode.Off, entry.Gate,
                "unity_agent_run_tests should have gate off (non-mutating)");
        }
    }
}
