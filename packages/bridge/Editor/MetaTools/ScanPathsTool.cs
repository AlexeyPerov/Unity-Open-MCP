using System.Text;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Batch;
using UnityOpenMcpVerify.Fixes;

namespace UnityOpenMcpBridge.MetaTools
{
    public static class ScanPathsTool
    {
        public static ToolDispatchResult Execute(string body)
        {
            var paths = JsonBody.GetStringArray(body, "paths");
            if (paths == null || paths.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'paths' is required and must be a non-empty array.");

            var categories = JsonBody.GetStringArray(body, "categories");
            var includeRules = JsonBody.GetStringArray(body, "include_rules");
            var excludeRules = JsonBody.GetStringArray(body, "exclude_rules");

            // fail_on_severity is optional. When omitted, fall back to the
            // project-default from `.unity-open-mcp/settings.json`
            // (verify.severityThreshold) — a demo project can set this to
            // "warning" so warnings also flip `passed:false`. Explicit
            // per-call values always win.
            var failOnSeverity = JsonBody.GetString(body, "fail_on_severity");
            if (string.IsNullOrEmpty(failOnSeverity))
                failOnSeverity = VerifyProjectSettings.SeverityThreshold;

            FilteredVerifyResult filtered;
            try
            {
                filtered = VerifyGateAdapter.ScanFiltered(paths, categories, includeRules, excludeRules);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("scan_error", e.Message);
            }

            var result = filtered.Result;

            if (result.HasUnknownRules)
                return ToolDispatchResult.Ok(
                    BuildUnknownRulesError(result.UnknownRuleIds, result.AvailableRuleIds));

            return ToolDispatchResult.Ok(BuildResult(result, filtered.RulesApplied, failOnSeverity));
        }

        private static bool ShouldFail(VerifySeverity severity, string failOnSeverity)
        {
            return failOnSeverity switch
            {
                "error" => severity == VerifySeverity.Error,
                "warn" => severity == VerifySeverity.Error || severity == VerifySeverity.Warning,
                "info" => true,
                "verbose" => true,
                _ => false
            };
        }

        private static string BuildResult(VerifyResult result, string[] rulesApplied, string failOnSeverity)
        {
            var sb = new StringBuilder(1024);
            var hasFailures = result.Issues.Exists(i => ShouldFail(i.Severity, failOnSeverity));

            sb.Append("{\"passed\":").Append(!hasFailures ? "true" : "false");
            // Echo the resolved threshold so an agent reading the response knows
            // whether the project default or a per-call value was applied.
            sb.Append(",\"failOnSeverity\":\"").Append(Esc(failOnSeverity)).Append("\"");
            sb.Append(",\"issues\":[");
            for (int i = 0; i < result.Issues.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var issue = result.Issues[i];
                sb.Append('{');
                // categoryId is the spec-named alias for ruleId (T2.6). Both
                // are emitted so agents can match either the catalog field or
                // the issue-key component.
                sb.Append("\"ruleId\":\"").Append(Esc(issue.RuleId)).Append("\",");
                sb.Append("\"categoryId\":\"").Append(Esc(issue.RuleId)).Append("\",");
                sb.Append("\"severity\":\"").Append(SeverityStr(issue.Severity)).Append("\",");
                sb.Append("\"code\":\"").Append(Esc(issue.IssueCode)).Append("\",");
                sb.Append("\"issueCode\":\"").Append(Esc(issue.IssueCode)).Append("\",");
                sb.Append("\"assetPath\":\"").Append(Esc(issue.AssetPath)).Append("\",");
                sb.Append("\"description\":\"").Append(Esc(issue.Description)).Append("\"");

                // M25 Plan 3 — explainability. rootCause + remediation are
                // static per issue class (looked up from IssueExplainability);
                // evidence is per-instance (the specific broken ref/value).
                // fixCandidates advertises every fix option (safe vs unsafe),
                // superseding the single fixId/fixSafe pair below (kept for
                // backwards compatibility).
                if (IssueExplainability.TryGet(issue.RuleId, issue.IssueCode, out var explain))
                {
                    sb.Append(",\"rootCause\":\"").Append(Esc(explain.RootCause)).Append("\"");
                    sb.Append(",\"remediation\":\"").Append(Esc(explain.Remediation)).Append("\"");
                }
                if (issue.Evidence != null && issue.Evidence.Count > 0)
                {
                    sb.Append(",\"evidence\":{");
                    int ei = 0;
                    foreach (var kv in issue.Evidence)
                    {
                        if (ei++ > 0) sb.Append(',');
                        sb.Append('"').Append(Esc(kv.Key)).Append("\":\"").Append(Esc(kv.Value ?? "")).Append('"');
                    }
                    sb.Append('}');
                }
                var candidates = FixProviderRegistry.CandidatesForIssue(issue.RuleId, issue.IssueCode);
                if (candidates.Length > 0)
                {
                    sb.Append(",\"fixCandidates\":[");
                    for (int ci = 0; ci < candidates.Length; ci++)
                    {
                        if (ci > 0) sb.Append(',');
                        sb.Append("{\"fixId\":\"").Append(Esc(candidates[ci].FixId)).Append("\"");
                        sb.Append(",\"safe\":").Append(candidates[ci].Safe ? "true" : "false");
                        sb.Append('}');
                    }
                    sb.Append(']');
                }
                if (FixProviderRegistry.TryGetFixInfo(issue.RuleId, issue.IssueCode, out var fixId, out var safe))
                {
                    sb.Append(",\"fixId\":\"").Append(Esc(fixId)).Append("\"");
                    sb.Append(",\"fixSafe\":").Append(safe ? "true" : "false");
                }
                sb.Append('}');
            }
            sb.Append(']');

            // categoriesRun mirrors the historical name (ruleIds that ran).
            sb.Append(",\"categoriesRun\":[");
            if (result.CategoriesRun != null)
            {
                for (int i = 0; i < result.CategoriesRun.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Esc(result.CategoriesRun[i])).Append('"');
                }
            }
            sb.Append(']');

            // rulesApplied is the post-filter effective rule set — distinct
            // from categoriesRun when includeRules/excludeRules were applied.
            sb.Append(",\"rulesApplied\":[");
            if (rulesApplied != null)
            {
                for (int i = 0; i < rulesApplied.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Esc(rulesApplied[i])).Append('"');
                }
            }
            sb.Append(']');

            sb.Append(",\"durationMs\":").Append(result.DurationMs);
            sb.Append('}');

            return sb.ToString();
        }

        private static string BuildUnknownRulesError(string[] unknownIds, string[] availableIds)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":\"unknown_rule\"");
            sb.Append(",\"message\":\"Unknown rule IDs: ")
                .Append(Esc(string.Join(", ", unknownIds))).Append("\"");
            sb.Append(",\"unknownRules\":[");
            for (int i = 0; i < unknownIds.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(unknownIds[i])).Append('"');
            }
            sb.Append("],\"availableRules\":[");
            for (int i = 0; i < availableIds.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(availableIds[i])).Append('"');
            }
            sb.Append("]}}");
            return sb.ToString();
        }

        private static string SeverityStr(VerifySeverity s) => s switch
        {
            VerifySeverity.Error => "Error",
            VerifySeverity.Warning => "Warning",
            _ => "Info"
        };

        // Single source of truth for JSON string-content escaping is BridgeJson
        // (T30.5). This returns the escaped CONTENT (no surrounding quotes),
        // matching the call sites here that wrap with `"..."` — and preserves
        // the `null ⇒ ""` contract.
        private static string Esc(string s) => BridgeJson.EscapeStringContent(s);
    }
}
