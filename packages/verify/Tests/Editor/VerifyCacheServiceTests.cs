using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityOpenMcpVerify.Batch;
using UnityOpenMcpVerify.Cache;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class VerifyCacheServiceTests
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
            VerifyCacheService.Ttl = VerifyCacheService.DefaultTtl;
        }

        [Test]
        public void GetSnapshot_ReturnsNoData_WhenNeverWritten()
        {
            var snapshot = VerifyCacheService.GetSnapshot();

            Assert.AreEqual(HealthSummaryStatus.NoData, snapshot.Status);
            Assert.IsNull(snapshot.AsOf);
            Assert.IsNull(snapshot.Summary);
            Assert.IsNull(snapshot.Source);
        }

        [Test]
        public void Record_PopulatesSnapshot_AndGetSnapshotReturnsOk()
        {
            var result = MakeResult(
                new VerifyIssue("missing_references", VerifySeverity.Error, "Assets/A.prefab", "MISSING_SCRIPT", "x"),
                new VerifyIssue("scene_prefab_health", VerifySeverity.Warning, "Assets/B.unity", "DEEP_NEST", "y"));

            VerifyCacheService.Record(result, VerifyCacheService.SourceGate);

            var snapshot = VerifyCacheService.GetSnapshot();

            Assert.AreEqual(HealthSummaryStatus.Ok, snapshot.Status);
            Assert.IsNotNull(snapshot.AsOf);
            Assert.IsNotNull(snapshot.Summary);
            Assert.AreEqual(1, snapshot.Summary.error);
            Assert.AreEqual(1, snapshot.Summary.warn);
            Assert.AreEqual(VerifyCacheService.SourceGate, snapshot.Source);
            Assert.IsTrue(VerifyCacheService.HasData);
        }

        [Test]
        public void Record_DefaultsSource_WhenSourceIsNullOrEmpty()
        {
            var result = MakeResult();

            VerifyCacheService.Record(result, null);

            Assert.AreEqual(VerifyCacheService.SourceValidateEdit, VerifyCacheService.GetSnapshot().Source);
        }

        [Test]
        public void Record_OverwritesPreviousSnapshot()
        {
            VerifyCacheService.Record(MakeResult(1, 0), VerifyCacheService.SourceValidateEdit);
            VerifyCacheService.Record(MakeResult(2, 3), VerifyCacheService.SourceScanAll);

            var snapshot = VerifyCacheService.GetSnapshot();
            Assert.AreEqual(2, snapshot.Summary.error);
            Assert.AreEqual(3, snapshot.Summary.warn);
            Assert.AreEqual(VerifyCacheService.SourceScanAll, snapshot.Source);
        }

        [Test]
        public void GetSnapshot_ReturnsNoData_AfterTtlExpires()
        {
            VerifyCacheService.Ttl = TimeSpan.FromMilliseconds(10);
            VerifyCacheService.Record(MakeResult(), VerifyCacheService.SourceGate);

            Assert.AreEqual(HealthSummaryStatus.Ok, VerifyCacheService.GetSnapshot().Status);
            Assert.IsTrue(VerifyCacheService.HasData);
            Assert.IsFalse(VerifyCacheService.IsStale());

            System.Threading.Thread.Sleep(30);

            var snapshot = VerifyCacheService.GetSnapshot();
            Assert.AreEqual(HealthSummaryStatus.NoData, snapshot.Status);
            Assert.IsNull(snapshot.AsOf);
            Assert.IsNull(snapshot.Summary);
            Assert.IsTrue(VerifyCacheService.IsStale());
        }

        [Test]
        public void Clear_ResetsState_AndGetSnapshotReturnsNoData()
        {
            VerifyCacheService.Record(MakeResult(), VerifyCacheService.SourceValidateEdit);
            VerifyCacheService.Clear();

            Assert.IsFalse(VerifyCacheService.HasData);
            Assert.AreEqual(HealthSummaryStatus.NoData, VerifyCacheService.GetSnapshot().Status);
        }

        [Test]
        public void Record_IgnoresNullResult_AndLeavesStateUntouched()
        {
            VerifyCacheService.Record(MakeResult(), VerifyCacheService.SourceValidateEdit);
            var before = VerifyCacheService.GetSnapshot();
            VerifyCacheService.Record(null, VerifyCacheService.SourceGate);

            var after = VerifyCacheService.GetSnapshot();
            Assert.AreEqual(before.AsOf, after.AsOf);
            Assert.AreEqual(before.Source, after.Source);
        }

        [Test]
        public void Record_ZeroIssues_ProducesZeroSummary()
        {
            VerifyCacheService.Record(MakeResult(), VerifyCacheService.SourceScanPaths);

            var summary = VerifyCacheService.GetSnapshot().Summary;
            Assert.AreEqual(0, summary.error);
            Assert.AreEqual(0, summary.warn);
            Assert.AreEqual(VerifyCacheService.SourceScanPaths, VerifyCacheService.GetSnapshot().Source);
        }

        [Test]
        public void Ttl_NonPositiveValue_FallsBackToDefault()
        {
            VerifyCacheService.Ttl = TimeSpan.Zero;
            Assert.AreEqual(VerifyCacheService.DefaultTtl, VerifyCacheService.Ttl);

            VerifyCacheService.Ttl = TimeSpan.FromSeconds(-5);
            Assert.AreEqual(VerifyCacheService.DefaultTtl, VerifyCacheService.Ttl);
        }

        static VerifyResult MakeResult(params VerifyIssue[] issues)
        {
            return new VerifyResult(
                new List<VerifyIssue>(issues),
                new[] { "missing_references" },
                10L);
        }

        static VerifyResult MakeResult(int errors, int warnings)
        {
            var issues = new List<VerifyIssue>();
            for (int i = 0; i < errors; i++)
                issues.Add(new VerifyIssue("missing_references", VerifySeverity.Error,
                    "Assets/E" + i + ".prefab", "MISSING_SCRIPT", "err " + i));
            for (int i = 0; i < warnings; i++)
                issues.Add(new VerifyIssue("scene_prefab_health", VerifySeverity.Warning,
                    "Assets/W" + i + ".unity", "DEEP", "warn " + i));
            return new VerifyResult(issues, new[] { "missing_references" }, 5L);
        }
    }
}
