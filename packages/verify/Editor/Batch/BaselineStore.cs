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
            var baseline = new BaselineFile
            {
                schemaVersion = BaselineSchema.Version,
                platformProfile = platformProfile,
                generatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                summary = BuildSummary(result != null ? result.Issues : null),
                rules = BuildRuleEntries(result)
            };
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
        public static RegressionDetail Compare(
            BaselineFile current,
            BaselineFile baseline,
            int errorThreshold,
            System.Collections.Generic.Dictionary<string, int> perCategoryThresholds)
        {
            var baselineSummary = baseline != null && baseline.summary != null
                ? baseline.summary
                : new SeveritySummary();

            var currentSummary = current != null && current.summary != null
                ? current.summary
                : new SeveritySummary();

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

        static int ErrorCountFor(BaselineFile file, string ruleId)
        {
            if (file == null || file.rules == null) return 0;
            foreach (var r in file.rules)
            {
                if (r.ruleId == ruleId) return r.error;
            }
            return 0;
        }

        static SeveritySummary BuildSummary(List<VerifyIssue> issues)
        {
            var s = new SeveritySummary();
            if (issues == null) return s;

            foreach (var issue in issues)
            {
                if (issue.Severity == VerifySeverity.Error) s.error++;
                else if (issue.Severity == VerifySeverity.Warning) s.warn++;
            }
            return s;
        }

        static List<RuleBaselineEntry> BuildRuleEntries(VerifyResult result)
        {
            var entries = new List<RuleBaselineEntry>();
            if (result == null) return entries;

            foreach (var ruleId in result.CategoriesRun)
            {
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
