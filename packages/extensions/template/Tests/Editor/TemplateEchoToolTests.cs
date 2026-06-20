using NUnit.Framework;
using UnityOpenMcpBridge;
using UnityOpenMcpExtensions.Template;

namespace UnityOpenMcpExtensions.Template.Tests
{
    public class TemplateEchoToolTests
    {
        [Test]
        public void Registry_DiscoveredTemplateEcho()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_open_mcp_template_echo"),
                "Template echo tool should be discovered by BridgeToolRegistry " +
                "(proves [BridgeToolType] assembly scan picks up extension packs).");
        }

        [Test]
        public void Registry_TemplateEchoIsReadOnly()
        {
            Assert.IsTrue(
                BridgeToolRegistry.TryGet("unity_open_mcp_template_echo", out var entry));
            Assert.IsFalse(entry.IsMutating);
            Assert.IsTrue(entry.ReadOnlyHint);
        }

        [Test]
        public void Registry_DiscoveredTemplateTouchAsMutating()
        {
            Assert.IsTrue(
                BridgeToolRegistry.TryGet("unity_open_mcp_template_touch", out var entry));
            Assert.IsTrue(entry.IsMutating);
            Assert.IsFalse(entry.ReadOnlyHint);
        }

        [Test]
        public void Dispatch_Echo_ReturnsOkEnvelope()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_template_echo", "{\"message\":\"hi\"}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result?.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"echo\":\"hi\"", result.Output);
        }

        [Test]
        public void Dispatch_Touch_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_template_touch", "{}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("paths_hint_required", result.ErrorCode);
        }

        [Test]
        public void Dispatch_Touch_WithScope_ReturnsOk()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_template_touch",
                "{\"paths_hint\":[\"Assets/Foo.unity\"]}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result?.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("Assets/Foo.unity", result.Output);
        }
    }
}
