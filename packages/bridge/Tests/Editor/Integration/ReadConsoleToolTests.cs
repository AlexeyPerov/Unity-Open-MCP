using NUnit.Framework;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.Console;

namespace UnityOpenMcpBridge.Tests
{
    public class ReadConsoleToolTests
    {
        [Test]
        public static void ReadConsoleTool_RegisteredInRegistry()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_agent_read_console"),
                "unity_agent_read_console should be discovered by the registry");
        }

        [Test]
        public static void ReadConsoleTool_IsNonMutating()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_agent_read_console", out var entry));
            Assert.IsFalse(entry.IsMutating,
                "unity_agent_read_console should be non-mutating (read-only)");
        }

        [Test]
        public static void ReadConsoleTool_GateIsOff()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_agent_read_console", out var entry));
            Assert.AreEqual(GateMode.Off, entry.Gate,
                "unity_agent_read_console should have gate off (non-mutating)");
        }

        [Test]
        public static void ReadConsoleTool_HasReadOnlyHint()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_agent_read_console", out var entry));
            Assert.IsTrue(entry.ReadOnlyHint,
                "unity_agent_read_console should have ReadOnlyHint = true");
        }

        [Test]
        public static void LogEntriesReader_IsAvailable()
        {
            Assert.IsTrue(LogEntriesReader.IsAvailable,
                "UnityEditor.LogEntries internal API should be available in Editor");
        }

        [Test]
        public static void ReadConsoleTool_DispatchReturnsJson()
        {
            var result = BridgeToolRegistry.TryDispatch("unity_agent_read_console",
                "{\"type\":\"all\",\"max_entries\":10}");

            Assert.IsNotNull(result, "Dispatch should return a result");
            Assert.IsTrue(result.Success, "Dispatch should succeed");
            Assert.IsNotNull(result.Output, "Output should not be null");
            StringAssert.Contains("\"entries\"", result.Output,
                "Output should contain an entries array");
            StringAssert.Contains("\"counts\"", result.Output,
                "Output should contain a counts object");
        }
    }
}
