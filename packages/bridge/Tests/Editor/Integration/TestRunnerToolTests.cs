using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    public class TestRunnerToolTests
    {
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator DeferredStartRunsOnceOnEditorUpdate()
        {
            int calls = 0;
            UnityOpenMcpBridge.TestRunner.Tool_TestRunner.ScheduleOnUpdate(() => calls++);
            Assert.AreEqual(0, calls, "Start must return before execution.");
            for (int i = 0; i < 10 && calls == 0; i++) yield return null;
            Assert.AreEqual(1, calls, "Execution must not depend on an inspector repaint.");
            yield return null;
            yield return null;
            Assert.AreEqual(1, calls, "Later updates must not start a second run.");
        }

        [Test]
        public static void RunTestsTool_RegisteredInRegistry()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_senses_run_tests"),
                "unity_senses_run_tests should be discovered when the test framework is present");
        }

        [Test]
        public static void RunTestsTool_IsNonMutating()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_run_tests", out var entry));
            Assert.IsFalse(entry.IsMutating,
                "unity_senses_run_tests should be non-mutating (read-only)");
        }

        [Test]
        public static void RunTestsTool_GateIsOff()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_run_tests", out var entry));
            Assert.AreEqual(GateMode.Off, entry.Gate,
                "unity_senses_run_tests should have gate off (non-mutating)");
        }
    }
}
