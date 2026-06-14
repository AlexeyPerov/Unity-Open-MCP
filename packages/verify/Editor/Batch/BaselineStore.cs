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
            var baselineSummary = baseline != null && baseline.summary != null
                ? baseline.summary
                : new SeveritySummary();

            var currentSummary = current != null && current.summary != null
                ? current.summary
                : new SeveritySummary();

            int errorDelta = currentSummary.error - baselineSummary.error;
            bool regressed = errorDelta > errorThreshold;

            return new RegressionDetail
            {
                baselineSummary = baselineSummary,
                currentSummary = currentSummary,
                errorDelta = errorDelta,
                errorThreshold = errorThreshold,
                regressed = regressed
            };
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
