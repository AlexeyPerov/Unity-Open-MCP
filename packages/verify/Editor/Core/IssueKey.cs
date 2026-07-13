using System;

namespace UnityOpenMcpVerify
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
            // Accept any case of the severity token. scan_paths / validate_edit
            // emit "Error"/"Warning" (SeverityStr), IssueKey.Build emits
            // "ERROR"/"WARN", and an agent may hand-transcribe either. Without
            // case-insensitive matching the documented scan→apply_fix loop fails
            // with invalid_issue_id across separate calls (specs/feedback.md
            // 2026-07-03). Also accept the long form "WARNING" for symmetry.
            if (!TryMatchSeverity(sevStr, out severity)) return false;
            if (string.IsNullOrEmpty(assetPath)) return false;
            if (string.IsNullOrEmpty(issueCode)) return false;

            return true;
        }

        // Case-insensitive severity matcher. Recognizes the short ("WARN") and
        // long ("WARNING") warning spellings plus "ERROR" so all three producers
        // (IssueKey.Build, ScanPathsTool.SeverityStr, ValidateEditTool.SeverityStr)
        // and hand-transcribed keys parse uniformly. Returns false for anything
        // else so a genuinely malformed severity (e.g. "CRITICAL") still rejects.
        private static bool TryMatchSeverity(string sevStr, out VerifySeverity severity)
        {
            severity = VerifySeverity.Warning;
            if (string.IsNullOrEmpty(sevStr)) return false;
            switch (sevStr.ToUpperInvariant())
            {
                case "ERROR": severity = VerifySeverity.Error; return true;
                case "WARN":
                case "WARNING": severity = VerifySeverity.Warning; return true;
                default: return false;
            }
        }

        public static void ValidateKey(string key)
        {
            if (!TryParse(key, out _, out _, out _, out _))
                throw new FormatException($"Malformed issue key: '{key}'. Expected format: {{ruleId}}|{{severity}}|{{assetPath}}|{{issueCode}}");
        }

        // Some issue codes carry a GUID suffix (e.g. "missing_guid:<guid>") so
        // the fix provider can identify exactly which broken reference to
        // rewrite when an asset has multiple. The bare code (without the
        // suffix) is what IssueExplainability and FixProviderRegistry key on.
        // These helpers strip / extract the suffix. Codes without a ":" suffix
        // are returned as-is (the common case).
        public static string BareIssueCode(string issueCode)
        {
            if (string.IsNullOrEmpty(issueCode)) return issueCode;
            var colon = issueCode.IndexOf(':');
            return colon < 0 ? issueCode : issueCode.Substring(0, colon);
        }

        // Extract the GUID suffix from an issueCode like "missing_guid:<guid>".
        // Returns null when the code has no suffix.
        public static string IssueCodeGuid(string issueCode)
        {
            if (string.IsNullOrEmpty(issueCode)) return null;
            var colon = issueCode.IndexOf(':');
            if (colon < 0 || colon + 1 >= issueCode.Length) return null;
            return issueCode.Substring(colon + 1);
        }

        private static void ValidateComponents(string ruleId, VerifySeverity severity, string assetPath, string issueCode)
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
