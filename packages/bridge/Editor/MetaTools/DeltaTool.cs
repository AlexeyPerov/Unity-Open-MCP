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
            {
                // Item F — a missing checkpoint is NOT a tool failure: checkpoints
                // are session-scoped (in-memory) and are wiped on script recompile,
                // domain reload, or editor restart. Returning a hard error would set
                // isError:true on the MCP response and block agent workflows. Instead
                // return success with an explicit `unavailable` warning + recovery
                // guidance so the agent can proceed (e.g. fall back to validate_edit).
                return ToolDispatchResult.Ok(BuildUnavailableResult(checkpointId));
            }

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

        // Item F — payload returned when the requested checkpoint is no longer in
        // the session-scoped store. `passed:true` + `unavailable:true` lets the
        // agent treat this as "no new errors detected, but I have no baseline to
        // delta against" rather than a hard failure.
        private static string BuildUnavailableResult(string checkpointId)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"passed\":true");
            sb.Append(",\"unavailable\":true");
            sb.Append(",\"warning\":\"Checkpoint '")
              .Append(Esc(checkpointId))
              .Append("' is no longer available. Checkpoints are session-scoped (in-memory) and are cleared on script recompile, domain reload, or editor restart — this does not indicate a problem with the project.\"");
            sb.Append(",\"agentNextSteps\":[");
            sb.Append("\"The pre-change baseline is gone, so a delta cannot be computed.\",");
            sb.Append("\"To verify current state directly, call unity_open_mcp_validate_edit (or unity_open_mcp_scan_paths) on the relevant paths.\",");
            sb.Append("\"For future delta checks, call unity_open_mcp_checkpoint_create immediately before mutating, then unity_open_mcp_delta right after.\"");
            sb.Append("]}");
            return sb.ToString();
        }

        // Single source of truth for JSON string-content escaping is BridgeJson
        // (T30.5). Returns escaped CONTENT (no surrounding quotes), matching the
        // call sites here; preserves the `null ⇒ ""` contract.
        private static string Esc(string s) => BridgeJson.EscapeStringContent(s);
    }
}
