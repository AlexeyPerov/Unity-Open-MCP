namespace UnityAgentVerify
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

        public VerifyIssue(string ruleId, VerifySeverity severity, string assetPath, string issueCode, string description)
        {
            RuleId = ruleId;
            Severity = severity;
            AssetPath = assetPath;
            IssueCode = issueCode;
            Description = description;
        }
    }
}
