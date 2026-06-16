using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Batch;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class BaselineStoreTests
    {
        // -------------------------------------------------------------------
        // Compare — errorDelta + regressed verdict (the CI regression gate)
        // -------------------------------------------------------------------

        [Test]
        public void Compare_Regressed_WhenErrorDeltaExceedsThreshold()
        {
            var baseline = BaselineWith(2);
            var current = BaselineWith(5);

            var detail = BaselineStore.Compare(current, baseline, errorThreshold: 0);

            Assert.AreEqual(3, detail.errorDelta, "+3 errors over baseline");
            Assert.True(detail.regressed, "any positive delta over a zero threshold regresses");
        }

        [Test]
        public void Compare_NotRegressed_WhenErrorDeltaWithinThreshold()
        {
            var baseline = BaselineWith(2);
            var current = BaselineWith(4); // +2

            var detail = BaselineStore.Compare(current, baseline, errorThreshold: 2);

            Assert.AreEqual(2, detail.errorDelta);
            Assert.False(detail.regressed, "delta equal to the threshold is tolerated");
        }

        [Test]
        public void Compare_NotRegressed_WhenErrorsDecreased()
        {
            var baseline = BaselineWith(5);
            var current = BaselineWith(1); // -4

            var detail = BaselineStore.Compare(current, baseline, errorThreshold: 0);

            Assert.AreEqual(-4, detail.errorDelta);
            Assert.False(detail.regressed, "fewer errors than baseline is an improvement");
        }

        [Test]
        public void Compare_TreatsNullBaseline_AsZeroErrors()
        {
            // No baseline yet -> treated as 0 errors -> any current error regresses.
            var current = BaselineWith(1);

            var detail = BaselineStore.Compare(current, null, errorThreshold: 0);

            Assert.AreEqual(1, detail.errorDelta);
            Assert.True(detail.regressed, "first run with errors regresses against an empty baseline");
        }

        [Test]
        public void Compare_TreatsBaselineWithNullSummary_AsZeroErrors()
        {
            var baseline = new BaselineFile { summary = null };
            var current = BaselineWith(3);

            var detail = BaselineStore.Compare(current, baseline, errorThreshold: 0);

            Assert.AreEqual(3, detail.errorDelta);
            Assert.True(detail.regressed);
        }

        [Test]
        public void Compare_PopulatesBothSummaries_AndThreshold()
        {
            var baseline = BaselineWith(2);
            var current = BaselineWith(4);

            var detail = BaselineStore.Compare(current, baseline, errorThreshold: 1);

            Assert.AreEqual(2, detail.baselineSummary.error);
            Assert.AreEqual(4, detail.currentSummary.error);
            Assert.AreEqual(1, detail.errorThreshold);
        }

        // -------------------------------------------------------------------
        // CreateFromResult — BuildSummary + BuildRuleEntries
        // -------------------------------------------------------------------

        [Test]
        public void CreateFromResult_CountsErrorsAndWarnings_BySeverity()
        {
            var result = ResultWith(errors: 3, warnings: 2, categories: new[] { "missing_references" });

            var baseline = BaselineStore.CreateFromResult(result, "desktop");

            Assert.AreEqual(BaselineSchema.Version, baseline.schemaVersion);
            Assert.AreEqual("desktop", baseline.platformProfile);
            Assert.AreEqual(3, baseline.summary.error);
            Assert.AreEqual(2, baseline.summary.warn);
        }

        [Test]
        public void CreateFromResult_GroupsIssues_ByRule()
        {
            var issues = new List<VerifyIssue>
            {
                new VerifyIssue("missing_references", VerifySeverity.Error, "Assets/A.prefab", "missing_script", "a"),
                new VerifyIssue("missing_references", VerifySeverity.Warning, "Assets/B.prefab", "missing_guid", "b"),
                new VerifyIssue("scene_prefab_health", VerifySeverity.Error, "Assets/C.unity", "hotspot", "c"),
            };
            var result = new VerifyResult(issues, new[] { "missing_references", "scene_prefab_health" }, 0);

            var baseline = BaselineStore.CreateFromResult(result, "desktop");

            Assert.AreEqual(2, baseline.rules.Count);

            var mr = baseline.rules.Find(r => r.ruleId == "missing_references");
            Assert.AreEqual(1, mr.error);
            Assert.AreEqual(1, mr.warn);
            Assert.AreEqual(2, mr.issueKeys.Count, "one issueKey per issue in the rule");

            var sph = baseline.rules.Find(r => r.ruleId == "scene_prefab_health");
            Assert.AreEqual(1, sph.error);
            Assert.AreEqual(0, sph.warn);
            Assert.AreEqual(1, sph.issueKeys.Count);
        }

        [Test]
        public void CreateFromResult_NullResult_ProducesEmptyBaseline()
        {
            var baseline = BaselineStore.CreateFromResult(null, "desktop");

            Assert.AreEqual(0, baseline.summary.error);
            Assert.AreEqual(0, baseline.rules.Count);
        }

        [Test]
        public void CreateFromResult_IssuesWithoutMatchingCategory_AreNotDroppedFromSummary()
        {
            // An issue's rule may not appear in CategoriesRun; the summary still
            // counts it, but BuildRuleEntries only emits entries for known categories.
            var issues = new List<VerifyIssue>
            {
                new VerifyIssue("orphan_rule", VerifySeverity.Error, "Assets/X.prefab", "x", "y"),
            };
            var result = new VerifyResult(issues, new[] { "missing_references" }, 0);

            var baseline = BaselineStore.CreateFromResult(result, "desktop");

            Assert.AreEqual(1, baseline.summary.error, "summary counts every issue regardless of category");
            // The orphan rule has no category entry.
            Assert.AreEqual(1, baseline.rules.Count);
            Assert.AreEqual("missing_references", baseline.rules[0].ruleId);
            Assert.AreEqual(0, baseline.rules[0].error, "the category's own issues are counted, not the orphan's");
        }

        // -------------------------------------------------------------------
        // Save / Load — round-trip + schema-version guard
        // -------------------------------------------------------------------

        [Test]
        public void SaveThenLoad_RoundTripsTheBaseline()
        {
            var path = Path.Combine(Application.temporaryCachePath, $"baseline-rt-{Guid.NewGuid()}.json");
            try
            {
                var original = BaselineStore.CreateFromResult(
                    ResultWith(2, 1, new[] { "missing_references" }), "desktop");
                BaselineStore.Save(original, path);

                var loaded = BaselineStore.Load(path);

                Assert.AreEqual(BaselineSchema.Version, loaded.schemaVersion);
                Assert.AreEqual("desktop", loaded.platformProfile);
                Assert.AreEqual(2, loaded.summary.error);
                Assert.AreEqual(1, loaded.rules.Count);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void Load_RejectsWrongSchemaVersion()
        {
            var path = Path.Combine(Application.temporaryCachePath, $"baseline-ver-{Guid.NewGuid()}.json");
            try
            {
                var bad = new BaselineFile { schemaVersion = BaselineSchema.Version + 999, platformProfile = "desktop" };
                BaselineStore.Save(bad, path);

                var ex = Assert.Throws<InvalidOperationException>(() => BaselineStore.Load(path));
                Assert.That(ex.Message, Does.Contain("schema version mismatch"));
                Assert.That(ex.Message, Does.Contain("Regenerate the baseline"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void Load_MissingFile_Throws()
        {
            var path = Path.Combine(Application.temporaryCachePath, $"baseline-missing-{Guid.NewGuid()}.json");
            Assert.Throws<FileNotFoundException>(() => BaselineStore.Load(path));
        }

        [Test]
        public void Save_EmptyPath_Throws()
        {
            Assert.Throws<ArgumentException>(() => BaselineStore.Save(new BaselineFile(), ""));
        }

        // -------------------------------------------------------------------
        // helpers
        // -------------------------------------------------------------------

        private static BaselineFile BaselineWith(int errors)
        {
            return new BaselineFile
            {
                schemaVersion = BaselineSchema.Version,
                summary = new SeveritySummary(errors, 0, 0),
            };
        }

        private static VerifyResult ResultWith(int errors, int warnings, string[] categories)
        {
            var issues = new List<VerifyIssue>();
            for (int i = 0; i < errors; i++)
            {
                issues.Add(new VerifyIssue(categories.Length > 0 ? categories[0] : "test_rule",
                    VerifySeverity.Error, "Assets/T.prefab", "test_error", $"e{i}"));
            }
            for (int i = 0; i < warnings; i++)
            {
                issues.Add(new VerifyIssue(categories.Length > 0 ? categories[0] : "test_rule",
                    VerifySeverity.Warning, "Assets/T.prefab", "test_warn", $"w{i}"));
            }
            return new VerifyResult(issues, categories, 0);
        }
    }
}
