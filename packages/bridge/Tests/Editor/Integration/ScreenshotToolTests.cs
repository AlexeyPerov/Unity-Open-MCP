using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    public class ScreenshotToolTests
    {
        [Test]
        public static void ScreenshotTool_RegisteredInRegistry()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_senses_screenshot"),
                "unity_senses_screenshot should be discovered by the registry");
        }

        [Test]
        public static void ScreenshotTool_IsNonMutating()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_screenshot", out var entry));
            Assert.IsFalse(entry.IsMutating,
                "unity_senses_screenshot should be non-mutating (read-only)");
        }

        [Test]
        public static void ScreenshotTool_GateIsOff()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_screenshot", out var entry));
            Assert.AreEqual(GateMode.Off, entry.Gate,
                "unity_senses_screenshot should have gate off (non-mutating)");
        }

        [Test]
        public static void ScreenshotTool_HasReadOnlyHint()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_senses_screenshot", out var entry));
            Assert.IsTrue(entry.ReadOnlyHint,
                "unity_senses_screenshot should have ReadOnlyHint = true");
        }
    }
}
