using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace UnityOpenMcpBridge.MetaTools
{
    // M27 Plan 4 — live `batch_execute`.
    //
    // One HTTP round trip runs many typed tools sequentially inside the
    // already-open Editor. NOT the headless batch spawn fallback — this tool
    // is `batchCapable: false` (not in BATCH_TOOL_NAMES); it requires the live
    // bridge to be up. The MCP server never falls back to spawning Unity.
    //
    // This class owns:
    //   1. Parsing + validating the `commands` array (count limit, tool
    //      allow/deny-list, schema presence).
    //   2. Running each command via the SAME dispatch path as a single tool
    //      (`BridgeHttpServer.DispatchTool`) — no business-logic duplication.
    //   3. Per-step result collection + fail_fast abort + skipped tail.
    //   4. BridgeBatchRunHistory live progress (BeginRun / AddEntry /
    //      SetEntryStatus / CompleteRun).
    //
    // The batch-level GATE (one checkpoint → N steps → one validate/delta) and
    // the undo grouping live in `BatchExecuteGateRunner` (the gate-runner
    // analogue of `ApplyFixGateRunner`), which wraps this Execute() in a
    // GatePolicy.Execute cycle. Execute() itself is just the "mutation lambda"
    // from the gate's perspective — it runs the steps and returns a single
    // ToolDispatchResult carrying the per-step results envelope.
    public static class BatchExecuteTool
    {
        // Default cap on nested commands. Overridable via
        // BridgeProjectSettings.BatchExecuteMaxCommands (clamped 1–100). Coplay
        // parity: 25 default, 100 hard max.
        public const int DefaultMaxCommands = 25;
        public const int HardMaxCommands = 100;

        // v1 deny-list: tools that must NOT be invoked as nested batch steps.
        //   - batch_execute itself (no nesting — would recurse).
        //   - compile_check (always headless spawn; cannot run live).
        //   - The three meta-tools that can run arbitrary code or hit any menu
        //     (execute_csharp / invoke_method / execute_menu). Agents use batch
        //     for typed tools; the power tools stay single-call so the deny
        //     heuristic + bypass contract stays uniform. Tracked as a HashSet
        //     for O(1) membership; the local-only tools (capabilities,
        //     manage_tools, hub_*, read_compile_errors, bridge_status,
        //     generate_skill, pull_events) are rejected dynamically below
        //     because they are NOT in the bridge's KnownTools set — a nested
        //     step naming one of them dispatches to tool_not_found, which is
        //     surfaced as a per-step error (matching the MCP contract's
        //     `batch_tool_not_invokable`).
        private static readonly HashSet<string> DeniedNestedTools = new HashSet<string>
        {
            "unity_open_mcp_batch_execute",
            "unity_open_mcp_compile_check",
            "unity_open_mcp_execute_csharp",
            "unity_open_mcp_invoke_method",
            "unity_open_mcp_execute_menu",
        };

        public static ToolDispatchResult Execute(string body)
        {
            var sw = Stopwatch.StartNew();

            // --- Parse + validate the commands array -------------------------
            var commandsRaw = JsonBody.GetObjectArray(body, "commands");
            if (commandsRaw == null || commandsRaw.Length == 0)
            {
                return ToolDispatchResult.Fail(
                    "missing_parameter",
                    "'commands' is required and must be a non-empty array of { tool, params } entries.");
            }

            int maxCommands = BridgeProjectSettings.BatchExecuteMaxCommands;
            if (maxCommands < 1) maxCommands = 1;
            if (maxCommands > HardMaxCommands) maxCommands = HardMaxCommands;

            if (commandsRaw.Length > maxCommands)
            {
                return ToolDispatchResult.Fail(
                    "batch_too_many_commands",
                    $"Batch has {commandsRaw.Length} commands; the limit is {maxCommands} " +
                    $"(configurable via .unity-open-mcp/settings.json 'batchExecuteMaxCommands', " +
                    $"hard max {HardMaxCommands}). Split the batch or raise the limit.");
            }

            bool failFast = JsonBody.GetBool(body, "fail_fast", true);

            // Pre-parse every step (tool + params) so a malformed entry fails
            // the WHOLE batch before any side effect — a partial run caused by
            // a mid-loop parse error would be worse than a clean rejection.
            var steps = new List<BatchStep>(commandsRaw.Length);
            for (int i = 0; i < commandsRaw.Length; i++)
            {
                var raw = commandsRaw[i];
                var tool = JsonBody.GetString(raw, "tool");
                if (string.IsNullOrWhiteSpace(tool))
                {
                    return ToolDispatchResult.Fail(
                        "batch_invalid_step",
                        $"commands[{i}] is missing a 'tool' name.");
                }

                var paramsRaw = JsonBody.GetRawValue(raw, "params");
                // params may be absent for no-arg tools — pass "{}" so the
                // nested handler sees a valid (empty) object body.
                var paramsBody = string.IsNullOrWhiteSpace(paramsRaw) || paramsRaw.Trim() == "null"
                    ? "{}"
                    : paramsRaw;

                // Deny-list check (nesting / headless-only / power tools).
                if (DeniedNestedTools.Contains(tool))
                {
                    return ToolDispatchResult.Fail(
                        "batch_tool_not_invokable",
                        $"commands[{i}] tool '{tool}' is not invokable inside a batch " +
                        "(nesting / headless-only / power-tool restriction). Use it as a " +
                        "single top-level call instead.");
                }

                steps.Add(new BatchStep { Tool = tool, ParamsBody = paramsBody });
            }

            // --- BridgeBatchRunHistory live progress -------------------------
            // One BeginRun / CompleteRun pair around the whole loop so the
            // operator's Activity Batch section shows in-flight progress without
            // a manual refresh. Source "mcp" distinguishes agent-driven batches
            // from any future Hub-initiated runs.
            var runId = System.Guid.NewGuid().ToString("N");
            var label = BuildRunLabel(steps);
            BridgeBatchRunHistory.BeginRun(runId, "mcp", label);

            try
            {
                // --- Sequential dispatch loop --------------------------------
                var results = new List<BatchStepResult>(steps.Count);
                int successCount = 0;
                int failureCount = 0;
                bool aborted = false;

                for (int i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];

                    // If a prior step failed under fail_fast, mark the rest
                    // skipped (NOT executed) — matches the MCP contract.
                    if (aborted)
                    {
                        BridgeBatchRunHistory.AddEntry(step.Tool, SummarizeArgs(step));
                        BridgeBatchRunHistory.SetEntryStatus(i, BridgeBatchEntryStatus.Skipped);
                        results.Add(new BatchStepResult
                        {
                            Index = i,
                            Tool = step.Tool,
                            Status = "skipped",
                        });
                        continue;
                    }

                    var entry = BridgeBatchRunHistory.AddEntry(step.Tool, SummarizeArgs(step));
                    BridgeBatchRunHistory.SetEntryStatus(i, BridgeBatchEntryStatus.Running);

                    var stepSw = Stopwatch.StartNew();
                    ToolDispatchResult stepResult;
                    try
                    {
                        // Reuse the EXACT per-tool dispatch path. This runs
                        // the typed/meta-tool handler with the step's params;
                        // paths_hint/gate are NOT re-enforced per step (the
                        // batch owns one gate cycle for the whole sequence —
                        // see BatchExecuteGateRunner).
                        stepResult = BridgeHttpServer.DispatchTool(step.Tool, step.ParamsBody);
                    }
                    catch (System.Exception e)
                    {
                        // A thrown handler is treated as a step failure (the
                        // outer gate runner catches checkpoint/validate throws;
                        // this only fires for a handler that threw inside
                        // DispatchTool, which the switch does not wrap).
                        stepResult = ToolDispatchResult.Fail("execution_error", e.Message);
                    }
                    stepSw.Stop();

                    if (stepResult.Success)
                    {
                        successCount++;
                        BridgeBatchRunHistory.SetEntryStatus(i, BridgeBatchEntryStatus.Done, stepSw.ElapsedMilliseconds);
                        results.Add(new BatchStepResult
                        {
                            Index = i,
                            Tool = step.Tool,
                            Status = "success",
                            Output = stepResult.Output,
                        });
                    }
                    else
                    {
                        failureCount++;
                        BridgeBatchRunHistory.SetEntryStatus(
                            i, BridgeBatchEntryStatus.Failed, stepSw.ElapsedMilliseconds,
                            stepResult.ErrorCode, stepResult.ErrorMessage);
                        results.Add(new BatchStepResult
                        {
                            Index = i,
                            Tool = step.Tool,
                            Status = "failed",
                            ErrorCode = stepResult.ErrorCode,
                            ErrorMessage = stepResult.ErrorMessage,
                        });
                        if (failFast)
                        {
                            aborted = true;
                        }
                    }
                }

                sw.Stop();

                // Batch-level success = every step succeeded. A failed/skipped
                // step makes the batch "success:false" so the gate runner +
                // envelope reflect the partial state, but per-step detail is
                // always present in the output.
                bool batchSuccess = failureCount == 0;
                return ToolDispatchResult.Ok(BuildBatchOutput(
                    batchSuccess, successCount, failureCount, failFast, results, sw.ElapsedMilliseconds));
            }
            finally
            {
                BridgeBatchRunHistory.CompleteRun(runId);
            }
        }

        // Build the redacted run label from the step tool names (no full params
        // dump — keeps the Activity Batch section readable and avoids leaking payloads).
        private static string BuildRunLabel(List<BatchStep> steps)
        {
            if (steps.Count == 0) return "batch_execute";
            var sb = new StringBuilder(64);
            sb.Append("batch (").Append(steps.Count).Append("): ");
            for (int i = 0; i < steps.Count && i < 3; i++)
            {
                if (i > 0) sb.Append(", ");
                // Short tool suffix (drop the unity_open_mcp_ prefix).
                var t = steps[i].Tool;
                const string prefix = "unity_open_mcp_";
                sb.Append(t.StartsWith(prefix) ? t.Substring(prefix.Length) : t);
            }
            if (steps.Count > 3) sb.Append(", …");
            return sb.ToString();
        }

        // Per-entry args summary for the Activity Batch section. Redacted to the tool name +
        // a short hint of the first key id-like param — never the full params
        // body (could be large / sensitive).
        private static string SummarizeArgs(BatchStep step)
        {
            var name = JsonBody.GetString(step.ParamsBody, "name");
            var assetPath = JsonBody.GetString(step.ParamsBody, "asset_path");
            var path = JsonBody.GetString(step.ParamsBody, "path");
            if (!string.IsNullOrWhiteSpace(name)) return "name=" + Truncate(name, 60);
            if (!string.IsNullOrWhiteSpace(assetPath)) return "asset_path=" + Truncate(assetPath, 60);
            if (!string.IsNullOrWhiteSpace(path)) return "path=" + Truncate(path, 60);
            return null;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // Compose the batch output JSON. Mirrors the response shape documented
        // in the MCP tool contract (execution-plan-4 T27.4.1).
        //
        //   {
        //     "batch": {
        //       "success": true,
        //       "callSuccessCount": 3,
        //       "callFailureCount": 0,
        //       "failFast": true,
        //       "results": [
        //         { "index": 0, "tool": "...", "status": "success", "output": {…} },
        //         { "index": 1, "tool": "...", "status": "failed", "error": { "code":…, "message":… } },
        //         { "index": 2, "tool": "...", "status": "skipped" }
        //       ]
        //     }
        //   }
        private static string BuildBatchOutput(
            bool success, int successCount, int failureCount, bool failFast,
            List<BatchStepResult> results, long durationMs)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"batch\":{");
            sb.Append("\"success\":").Append(success ? "true" : "false");
            sb.Append(",\"callSuccessCount\":").Append(successCount);
            sb.Append(",\"callFailureCount\":").Append(failureCount);
            sb.Append(",\"failFast\":").Append(failFast ? "true" : "false");
            sb.Append(",\"durationMs\":").Append(durationMs);
            sb.Append(",\"results\":[");
            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) sb.Append(',');
                results[i].WriteJson(sb);
            }
            sb.Append("]}}");
            return sb.ToString();
        }

        private struct BatchStep
        {
            public string Tool;
            public string ParamsBody;
        }

        private struct BatchStepResult
        {
            public int Index;
            public string Tool;
            public string Status; // "success" | "failed" | "skipped"
            public string Output; // raw JSON object (success only)
            public string ErrorCode;
            public string ErrorMessage;

            public void WriteJson(StringBuilder sb)
            {
                sb.Append("{\"index\":").Append(Index);
                sb.Append(",\"tool\":\"");
                BridgeJson.EscapeStringContentTo(sb, Tool ?? "");
                sb.Append("\",\"status\":\"").Append(Status).Append("\"");
                if (Output != null)
                {
                    sb.Append(",\"output\":").Append(Output);
                }
                if (ErrorCode != null)
                {
                    sb.Append(",\"error\":{\"code\":\"");
                    BridgeJson.EscapeStringContentTo(sb, ErrorCode);
                    sb.Append("\",\"message\":\"");
                    BridgeJson.EscapeStringContentTo(sb, ErrorMessage ?? "");
                    sb.Append("\"}");
                }
                sb.Append('}');
            }
        }
    }
}
