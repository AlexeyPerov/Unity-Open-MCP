using System.Collections.Generic;

namespace UnityOpenMcpVerify
{
    public enum VerifySeverity
    {
        Error,
        Warning
    }

    public class VerifyIssue
    {
        public string RuleId { get; }
        public VerifySeverity Severity { get; }
        public string AssetPath { get; }
        public string IssueCode { get; }
        public string Description { get; }

        // M25 Plan 3 — per-instance evidence: the specific broken ref / field /
        // value that triggered this issue (PPtr target GUID, fileID, line, the
        // expected vs actual value, etc.). Additive and optional — null when a
        // rule does not supply it, and the 5-arg constructor below still works
        // unchanged.
        //
        // The static root-cause + remediation text does NOT live here: it is
        // keyed by ruleId|issueCode in IssueExplainability (identical across
        // every instance of the same code), so it is not repeated per issue.
        public IReadOnlyDictionary<string, string> Evidence { get; }

        public VerifyIssue(string ruleId, VerifySeverity severity, string assetPath, string issueCode, string description)
            : this(ruleId, severity, assetPath, issueCode, description, null)
        {
        }

        public VerifyIssue(string ruleId, VerifySeverity severity, string assetPath, string issueCode,
            string description, IReadOnlyDictionary<string, string> evidence)
        {
            RuleId = ruleId;
            Severity = severity;
            AssetPath = assetPath;
            IssueCode = issueCode;
            Description = description;
            Evidence = evidence;
        }
    }
}
