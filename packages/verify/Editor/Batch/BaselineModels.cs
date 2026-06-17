using System;
using System.Collections.Generic;

namespace UnityOpenMcpVerify.Batch
{
    public static class BaselineSchema
    {
        public const int Version = 1;
    }

    [Serializable]
    public class SeveritySummary
    {
        public int error;
        public int warn;
        public int info;

        public SeveritySummary() { }

        public SeveritySummary(int error, int warn, int info)
        {
            this.error = error;
            this.warn = warn;
            this.info = info;
        }
    }

    [Serializable]
    public class RuleBaselineEntry
    {
        public string ruleId;
        public int error;
        public int warn;
        public int info;
        public List<string> issueKeys;

        public RuleBaselineEntry()
        {
            issueKeys = new List<string>();
        }
    }

    [Serializable]
    public class BaselineFile
    {
        public int schemaVersion;
        public string platformProfile;
        public string generatedAt;
        public SeveritySummary summary;
        public List<RuleBaselineEntry> rules;

        public BaselineFile()
        {
            schemaVersion = BaselineSchema.Version;
            summary = new SeveritySummary();
            rules = new List<RuleBaselineEntry>();
        }
    }

    [Serializable]
    public class BatchRuleSummary
    {
        public string ruleId;
        public int error;
        public int warn;
        public int info;
        public long durationMs;
    }

    [Serializable]
    public class IssueEntry
    {
        public string ruleId;
        public string severity;
        public string assetPath;
        public string issueCode;
        public string description;
    }

    [Serializable]
    public class RuleRegressionDetail
    {
        public string ruleId;
        public int baselineError;
        public int currentError;
        public int errorDelta;
        public int errorThreshold;
        public bool regressed;
    }

    [Serializable]
    public class RegressionDetail
    {
        public SeveritySummary baselineSummary;
        public SeveritySummary currentSummary;
        public int errorDelta;
        public int errorThreshold;
        public bool regressed;
        // Per-rule breakdown surfaced when the caller passes per-category
        // thresholds. Null when only the global error-threshold was used, so
        // older callers / fixtures that never set per-category thresholds see
        // the same JSON shape as before.
        public List<RuleRegressionDetail> perRule;
    }

    [Serializable]
    public class BatchResult
    {
        public string operation;
        public string platformProfile;
        public SeveritySummary summary;
        public List<BatchRuleSummary> rules;
        public List<IssueEntry> issues;
        public long durationMs;
        public int exitCode;
        public string failOnSeverity;
        public string baselinePath;
        public string outputPath;
        public RegressionDetail regression;
        public string error;

        public BatchResult()
        {
            summary = new SeveritySummary();
            rules = new List<BatchRuleSummary>();
            issues = new List<IssueEntry>();
        }
    }
}
