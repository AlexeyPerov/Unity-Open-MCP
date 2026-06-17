using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M14 — Auth policy tests (mirrors BridgeGateDefaultPolicyTests shape):
    // project default lives under `authMode` in .unity-open-mcp/settings.json,
    // validated against ValidModes, defaulting to "none".
    public class BridgeAuthPolicyTests
    {
        [SetUp]
        public void SetUp()
        {
            // Clean settings state for each test.
            var data = BridgeProjectSettings.Data;
            data.authMode = BridgeAuthPolicy.None;
            BridgeProjectSettings.Save();
        }

        [TearDown]
        public void TearDown()
        {
            // Never leave "required" set — other test classes share the file.
            var data = BridgeProjectSettings.Data;
            data.authMode = BridgeAuthPolicy.None;
            BridgeProjectSettings.Save();
        }

        [Test]
        public void GetDefault_ReturnsNone_WhenUnset()
        {
            var data = BridgeProjectSettings.Data;
            data.authMode = null;
            BridgeProjectSettings.Save();

            Assert.AreEqual(BridgeAuthPolicy.None, BridgeAuthPolicy.GetDefault());
        }

        [Test]
        public void GetDefault_ReturnsNone_WhenUnknown()
        {
            var data = BridgeProjectSettings.Data;
            data.authMode = "bogus";
            BridgeProjectSettings.Save();

            // Unknown values coerce to the safe default ("none") via Load,
            // and GetDefault re-validates so a corrupt in-memory value can't
            // leak through either.
            Assert.AreEqual(BridgeAuthPolicy.None, BridgeAuthPolicy.GetDefault());
        }

        [Test]
        public void SetDefault_PersistsAndReloads()
        {
            BridgeAuthPolicy.SetDefault(BridgeAuthPolicy.Required);
            Assert.AreEqual(BridgeAuthPolicy.Required, BridgeAuthPolicy.GetDefault());

            // Force a reload from disk to verify persistence.
            BridgeProjectSettings.Load();
            Assert.AreEqual(BridgeAuthPolicy.Required, BridgeAuthPolicy.GetDefault());

            BridgeAuthPolicy.SetDefault(BridgeAuthPolicy.None);
            BridgeProjectSettings.Load();
            Assert.AreEqual(BridgeAuthPolicy.None, BridgeAuthPolicy.GetDefault());
        }

        [Test]
        public void SetDefault_IgnoresInvalidValue()
        {
            BridgeAuthPolicy.SetDefault("nope");
            Assert.AreEqual(BridgeAuthPolicy.None, BridgeAuthPolicy.GetDefault());
        }

        [Test]
        public void IsValid_OnlyAcceptsKnownModes()
        {
            Assert.IsTrue(BridgeAuthPolicy.IsValid("none"));
            Assert.IsTrue(BridgeAuthPolicy.IsValid("required"));
            Assert.IsFalse(BridgeAuthPolicy.IsValid(null));
            Assert.IsFalse(BridgeAuthPolicy.IsValid(""));
            Assert.IsFalse(BridgeAuthPolicy.IsValid("REQUIRED"));
            Assert.IsFalse(BridgeAuthPolicy.IsValid("unknown"));
        }
    }
}
