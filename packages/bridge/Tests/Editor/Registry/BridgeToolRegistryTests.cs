using System;
using System.Net;
using System.Net.Http;
using System.Text;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    public class BridgeToolRegistryTests
    {
        static readonly string BaseUrl = $"http://127.0.0.1:19120";
        static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        [Test]
        public static void Scan_DiscoveredEditorStatus()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_open_mcp_editor_status"),
                "unity_open_mcp_editor_status should be registered");
        }

        [Test]
        public static void Scan_EditorStatusIsReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_open_mcp_editor_status", out var entry));
            Assert.IsFalse(entry.IsMutating);
            Assert.IsTrue(entry.ReadOnlyHint);
        }

        [Test]
        public static void Scan_EditorStatusHasNoParameters()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_open_mcp_editor_status", out var entry));
            Assert.AreEqual(0, entry.Parameters.Length);
        }

        [Test]
        public static void TryDispatch_UnknownTool_ReturnsNull()
        {
            var result = BridgeToolRegistry.TryDispatch("unity_open_mcp_nonexistent_tool", "{}");
            Assert.IsNull(result);
        }
    }

    public class BridgeResourceRegistryTests
    {
        [Test]
        public static void All_ReturnsList()
        {
            var all = BridgeResourceRegistry.All();
            Assert.IsNotNull(all);
        }

        [Test]
        public static void FindByRoute_UnknownRoute_ReturnsNull()
        {
            var entry = BridgeResourceRegistry.FindByRoute("unity-open-mcp://nonexistent");
            Assert.IsNull(entry);
        }
    }
}
