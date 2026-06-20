using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M14 — Pure auth-decision tests. BridgeHttpServer.CheckAuth delegates the
    // verdict to BridgeAuthCheck.IsAuthorized so this logic is covered without
    // the live-HTTP harness.
    public class BridgeAuthCheckTests
    {
        private const string PolicyNone = BridgeAuthPolicy.None;
        private const string PolicyRequired = BridgeAuthPolicy.Required;

        [Test]
        public void NonePolicy_AllowsAnyRequest()
        {
            var token = BridgeAuthToken.Generate();
            // With authMode "none" every combination must pass: present token,
            // missing header, mismatched token — enforcement is off.
            Assert.IsTrue(BridgeAuthCheck.IsAuthorized(PolicyNone, null, token));
            Assert.IsTrue(BridgeAuthCheck.IsAuthorized(PolicyNone, "", token));
            Assert.IsTrue(BridgeAuthCheck.IsAuthorized(PolicyNone, "Bearer wrong", token));
            // Even a null expected token is fine under "none".
            Assert.IsTrue(BridgeAuthCheck.IsAuthorized(PolicyNone, null, null));
        }

        [Test]
        public void RequiredPolicy_AllowsMatchingBearer()
        {
            var token = BridgeAuthToken.Generate();
            Assert.IsTrue(
                BridgeAuthCheck.IsAuthorized(PolicyRequired, $"Bearer {token}", token));
        }

        [Test]
        public void RequiredPolicy_RejectsMissingHeader()
        {
            var token = BridgeAuthToken.Generate();
            Assert.IsFalse(BridgeAuthCheck.IsAuthorized(PolicyRequired, null, token));
            Assert.IsFalse(BridgeAuthCheck.IsAuthorized(PolicyRequired, "", token));
        }

        [Test]
        public void RequiredPolicy_RejectsWrongToken()
        {
            var expected = BridgeAuthToken.Generate();
            var other = BridgeAuthToken.Generate();
            Assert.IsFalse(
                BridgeAuthCheck.IsAuthorized(PolicyRequired, $"Bearer {other}", expected));
        }

        [Test]
        public void RequiredPolicy_RejectsNonBearerScheme()
        {
            var token = BridgeAuthToken.Generate();
            Assert.IsFalse(
                BridgeAuthCheck.IsAuthorized(PolicyRequired, $"Basic {token}", token));
        }

        [Test]
        public void RequiredPolicy_RejectsWhenExpectedTokenMissing()
        {
            // No token minted yet (bridge hasn't acquired a lock) — fail closed.
            var token = BridgeAuthToken.Generate();
            Assert.IsFalse(
                BridgeAuthCheck.IsAuthorized(PolicyRequired, $"Bearer {token}", null));
            Assert.IsFalse(
                BridgeAuthCheck.IsAuthorized(PolicyRequired, $"Bearer {token}", ""));
        }

        [Test]
        public void UnknownPolicy_FailsClosed()
        {
            // A corrupt settings file must not silently disable auth. Only
            // "none" is an explicit opt-out.
            var token = BridgeAuthToken.Generate();
            Assert.IsFalse(
                BridgeAuthCheck.IsAuthorized("yes", $"Bearer {token}", token));
            Assert.IsFalse(
                BridgeAuthCheck.IsAuthorized(null, $"Bearer {token}", token));
        }
    }
}
