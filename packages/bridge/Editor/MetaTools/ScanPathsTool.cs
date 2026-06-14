using System.Text;
using UnityOpenMcpVerify;
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
            var failOnSeverity = JsonBody.GetString(body, "fail_on_severity") ?? "never";

            VerifyResult result;
            try
            {
                result = VerifyGateAdapter.ScanPaths(paths, categories);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("scan_error", e.Message);
            }

            if (result.HasUnknownRules)
                return ToolDispatchResult.Ok(
                    BuildUnknownRulesError(result.UnknownRuleIds, result.AvailableRuleIds));

            return ToolDispatchResult.Ok(BuildResult(result, failOnSeverity));
        }

        static bool ShouldFail(VerifySeverity severity, string failOnSeverity)
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

        static string BuildResult(VerifyResult result, string failOnSeverity)
        {
            var sb = new StringBuilder(1024);
            var hasFailures = result.Issues.Exists(i => ShouldFail(i.Severity, failOnSeverity));

            sb.Append("{\"passed\":").Append(!hasFailures ? "true" : "false");
            sb.Append(",\"issues\":[");
            for (int i = 0; i < result.Issues.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var issue = result.Issues[i];
                sb.Append('{');
                sb.Append("\"severity\":\"").Append(SeverityStr(issue.Severity)).Append("\",");
                sb.Append("\"code\":\"").Append(Esc(issue.IssueCode)).Append("\",");
                sb.Append("\"assetPath\":\"").Append(Esc(issue.AssetPath)).Append("\",");
                sb.Append("\"description\":\"").Append(Esc(issue.Description)).Append("\",");
                sb.Append("\"ruleId\":\"").Append(Esc(issue.RuleId)).Append("\"");
                if (FixProviderRegistry.TryGetFixInfo(issue.RuleId, issue.IssueCode, out var fixId, out var safe))
                {
                    sb.Append(",\"fixId\":\"").Append(Esc(fixId)).Append("\"");
                    sb.Append(",\"fixSafe\":").Append(safe ? "true" : "false");
                }
                sb.Append('}');
            }
            sb.Append(']');

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

            sb.Append(",\"durationMs\":").Append(result.DurationMs);
            sb.Append('}');

            return sb.ToString();
        }

        static string BuildUnknownRulesError(string[] unknownIds, string[] availableIds)
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

        static string SeverityStr(VerifySeverity s) => s switch
        {
            VerifySeverity.Error => "Error",
            VerifySeverity.Warning => "Warning",
            _ => "Info"
        };

        static string Esc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
