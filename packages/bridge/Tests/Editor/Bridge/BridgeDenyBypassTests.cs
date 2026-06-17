using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M14 T5.2 — Bypass resolver tests. The bypass requires BOTH gate=off and
    // confirm_bypass=true; a single flag is not enough.
    public class BridgeDenyBypassTests
    {
        [Test]
        public void IsRequested_RequiresBothFlags()
        {
            Assert.IsFalse(BridgeDenyBypass.IsRequested("off", false));
            Assert.IsFalse(BridgeDenyBypass.IsRequested("enforce", true));
            Assert.IsFalse(BridgeDenyBypass.IsRequested("warn", true));
            Assert.IsTrue(BridgeDenyBypass.IsRequested("off", true));
        }

        [Test]
        public void IsRequested_OnlyOffMode()
        {
            // confirm alone is not enough — gate must be explicitly "off".
            Assert.IsFalse(BridgeDenyBypass.IsRequested("enforce", true));
            Assert.IsFalse(BridgeDenyBypass.IsRequested(null, true));
        }

        [Test]
        public void IsRequestedFromBody_OmittingGate_ReturnsFalse()
        {
            Assert.IsFalse(BridgeDenyBypass.IsRequestedFromBody("{\"confirm_bypass\":true}"));
        }

        [Test]
        public void IsRequestedFromBody_OmittingConfirm_ReturnsFalse()
        {
            Assert.IsFalse(BridgeDenyBypass.IsRequestedFromBody("{\"gate\":\"off\"}"));
        }

        [Test]
        public void IsRequestedFromBody_BothFlags_ReturnsTrue()
        {
            var body = "{\"gate\":\"off\",\"confirm_bypass\":true}";
            Assert.IsTrue(BridgeDenyBypass.IsRequestedFromBody(body));
        }

        [Test]
        public void IsRequestedFromBody_WhitespacedValues_Resolved()
        {
            var body = "{ \"gate\" : \"off\" , \"confirm_bypass\" : true }";
            Assert.IsTrue(BridgeDenyBypass.IsRequestedFromBody(body));
        }

        [Test]
        public void IsRequestedFromBody_NonOffGate_ReturnsFalse()
        {
            // A project default of "off" must NOT grant a bypass — the request
            // has to carry an explicit "off".
            var body = "{\"gate\":\"enforce\",\"confirm_bypass\":true}";
            Assert.IsFalse(BridgeDenyBypass.IsRequestedFromBody(body));
        }

        [Test]
        public void IsRequestedFromBody_EmptyBody_ReturnsFalse()
        {
            Assert.IsFalse(BridgeDenyBypass.IsRequestedFromBody(null));
            Assert.IsFalse(BridgeDenyBypass.IsRequestedFromBody(""));
        }
    }
}
