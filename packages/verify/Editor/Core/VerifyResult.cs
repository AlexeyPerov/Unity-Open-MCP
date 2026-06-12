using System.Collections.Generic;

namespace UnityAgentVerify
{
    public class VerifyResult
    {
        public List<VerifyIssue> Issues { get; }
        public string[] CategoriesRun { get; }
        public long DurationMs { get; }
        public string[] UnknownRuleIds { get; }
        public string[] AvailableRuleIds { get; }
        public bool HasUnknownRules => UnknownRuleIds != null && UnknownRuleIds.Length > 0;

        public VerifyResult(List<VerifyIssue> issues, string[] categoriesRun, long durationMs,
            string[] unknownRuleIds = null, string[] availableRuleIds = null)
        {
            Issues = issues;
            CategoriesRun = categoriesRun;
            DurationMs = durationMs;
            UnknownRuleIds = unknownRuleIds ?? System.Array.Empty<string>();
            AvailableRuleIds = availableRuleIds ?? System.Array.Empty<string>();
        }
    }
}
