using System;

namespace UnityAgentVerify
{
    public static class IssueKey
    {
        public static string Build(string ruleId, VerifySeverity severity, string assetPath, string issueCode)
        {
            ValidateComponents(ruleId, severity, assetPath, issueCode);
            var sev = severity == VerifySeverity.Error ? "ERROR" : "WARN";
            return $"{ruleId}|{sev}|{assetPath}|{issueCode}";
        }

        public static string Build(VerifyIssue issue)
        {
            return Build(issue.RuleId, issue.Severity, issue.AssetPath, issue.IssueCode);
        }

        public static bool TryParse(string key, out string ruleId, out VerifySeverity severity, out string assetPath, out string issueCode)
        {
            ruleId = null;
            severity = default;
            assetPath = null;
            issueCode = null;

            if (string.IsNullOrEmpty(key)) return false;

            var parts = key.Split('|');
            if (parts.Length != 4) return false;

            ruleId = parts[0];
            var sevStr = parts[1];
            assetPath = parts[2];
            issueCode = parts[3];

            if (string.IsNullOrEmpty(ruleId)) return false;
            if (sevStr != "ERROR" && sevStr != "WARN") return false;
            if (string.IsNullOrEmpty(assetPath)) return false;
            if (string.IsNullOrEmpty(issueCode)) return false;

            severity = sevStr == "ERROR" ? VerifySeverity.Error : VerifySeverity.Warning;
            return true;
        }

        public static void ValidateKey(string key)
        {
            if (!TryParse(key, out _, out _, out _, out _))
                throw new FormatException($"Malformed issue key: '{key}'. Expected format: {{ruleId}}|{{severity}}|{{assetPath}}|{{issueCode}}");
        }

        static void ValidateComponents(string ruleId, VerifySeverity severity, string assetPath, string issueCode)
        {
            if (string.IsNullOrEmpty(ruleId))
                throw new ArgumentException("Issue key ruleId must not be empty.", nameof(ruleId));
            if (string.IsNullOrEmpty(assetPath))
                throw new ArgumentException("Issue key assetPath must not be empty.", nameof(assetPath));
            if (string.IsNullOrEmpty(issueCode))
                throw new ArgumentException("Issue key issueCode must not be empty.", nameof(issueCode));
            if (ruleId.Contains('|'))
                throw new ArgumentException($"Issue key ruleId must not contain '|': '{ruleId}'", nameof(ruleId));
            if (assetPath.Contains('|'))
                throw new ArgumentException($"Issue key assetPath must not contain '|': '{assetPath}'", nameof(assetPath));
            if (issueCode.Contains('|'))
                throw new ArgumentException($"Issue key issueCode must not contain '|': '{issueCode}'", nameof(issueCode));
        }
    }
}
