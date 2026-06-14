using System;
using NUnit.Framework;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Cache;

namespace UnityOpenMcpBridge.Tests
{
    public class VerifyCacheIntegrationTests
    {
        [SetUp]
        public void SetUp()
        {
            VerifyCacheService.Clear();
            VerifyCacheService.Ttl = VerifyCacheService.DefaultTtl;
        }

        [TearDown]
        public void TearDown()
        {
            VerifyCacheService.Clear();
        }

        [Test]
        public void ValidatePaths_RecordsCache_AsValidateEdit_ByDefault()
        {
            VerifyGateAdapter.ValidatePaths(new[] { "Assets/__nonexistent__.prefab" }, null);

            var snapshot = VerifyCacheService.GetSnapshot();
            Assert.AreEqual(HealthSummaryStatus.Ok, snapshot.Status);
            Assert.AreEqual(VerifyCacheService.SourceValidateEdit, snapshot.Source);
            Assert.IsNotNull(snapshot.Summary);
        }

        [Test]
        public void ValidatePaths_RecordsCache_AsGate_WhenSourceIsGate()
        {
            VerifyGateAdapter.ValidatePaths(
                new[] { "Assets/__nonexistent__.prefab" },
                null,
                VerifyCacheService.SourceGate);

            var snapshot = VerifyCacheService.GetSnapshot();
            Assert.AreEqual(HealthSummaryStatus.Ok, snapshot.Status);
            Assert.AreEqual(VerifyCacheService.SourceGate, snapshot.Source);
        }

        [Test]
        public void ScanPaths_RecordsCache_AsScanPaths()
        {
            VerifyGateAdapter.ScanPaths(new[] { "Assets/__nonexistent__.prefab" }, null);

            var snapshot = VerifyCacheService.GetSnapshot();
            Assert.AreEqual(HealthSummaryStatus.Ok, snapshot.Status);
            Assert.AreEqual(VerifyCacheService.SourceScanPaths, snapshot.Source);
        }

        [Test]
        public void GetSnapshot_ReturnsNoData_BeforeAnyToolCall()
        {
            var snapshot = VerifyCacheService.GetSnapshot();
            Assert.AreEqual(HealthSummaryStatus.NoData, snapshot.Status);
            Assert.IsFalse(VerifyCacheService.HasData);
        }

        [Test]
        public void GetSnapshot_ReturnsNoData_AfterTtlExpires()
        {
            VerifyCacheService.Ttl = TimeSpan.FromMilliseconds(10);
            VerifyGateAdapter.ScanPaths(new[] { "Assets/__nonexistent__.prefab" }, null);

            Assert.IsTrue(VerifyCacheService.HasData);
            System.Threading.Thread.Sleep(30);

            var snapshot = VerifyCacheService.GetSnapshot();
            Assert.AreEqual(HealthSummaryStatus.NoData, snapshot.Status);
            Assert.IsTrue(VerifyCacheService.IsStale());
        }

        [Test]
        public void RepeatedGateCalls_RefreshAsOf()
        {
            VerifyGateAdapter.ValidatePaths(
                new[] { "Assets/__nonexistent__.prefab" },
                null,
                VerifyCacheService.SourceGate);
            var firstAsOf = VerifyCacheService.GetSnapshot().AsOf;

            System.Threading.Thread.Sleep(5);

            VerifyGateAdapter.ValidatePaths(
                new[] { "Assets/__nonexistent__.prefab" },
                null,
                VerifyCacheService.SourceGate);
            var secondAsOf = VerifyCacheService.GetSnapshot().AsOf;

            Assert.AreNotEqual(firstAsOf, secondAsOf);
        }
    }
}
