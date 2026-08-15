using System.Collections.Generic;

namespace UnityOpenMcpVerify
{
    public class VerifyResult
    {
        public List<VerifyIssue> Issues { get; }
        public string[] CategoriesRun { get; }
        public long DurationMs { get; }
        public string[] UnknownRuleIds { get; }
        public string[] AvailableRuleIds { get; }
        // Rule ids that were selected to run but threw during Scan. The issues
        // list is INCOMPLETE for these rules: a consumer must not read "no
        // issues from rule X" as "rule X found nothing" when X is listed here.
        // The gate uses this to distrust its delta (GatePolicy surfaces
        // validate_scan_failed instead of a possibly-clean verdict), and
        // scan_paths / the batch entry echo it so agents see the gap.
        public string[] RulesFailed { get; }
        public bool HasUnknownRules => UnknownRuleIds != null && UnknownRuleIds.Length > 0;
        public bool HasFailedRules => RulesFailed != null && RulesFailed.Length > 0;

        public VerifyResult(List<VerifyIssue> issues, string[] categoriesRun, long durationMs,
            string[] unknownRuleIds = null, string[] availableRuleIds = null,
            string[] rulesFailed = null)
        {
            Issues = issues;
            CategoriesRun = categoriesRun;
            DurationMs = durationMs;
            UnknownRuleIds = unknownRuleIds ?? System.Array.Empty<string>();
            AvailableRuleIds = availableRuleIds ?? System.Array.Empty<string>();
            RulesFailed = rulesFailed ?? System.Array.Empty<string>();
        }
    }
}
