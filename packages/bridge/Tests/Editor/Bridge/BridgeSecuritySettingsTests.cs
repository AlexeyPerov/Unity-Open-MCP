using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M14 T5.2 / T5.3 / T5.4 / T5.5 — Project-settings round-trip for the new
    // security fields. Verifies the .unity-open-mcp/settings.json layer
    // persists and canonicalizes deny patterns, bind address, and the audit
    // flag, and that invalid inputs are coerced/dropped to safe defaults.
    public class BridgeSecuritySettingsTests
    {
        [SetUp]
        public void SetUp()
        {
            ResetSecuritySettings();
        }

        [TearDown]
        public void TearDown()
        {
            ResetSecuritySettings();
        }

        static void ResetSecuritySettings()
        {
            var data = BridgeProjectSettings.Data;
            data.csharpDenyPatterns = null;
            data.menuDenyPatterns = null;
            data.bindAddress = BridgeBindAddress.Loopback;
            data.auditLogEnabled = false;
            data.authMode = BridgeAuthPolicy.None;
            BridgeProjectSettings.Save();
            // Clear the deny-list cache so the next test re-resolves defaults.
            BridgeDenyList.ResetCacheForTests();
        }

        // --- deny patterns ---

        [Test]
        public void CSharpDenyPatterns_NullByDefault()
        {
            Assert.IsNull(BridgeProjectSettings.CSharpDenyPatterns);
            // null ⇒ evaluator applies built-in defaults.
            Assert.AreEqual(BridgeDenyList.DefaultCSharpDenyPatterns.Length,
                BridgeDenyList.ResolveCSharpPatterns(BridgeProjectSettings.CSharpDenyPatterns).Length);
        }

        [Test]
        public void SetCSharpDenyPatterns_PersistsAndStripsInvalid()
        {
            // Invalid regex + whitespace-only entries are dropped; valid one survives.
            BridgeProjectSettings.SetCSharpDenyPatterns(new[] { @"Foo\.Bar", "(bad", "  " });
            BridgeProjectSettings.Load(); // force re-read from disk
            var stored = BridgeProjectSettings.CSharpDenyPatterns;
            Assert.IsNotNull(stored);
            Assert.AreEqual(1, stored.Length);
            Assert.AreEqual(@"Foo\.Bar", stored[0]);
        }

        [Test]
        public void SetCSharpDenyPatterns_EmptyOrNull_FallsBackToDefaults()
        {
            // JsonUtility serializes null as [], so null and empty are
            // indistinguishable after a round-trip — both resolve to defaults.
            BridgeProjectSettings.SetCSharpDenyPatterns(new string[0]);
            BridgeProjectSettings.Load();
            var stored = BridgeProjectSettings.CSharpDenyPatterns;
            // After load the defaults apply via Resolve* regardless of null/empty.
            Assert.AreEqual(
                BridgeDenyList.DefaultCSharpDenyPatterns.Length,
                BridgeDenyList.ResolveCSharpPatterns(stored).Length);
        }

        [Test]
        public void SetCSharpDenyPatterns_CustomOverridesDefaults()
        {
            BridgeProjectSettings.SetCSharpDenyPatterns(new[] { "^MyPattern$" });
            BridgeProjectSettings.Load();
            var stored = BridgeProjectSettings.CSharpDenyPatterns;
            // A default-blocked snippet is NOT blocked under the custom list.
            var r = BridgeDenyList.EvaluateCSharp("EditorApplication.Exit(0);", stored, false);
            Assert.IsTrue(r.Allowed);
            // The custom pattern does fire.
            var r2 = BridgeDenyList.EvaluateCSharp("MyPattern", stored, false);
            Assert.IsFalse(r2.Allowed);
        }

        [Test]
        public void SetMenuDenyPatterns_RoundTrips()
        {
            BridgeProjectSettings.SetMenuDenyPatterns(new[] { "^Custom/Menu$" });
            BridgeProjectSettings.Load();
            Assert.AreEqual(new[] { "^Custom/Menu$" }, BridgeProjectSettings.MenuDenyPatterns);
        }

        // --- bind address ---

        [Test]
        public void BindAddress_LoopbackByDefault()
        {
            Assert.AreEqual(BridgeBindAddress.Loopback, BridgeProjectSettings.BindAddress);
        }

        [Test]
        public void SetBindAddress_Remote_Persists()
        {
            BridgeProjectSettings.SetBindAddress(BridgeBindAddress.Remote);
            BridgeProjectSettings.Load();
            Assert.AreEqual(BridgeBindAddress.Remote, BridgeProjectSettings.BindAddress);
        }

        [Test]
        public void SetBindAddress_Invalid_Ignored()
        {
            var before = BridgeProjectSettings.BindAddress;
            BridgeProjectSettings.SetBindAddress("example.com");
            Assert.AreEqual(before, BridgeProjectSettings.BindAddress);
        }

        [Test]
        public void BindAddress_InvalidOnDisk_CoercesToLoopback()
        {
            var data = BridgeProjectSettings.Data;
            data.bindAddress = "10.0.0.1";
            BridgeProjectSettings.Save();
            // After Load, the invalid value is coerced to loopback.
            Assert.AreEqual(BridgeBindAddress.Loopback, BridgeProjectSettings.BindAddress);
        }

        // --- audit flag ---

        [Test]
        public void AuditLogEnabled_FalseByDefault()
        {
            Assert.IsFalse(BridgeProjectSettings.AuditLogEnabled);
        }

        [Test]
        public void SetAuditLogEnabled_Persists()
        {
            BridgeProjectSettings.SetAuditLogEnabled(true);
            BridgeProjectSettings.Load();
            Assert.IsTrue(BridgeProjectSettings.AuditLogEnabled);
        }
    }
}
