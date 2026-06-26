using System;
using NUnit.Framework;
using UnityOpenMcpBridge;
using UnityOpenMcpVerify.Cache;

namespace UnityOpenMcpBridge.Tests
{
    // Round-trip + clamping for the verify cache TTL project setting. The TTL
    // governs how fresh the in-memory verify health snapshot is (the
    // `health/summary` MCP resource and `gate_budget_estimate` "cache" mode).
    // It lives in settings.json AND must be applied to the runtime
    // VerifyCacheService.Ttl on every load/set so the editor and the cache
    // never drift apart.
    public class VerifyCacheTtlSettingsTests
    {
        private int _previousTtl;

        [SetUp]
        public void SetUp()
        {
            _previousTtl = BridgeProjectSettings.VerifyCacheTtlSeconds;
        }

        [TearDown]
        public void TearDown()
        {
            BridgeProjectSettings.SetVerifyCacheTtlSeconds(_previousTtl);
            // Restore the service to a known state so test ordering can't bleed.
            VerifyCacheService.Ttl = VerifyCacheService.DefaultTtl;
            VerifyCacheService.Clear();
        }

        [Test]
        public void Default_Is60Seconds()
        {
            // A fresh data object ships with the documented default.
            Assert.AreEqual(60, new BridgeProjectSettingsData().verifyCacheTtlSeconds);
        }

        [Test]
        public void Set_PersistsAndAppliesToService()
        {
            BridgeProjectSettings.SetVerifyCacheTtlSeconds(120);
            BridgeProjectSettings.Load(); // force re-read from disk
            Assert.AreEqual(120, BridgeProjectSettings.VerifyCacheTtlSeconds);
            Assert.AreEqual(
                TimeSpan.FromSeconds(120),
                VerifyCacheService.Ttl,
                "SetVerifyCacheTtlSeconds must push the value into VerifyCacheService.Ttl");
        }

        [Test]
        public void Load_AppliesPersistedTtlToService()
        {
            // Write 45s directly to the data object, save, then Load() — the
            // service must reflect the on-disk value without an explicit Set.
            BridgeProjectSettings.Data.verifyCacheTtlSeconds = 45;
            BridgeProjectSettings.Save();
            BridgeProjectSettings.Load();
            Assert.AreEqual(45, BridgeProjectSettings.VerifyCacheTtlSeconds);
            Assert.AreEqual(TimeSpan.FromSeconds(45), VerifyCacheService.Ttl);
        }

        [Test]
        public void BelowMinimum_ClampsToDefault()
        {
            // 0 / negative / sub-min values fall back to the default (60), not
            // to the minimum, so a hand-edited bogus value can't shrink the
            // cache to the floor.
            BridgeProjectSettings.SetVerifyCacheTtlSeconds(0);
            Assert.AreEqual(60, BridgeProjectSettings.VerifyCacheTtlSeconds);
            Assert.AreEqual(TimeSpan.FromSeconds(60), VerifyCacheService.Ttl);

            BridgeProjectSettings.SetVerifyCacheTtlSeconds(5);
            Assert.AreEqual(60, BridgeProjectSettings.VerifyCacheTtlSeconds);

            BridgeProjectSettings.SetVerifyCacheTtlSeconds(-10);
            Assert.AreEqual(60, BridgeProjectSettings.VerifyCacheTtlSeconds);
        }

        [Test]
        public void AboveMaximum_ClampsToCeiling()
        {
            BridgeProjectSettings.SetVerifyCacheTtlSeconds(99999);
            Assert.AreEqual(
                BridgeProjectSettings.MaxVerifyCacheTtlSeconds,
                BridgeProjectSettings.VerifyCacheTtlSeconds);
            Assert.AreEqual(
                TimeSpan.FromSeconds(BridgeProjectSettings.MaxVerifyCacheTtlSeconds),
                VerifyCacheService.Ttl);
        }

        [Test]
        public void InRange_PassesThrough()
        {
            BridgeProjectSettings.SetVerifyCacheTtlSeconds(BridgeProjectSettings.MinVerifyCacheTtlSeconds);
            Assert.AreEqual(BridgeProjectSettings.MinVerifyCacheTtlSeconds, BridgeProjectSettings.VerifyCacheTtlSeconds);

            BridgeProjectSettings.SetVerifyCacheTtlSeconds(BridgeProjectSettings.MaxVerifyCacheTtlSeconds);
            Assert.AreEqual(BridgeProjectSettings.MaxVerifyCacheTtlSeconds, BridgeProjectSettings.VerifyCacheTtlSeconds);
        }

        [Test]
        public void GarbledOnDisk_CoercedToDefaultOnLoad()
        {
            // Simulate a hand-edited settings.json with a nonsensical TTL.
            BridgeProjectSettings.Data.verifyCacheTtlSeconds = -1;
            BridgeProjectSettings.Save();
            BridgeProjectSettings.Load();
            Assert.AreEqual(60, BridgeProjectSettings.VerifyCacheTtlSeconds);
            Assert.AreEqual(TimeSpan.FromSeconds(60), VerifyCacheService.Ttl);
        }
    }
}
