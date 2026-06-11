namespace UnityAgentVerify
{
    public static class IssueKey
    {
        public static string Build(string ruleId, VerifySeverity severity, string assetPath, string issueCode)
        {
            var sev = severity == VerifySeverity.Error ? "ERROR" : "WARN";
            return $"{ruleId}|{sev}|{assetPath}|{issueCode}";
        }

        public static string Build(VerifyIssue issue)
        {
            return Build(issue.RuleId, issue.Severity, issue.AssetPath, issue.IssueCode);
        }
    }
}
