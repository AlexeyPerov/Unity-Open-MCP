using System.Text;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Fixes;

namespace UnityOpenMcpBridge.MetaTools
{
    public static class ApplyFixTool
    {
        public static ToolDispatchResult Execute(string body)
        {
            var fixId = JsonBody.GetString(body, "fix_id");
            var issueId = JsonBody.GetString(body, "issue_id");
            var dryRun = JsonBody.GetBool(body, "dry_run", true);

            if (string.IsNullOrEmpty(fixId))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'fix_id' is required and must be non-empty.");

            if (string.IsNullOrEmpty(issueId))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'issue_id' is required and must be non-empty.");

            if (!IssueKey.TryParse(issueId, out _, out _, out _, out _))
                return ToolDispatchResult.Fail("invalid_issue_id",
                    $"Issue id '{issueId}' is not a valid issue key. Expected format: {{ruleId}}|{{severity}}|{{assetPath}}|{{issueCode}}");

            var provider = FixProviderRegistry.Find(fixId);
            if (provider == null)
                return ToolDispatchResult.Ok(BuildUnknownFixError(fixId));

            if (!provider.CanFix(issueId))
                return ToolDispatchResult.Fail("fix_not_applicable",
                    $"Fix '{fixId}' cannot be applied to issue '{issueId}'.");

            if (dryRun)
            {
                var desc = provider.Describe(issueId);
                return ToolDispatchResult.Ok(BuildDryRunResult(desc));
            }

            FixResult result;
            try
            {
                result = provider.Apply(issueId);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("fix_error",
                    $"Fix application failed: {e.Message}");
            }

            if (!result.Success)
                return ToolDispatchResult.Fail("fix_failed", result.Description);

            return ToolDispatchResult.Ok(BuildApplyResult(result));
        }

        static string BuildDryRunResult(FixDescription desc)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"dryRun\":true");
            sb.Append(",\"fixId\":\"").Append(Esc(desc.FixId)).Append("\"");
            sb.Append(",\"issueId\":\"").Append(Esc(desc.IssueId)).Append("\"");
            sb.Append(",\"assetPath\":\"").Append(Esc(desc.AssetPath)).Append("\"");
            sb.Append(",\"description\":\"").Append(Esc(desc.Description)).Append("\"");
            sb.Append(",\"safe\":").Append(desc.Safe ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        static string BuildApplyResult(FixResult result)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"dryRun\":false");
            sb.Append(",\"success\":true");
            sb.Append(",\"description\":\"").Append(Esc(result.Description)).Append("\"");
            sb.Append(",\"touchedPaths\":[");
            if (result.TouchedPaths != null)
            {
                for (int i = 0; i < result.TouchedPaths.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Esc(result.TouchedPaths[i])).Append('"');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string BuildUnknownFixError(string fixId)
        {
            var available = FixProviderRegistry.AvailableFixIds();
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":\"unknown_fix\"");
            sb.Append(",\"message\":\"Unknown fix id '").Append(Esc(fixId)).Append("'.\"");
            sb.Append(",\"availableFixIds\":[");
            for (int i = 0; i < available.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(available[i])).Append('"');
            }
            sb.Append("]}}");
            return sb.ToString();
        }

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
