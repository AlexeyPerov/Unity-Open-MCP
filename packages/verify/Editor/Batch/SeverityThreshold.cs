using System;

namespace UnityOpenMcpVerify.Batch
{
    public enum FailSeverity
    {
        Never = 0,
        Verbose = 1,
        Info = 2,
        Warn = 3,
        Error = 4
    }

    public static class SeverityThreshold
    {
        public static readonly string[] ValidValues =
            { "error", "warn", "info", "verbose", "never" };

        public static FailSeverity Parse(string value)
        {
            if (string.IsNullOrEmpty(value))
                return FailSeverity.Never;

            switch (value.ToLowerInvariant())
            {
                case "never": return FailSeverity.Never;
                case "verbose": return FailSeverity.Verbose;
                case "info": return FailSeverity.Info;
                case "warn": return FailSeverity.Warn;
                case "error": return FailSeverity.Error;
                default:
                    throw new ArgumentException(
                        $"Unknown fail-on-severity value '{value}'. " +
                        $"Expected one of: {string.Join(", ", ValidValues)}.");
            }
        }

        public static bool ShouldFail(FailSeverity threshold, VerifyResult result)
        {
            if (threshold == FailSeverity.Never)
                return false;

            int errorCount = 0;
            int warnCount = 0;

            if (result != null && result.Issues != null)
            {
                foreach (var issue in result.Issues)
                {
                    if (issue.Severity == VerifySeverity.Error) errorCount++;
                    else if (issue.Severity == VerifySeverity.Warning) warnCount++;
                }
            }

            switch (threshold)
            {
                case FailSeverity.Error:
                    return errorCount > 0;
                case FailSeverity.Warn:
                case FailSeverity.Info:
                case FailSeverity.Verbose:
                    return errorCount > 0 || warnCount > 0;
                default:
                    return false;
            }
        }

        public static string ToString(FailSeverity threshold)
        {
            switch (threshold)
            {
                case FailSeverity.Never: return "never";
                case FailSeverity.Verbose: return "verbose";
                case FailSeverity.Info: return "info";
                case FailSeverity.Warn: return "warn";
                case FailSeverity.Error: return "error";
                default: return "never";
            }
        }
    }
}
