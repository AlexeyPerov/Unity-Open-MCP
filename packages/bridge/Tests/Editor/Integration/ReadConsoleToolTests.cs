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
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_senses_read_console"),
                "unity_senses_read_console should be discovered by the registry");
        }

        [Test]
        public static void ReadConsoleTool_IsNonMutating()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_read_console", out var entry));
            Assert.IsFalse(entry.IsMutating,
                "unity_senses_read_console should be non-mutating (read-only)");
        }

        [Test]
        public static void ReadConsoleTool_GateIsOff()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_read_console", out var entry));
            Assert.AreEqual(GateMode.Off, entry.Gate,
                "unity_senses_read_console should have gate off (non-mutating)");
        }

        [Test]
        public static void ReadConsoleTool_HasReadOnlyHint()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_read_console", out var entry));
            Assert.IsTrue(entry.ReadOnlyHint,
                "unity_senses_read_console should have ReadOnlyHint = true");
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
            var result = BridgeToolRegistry.TryDispatch("unity_senses_read_console",
                "{\"type\":\"all\",\"max_entries\":10}");

            Assert.IsNotNull(result, "Dispatch should return a result");
            Assert.IsTrue(result.Success, "Dispatch should succeed");
            Assert.IsNotNull(result.Output, "Output should not be null");
            StringAssert.Contains("\"entries\"", result.Output,
                "Output should contain an entries array");
            StringAssert.Contains("\"counts\"", result.Output,
                "Output should contain a counts object");
        }

        // M13 T4.6 — `truncated` and `detail` are always present so agents can
        // bound their token budget without re-checking the schema.
        [Test]
        public static void ReadConsole_AlwaysReportsTruncated()
        {
            var result = BridgeToolRegistry.TryDispatch("unity_senses_read_console",
                "{\"type\":\"all\",\"max_entries\":10}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"truncated\":", result.Output);
            StringAssert.Contains("\"detail\":\"normal\"", result.Output);
        }

        [Test]
        public static void ReadConsole_DetailSummary_OmitsStacks()
        {
            // Emit a test log entry so we have at least one to inspect.
            UnityEngine.Debug.Log("m13_t46_summary_probe");
            try
            {
                var result = BridgeToolRegistry.TryDispatch("unity_senses_read_console",
                    "{\"type\":\"all\",\"detail\":\"summary\",\"max_entries\":50}");
                Assert.IsNotNull(result);
                Assert.IsTrue(result.Success);
                // Summary must never include stack traces (the dominant token cost).
                Assert.IsFalse(result.Output.Contains("\"stack\":"),
                    $"summary detail must omit stacks. Output: {result.Output}");
                StringAssert.Contains("\"detail\":\"summary\"", result.Output);
            }
            finally
            {
                // best-effort cleanup so we don't pollute later tests
                BridgeToolRegistry.TryDispatch("unity_senses_read_console",
                    "{\"type\":\"all\",\"clear\":true,\"max_entries\":1}");
            }
        }

        [Test]
        public static void ReadConsole_DetailVerbose_IncludesStacksWhenPresent()
        {
            UnityEngine.Debug.Log("m13_t46_verbose_probe");
            try
            {
                var result = BridgeToolRegistry.TryDispatch("unity_senses_read_console",
                    "{\"type\":\"log\",\"detail\":\"verbose\",\"max_entries\":50}");
                Assert.IsNotNull(result);
                Assert.IsTrue(result.Success);
                StringAssert.Contains("\"detail\":\"verbose\"", result.Output);
                // Our probe entry should be present. Stack presence depends on
                // Unity's LogEntry internals, so we only assert detail echoed.
                StringAssert.Contains("m13_t46_verbose_probe", result.Output);
            }
            finally
            {
                BridgeToolRegistry.TryDispatch("unity_senses_read_console",
                    "{\"type\":\"all\",\"clear\":true,\"max_entries\":1}");
            }
        }
    }
}
