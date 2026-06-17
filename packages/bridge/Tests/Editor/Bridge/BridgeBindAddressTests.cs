using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M14 T5.4 — Pure bind-address decision tests. BridgeHttpServer.Start
    // delegates the verdict to BridgeBindAddress.Decide so the start-vs-refuse
    // logic is covered without a live HttpListener.
    public class BridgeBindAddressTests
    {
        [Test]
        public void Loopback_AllowedWithAnyAuthMode()
        {
            var d1 = BridgeBindAddress.Decide(BridgeBindAddress.Loopback, BridgeAuthPolicy.None);
            Assert.IsTrue(d1.Allowed);
            Assert.AreEqual(BridgeBindAddress.Loopback, d1.ResolvedAddress);

            var d2 = BridgeBindAddress.Decide(BridgeBindAddress.Loopback, BridgeAuthPolicy.Required);
            Assert.IsTrue(d2.Allowed);
        }

        [Test]
        public void Remote_AllowedOnlyWhenAuthRequired()
        {
            var denied = BridgeBindAddress.Decide(BridgeBindAddress.Remote, BridgeAuthPolicy.None);
            Assert.IsFalse(denied.Allowed);
            Assert.IsNotNull(denied.RefusalReason);
            StringAssert.Contains("required", denied.RefusalReason);

            var allowed = BridgeBindAddress.Decide(BridgeBindAddress.Remote, BridgeAuthPolicy.Required);
            Assert.IsTrue(allowed.Allowed);
            Assert.AreEqual(BridgeBindAddress.Remote, allowed.ResolvedAddress);
        }

        [Test]
        public void Remote_UnknownAuth_FailsClosed()
        {
            // Mirrors the auth policy: unknown authMode must not silently
            // permit remote bind.
            var d = BridgeBindAddress.Decide(BridgeBindAddress.Remote, "bogus");
            Assert.IsFalse(d.Allowed);
        }

        [Test]
        public void UnknownAddress_CoercesToLoopback()
        {
            var d = BridgeBindAddress.Decide("example.com", BridgeAuthPolicy.None);
            Assert.IsTrue(d.Allowed);
            Assert.AreEqual(BridgeBindAddress.Loopback, d.ResolvedAddress);
        }

        [Test]
        public void IsValid_OnlyLoopbackOrRemote()
        {
            Assert.IsTrue(BridgeBindAddress.IsValid("127.0.0.1"));
            Assert.IsTrue(BridgeBindAddress.IsValid("0.0.0.0"));
            Assert.IsFalse(BridgeBindAddress.IsValid(null));
            Assert.IsFalse(BridgeBindAddress.IsValid(""));
            Assert.IsFalse(BridgeBindAddress.IsValid("localhost"));
            Assert.IsFalse(BridgeBindAddress.IsValid("192.168.1.1"));
        }

        [Test]
        public void IsRemote_TrueOnlyForWildcard()
        {
            Assert.IsTrue(BridgeBindAddress.IsRemote("0.0.0.0"));
            Assert.IsFalse(BridgeBindAddress.IsRemote("127.0.0.1"));
        }
    }
}
