using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace UnityOpenMcpVerify.Batch
{
    public static class BaselineStore
    {
        public static BaselineFile CreateFromResult(VerifyResult result, string platformProfile)
        {
            // Rules that threw get no baseline entry and no summary
            // contribution. A rule that throws mid-scan can leave PARTIAL
            // issues in the list (it appends to the sink as it goes, then
            // throws), so the summary must filter by RuleId rather than
            // trusting the issue list to be all-or-nothing per rule.
            var failed = result != null && result.RulesFailed != null
                ? new HashSet<string>(result.RulesFailed)
                : new HashSet<string>();

            var baseline = new BaselineFile
            {
                schemaVersion = BaselineSchema.Version,
                platformProfile = platformProfile,
                generatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                summary = BuildSummary(result != null ? result.Issues : null, failed),
                rules = BuildRuleEntries(result, failed)
            };
            if (result != null && result.HasFailedRules)
                baseline.rulesFailed.AddRange(result.RulesFailed);
            return baseline;
        }

        public static void Save(BaselineFile baseline, string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Baseline path must not be empty.", nameof(path));

            var json = JsonUtility.ToJson(baseline, true);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, json);
        }

        public static BaselineFile Load(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Baseline path must not be empty.", nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Baseline file not found: {path}", path);

            var json = File.ReadAllText(path);
            var baseline = JsonUtility.FromJson<BaselineFile>(json);

            if (baseline == null)
                throw new InvalidOperationException(
                    $"Failed to parse baseline JSON from: {path}");

            if (baseline.schemaVersion != BaselineSchema.Version)
                throw new InvalidOperationException(
                    $"Baseline schema version mismatch: expected {BaselineSchema.Version}, " +
                    $"got {baseline.schemaVersion} in '{path}'. " +
                    "Regenerate the baseline with unity_open_mcp_baseline_create.");

            return baseline;
        }

        public static RegressionDetail Compare(
            BaselineFile current,
            BaselineFile baseline,
            int errorThreshold)
        {
            return Compare(current, baseline, errorThreshold, null);
        }

        // Per-category regression gate. `perCategoryThresholds` maps a ruleId to
        // the max tolerated increase in that rule's error count; rules absent
        // from the map fall back to `errorThreshold`. A null/empty map reduces
        // to the global-threshold-only path (perRule stays null), so existing
        // callers and fixtures see an unchanged response.
        //
        // The overall `regressed` verdict is the OR of the global check and
        // every per-rule check — a regression under any scope fails the gate.
        // A rule that threw on EITHER side makes that rule's counts unreliable
        // in both directions: absent baseline counts turn pre-existing issues
        // into phantom "new" ones, absent current counts hide real regressions
        // and fake improvements. Rules in the union of both files' rulesFailed
        // are excluded from the global error delta and skipped by the per-rule
        // loop — the comparison certifies only the rules that ran clean on
        // both sides. The batch result's top-level rulesFailed names the
        // current scan's failures; the baseline file names its own.
        public static RegressionDetail Compare(
            BaselineFile current,
            BaselineFile baseline,
            int errorThreshold,
            System.Collections.Generic.Dictionary<string, int> perCategoryThresholds)
        {
            var failedUnion = new System.Collections.Generic.HashSet<string>();
            CollectFailed(baseline, failedUnion);
            CollectFailed(current, failedUnion);

            SeveritySummary baselineSummary;
            SeveritySummary currentSummary;
            if (failedUnion.Count == 0)
            {
                baselineSummary = baseline != null && baseline.summary != null
                    ? baseline.summary
                    : new SeveritySummary();
                currentSummary = current != null && current.summary != null
                    ? current.summary
                    : new SeveritySummary();
            }
            else
            {
                // Recompute both sides from the per-rule entries over the
                // clean rules only, so the reported summaries stay consistent
                // with the delta below (the raw file summaries include the
                // OTHER side's counts for the failed rules).
                CleanSums(baseline, failedUnion, out var baseErr, out var baseWarn);
                CleanSums(current, failedUnion, out var currErr, out var currWarn);
                baselineSummary = new SeveritySummary(baseErr, baseWarn, 0);
                currentSummary = new SeveritySummary(currErr, currWarn, 0);
            }

            int errorDelta = currentSummary.error - baselineSummary.error;
            bool globalRegressed = errorDelta > errorThreshold;

            var detail = new RegressionDetail
            {
                baselineSummary = baselineSummary,
                currentSummary = currentSummary,
                errorDelta = errorDelta,
                errorThreshold = errorThreshold,
                regressed = globalRegressed
            };

            if (perCategoryThresholds == null || perCategoryThresholds.Count == 0)
                return detail;

            // Union of ruleIds present on either side plus any the caller named
            // explicitly — a rule that newly appears with errors still has to
            // respect its threshold even if the baseline had no entry for it.
            var ruleIds = new System.Collections.Generic.HashSet<string>();
            if (baseline != null && baseline.rules != null)
                foreach (var r in baseline.rules) ruleIds.Add(r.ruleId);
            if (current != null && current.rules != null)
                foreach (var r in current.rules) ruleIds.Add(r.ruleId);
            foreach (var kv in perCategoryThresholds) ruleIds.Add(kv.Key);

            var perRule = new System.Collections.Generic.List<RuleRegressionDetail>();
            foreach (var ruleId in ruleIds)
            {
                // A rule that failed on either side has no trustworthy counts
                // in this comparison — skip it entirely rather than compare
                // against a baseline (or current) value it never established.
                if (failedUnion.Contains(ruleId)) continue;

                int baseErr = ErrorCountFor(baseline, ruleId);
                int currErr = ErrorCountFor(current, ruleId);
                int delta = currErr - baseErr;
                int threshold = perCategoryThresholds.TryGetValue(ruleId, out var perRuleThreshold)
                    ? perRuleThreshold
                    : errorThreshold;
                bool ruleRegressed = delta > threshold;

                perRule.Add(new RuleRegressionDetail
                {
                    ruleId = ruleId,
                    baselineError = baseErr,
                    currentError = currErr,
                    errorDelta = delta,
                    errorThreshold = threshold,
                    regressed = ruleRegressed
                });

                if (ruleRegressed) detail.regressed = true;
            }

            detail.perRule = perRule;
            return detail;
        }

        private static void CollectFailed(BaselineFile file, System.Collections.Generic.HashSet<string> sink)
        {
            if (file?.rulesFailed == null) return;
            foreach (var id in file.rulesFailed)
            {
                if (!string.IsNullOrEmpty(id)) sink.Add(id);
            }
        }

        // Sum error/warn counts over the file's per-rule entries, skipping the
        // rules in `failed`. Used when a side had crashed rules so the global
        // delta reflects only the rules that ran clean on both sides.
        private static void CleanSums(
            BaselineFile file,
            System.Collections.Generic.HashSet<string> failed,
            out int error,
            out int warn)
        {
            error = 0;
            warn = 0;
            if (file?.rules == null) return;
            foreach (var r in file.rules)
            {
                if (r == null || failed.Contains(r.ruleId)) continue;
                error += r.error;
                warn += r.warn;
            }
        }

        private static int ErrorCountFor(BaselineFile file, string ruleId)
        {
            if (file == null || file.rules == null) return 0;
            foreach (var r in file.rules)
            {
                if (r.ruleId == ruleId) return r.error;
            }
            return 0;
        }

        private static SeveritySummary BuildSummary(List<VerifyIssue> issues, HashSet<string> failed)
        {
            var s = new SeveritySummary();
            if (issues == null) return s;

            foreach (var issue in issues)
            {
                // A crashed rule's (possibly partial) issues must not count:
                // the summary has to agree with the per-rule entries, which
                // skip failed rules entirely.
                if (failed.Contains(issue.RuleId)) continue;
                if (issue.Severity == VerifySeverity.Error) s.error++;
                else if (issue.Severity == VerifySeverity.Warning) s.warn++;
            }
            return s;
        }

        private static List<RuleBaselineEntry> BuildRuleEntries(VerifyResult result, HashSet<string> failed)
        {
            var entries = new List<RuleBaselineEntry>();
            if (result == null) return entries;

            foreach (var ruleId in result.CategoriesRun)
            {
                // No entry for a rule that threw — an entry with error=0 would
                // claim a clean baseline the rule never established. The rule
                // id rides on baseline.rulesFailed instead, and regression
                // comparisons skip it on both sides.
                if (failed.Contains(ruleId)) continue;

                var ruleIssues = result.Issues != null
                    ? result.Issues.Where(i => i.RuleId == ruleId).ToList()
                    : new List<VerifyIssue>();

                var entry = new RuleBaselineEntry
                {
                    ruleId = ruleId,
                    error = ruleIssues.Count(i => i.Severity == VerifySeverity.Error),
                    warn = ruleIssues.Count(i => i.Severity == VerifySeverity.Warning),
                    info = 0,
                    issueKeys = ruleIssues.Select(IssueKey.Build).ToList()
                };
                entries.Add(entry);
            }
            return entries;
        }
    }
}
