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
            var failOnSeverityRaw = JsonBody.GetString(body, "fail_on_severity");
            if (string.IsNullOrEmpty(failOnSeverityRaw))
                failOnSeverityRaw = VerifyProjectSettings.SeverityThreshold;

            // B42 — fail closed on an unrecognized fail_on_severity. The local
            // ShouldFail switch had a `_ => false` arm that mapped ANY unknown
            // token (typos like "warning", "critical", "errors") to "never
            // fail", so the response reported passed:true while still listing
            // the errors. batch_execute re-enters DispatchTool with agent-
            // authored bodies that bypass the MCP schema, and the canonical
            // SeverityThreshold.Parse *throws* on unknown values — so an
            // unrecognized value here is a real contract violation. Validate
            // against the canonical parser and reject before scanning so the
            // caller sees a structured error instead of a false pass. (The
            // project default from VerifyProjectSettings is already normalized
            // and never trips this path.)
            //
            // A8 — the previous ShouldFail switch ran on the RAW string, so a
            // case-variant like "Error" or "WARN" passed Parse (which lower-
            // cases) but then fell through the switch's `_ => false` arm and
            // reported passed:true with the errors still listed. Parse the
            // value ONCE to the canonical FailSeverity enum and derive the
            // echoed string from it, so the decision and the wire value both
            // use the normalized form. (batch_execute bypasses the MCP schema
            // entirely, so this is the only enforcement point.)
            FailSeverity failSeverity;
            try
            {
                failSeverity = SeverityThreshold.Parse(failOnSeverityRaw);
            }
            catch (System.Exception ex)
            {
                return ToolDispatchResult.Fail("invalid_argument", ex.Message);
            }
            var failOnSeverity = SeverityThreshold.ToString(failSeverity);

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

            return ToolDispatchResult.Ok(BuildResult(result, filtered.RulesApplied, failSeverity, failOnSeverity));
        }

        private static string BuildResult(VerifyResult result, string[] rulesApplied, FailSeverity failSeverity, string failOnSeverity)
        {
            var sb = new StringBuilder(1024);
            // A8 — use the canonical ShouldFail against the parsed enum so a
            // case-variant threshold ("Error", "WARN") cannot slip through a
            // raw-string switch's default arm and report passed:true.
            var hasFailures = SeverityThreshold.ShouldFail(failSeverity, result);

            sb.Append("{\"passed\":").Append(!hasFailures ? "true" : "false");
            // Echo the resolved threshold so an agent reading the response knows
            // whether the project default or a per-call value was applied.
            // Always the normalized canonical token (never the raw input).
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
                // B-N14 — `code` is the BARE catalog code (e.g. "missing_script")
                // that an agent matches against the rule-catalog / SKILL.md
                // (both advertise the bare tokens). `issueCode` carries the full
                // key-discriminator form ("missing_script:<guid>",
                // "invalid_layer:7", …) that IssueKey.Build uses for delta
                // tracking and apply_fix uses to target the exact instance. The
                // two were previously identical, so `code === "missing_script"`
                // never matched because the value was `missing_script:<guid>`.
                var bareCode = IssueKey.BareIssueCode(issue.IssueCode);
                sb.Append("\"code\":\"").Append(Esc(bareCode)).Append("\",");
                sb.Append("\"issueCode\":\"").Append(Esc(issue.IssueCode)).Append("\",");
                sb.Append("\"assetPath\":\"").Append(Esc(issue.AssetPath)).Append("\",");
                sb.Append("\"description\":\"").Append(Esc(issue.Description)).Append("\"");

                // M25 Plan 3 — explainability. rootCause + remediation are
                // static per issue class (looked up from IssueExplainability);
                // evidence is per-instance (the specific broken ref/value).
                // fixCandidates advertises every fix option (safe vs unsafe),
                // superseding the single fixId/fixSafe pair below (kept for
                // backwards compatibility).
                //
                // The explainability table is keyed by the bare code (computed
                // above for the `code` wire field). FixProviderRegistry uses
                // CanFix which handles both forms.
                if (IssueExplainability.TryGet(issue.RuleId, bareCode, out var explain))
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
                var candidates = FixProviderRegistry.CandidatesForIssue(issue.RuleId, bareCode);
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
                if (FixProviderRegistry.TryGetFixInfo(issue.RuleId, bareCode, out var fixId, out var safe))
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
