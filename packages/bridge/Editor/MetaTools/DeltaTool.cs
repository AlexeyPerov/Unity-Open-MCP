using System.Text;
using UnityOpenMcpVerify;

namespace UnityOpenMcpBridge.MetaTools
{
    public static class DeltaTool
    {
        public static ToolDispatchResult Execute(string body)
        {
            var checkpointId = JsonBody.GetString(body, "checkpoint_id");
            if (string.IsNullOrEmpty(checkpointId))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'checkpoint_id' is required.");

            var stored = CheckpointStore.Get(checkpointId);
            if (stored == null)
                return ToolDispatchResult.Fail("checkpoint_not_found",
                    $"No checkpoint found with id '{checkpointId}'.");

            var paths = JsonBody.GetStringArray(body, "paths") ?? stored.Paths;
            var categories = stored.Categories;

            VerifyResult currentResult;
            try
            {
                currentResult = VerifyGateAdapter.ValidatePaths(paths, categories);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("validation_error", e.Message);
            }

            DeltaData delta;
            try
            {
                delta = VerifyGateAdapter.ComputeDelta(stored.Fingerprint, currentResult);
            }
            catch (System.FormatException e)
            {
                return ToolDispatchResult.Fail("delta_error",
                    $"Delta computation failed: {e.Message}");
            }

            var hasNewErrors = delta.NewErrors > 0;
            return ToolDispatchResult.Ok(BuildResult(delta, hasNewErrors));
        }

        private static string BuildResult(DeltaData delta, bool hasNewErrors)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"passed\":").Append(!hasNewErrors ? "true" : "false");

            sb.Append(",\"summary\":{");
            sb.Append("\"newErrors\":").Append(delta.NewErrors);
            sb.Append(",\"newWarnings\":").Append(delta.NewWarnings);
            sb.Append(",\"resolvedErrors\":").Append(delta.ResolvedErrors);
            sb.Append(",\"resolvedWarnings\":").Append(delta.ResolvedWarnings);
            sb.Append('}');

            sb.Append(",\"newIssues\":[");
            if (delta.NewIssueKeys != null)
            {
                for (int i = 0; i < delta.NewIssueKeys.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Esc(delta.NewIssueKeys[i])).Append('"');
                }
            }
            sb.Append(']');

            sb.Append(",\"resolvedIssues\":[");
            if (delta.ResolvedIssueKeys != null)
            {
                for (int i = 0; i < delta.ResolvedIssueKeys.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Esc(delta.ResolvedIssueKeys[i])).Append('"');
                }
            }
            sb.Append(']');

            sb.Append('}');
            return sb.ToString();
        }

        private static string Esc(string s)
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
