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

        // v1 deny-list: tools that must NOT be invoked as nested batch steps for
        // reasons OTHER than lifecycle. Tools that force a domain reload or scene
        // switch are denied via the lifecycle-derived check in IsNestedReloadUnsafe
        // (so the deny-list stays accurate as new RestartThenSettle tools are
        // added without re-touching this list). This explicit set covers:
        //   - batch_execute itself (no nesting — would recurse).
        //   - compile_check (always headless spawn; cannot run live).
        // The local-only tools (capabilities, manage_tools, hub_*,
        // read_compile_errors, bridge_status, generate_skill, pull_events) are
        // rejected dynamically because they are NOT in the bridge's KnownTools
        // set — a nested step naming one of them dispatches to tool_not_found,
        // which is surfaced as a per-step error (matching the MCP contract's
        // `batch_tool_not_invokable`).
        private static readonly HashSet<string> DeniedNestedTools = new HashSet<string>
        {
            "unity_open_mcp_batch_execute",
            "unity_open_mcp_compile_check",
        };

        // M30-polish Plan 5 / T5.2 — a nested step that resolves to the
        // RestartThenSettle lifecycle would force a domain reload (package add /
        // remove / reimport, asmdef edit, build_set_defines/target, settings_set
        // player) or a scene switch (scene_open Single mode), silently aborting
        // every later step in the batch (the bridge's RestartThenSettle settle
        // wait can't bridge a mid-batch reload). Deriving the deny-list from the
        // lifecycle taxonomy catches the whole class as new RestartThenSettle
        // tools are added, instead of hardcoding each tool name. Returns the
        // resolved lifecycle so the caller can build a precise error message.
        //
        // scene_create is RestartThenSettle too, but its unsafety is MODE-
        // DEPENDENT: Single mode replaces the scene stack (unsafe mid-batch),
        // while additive mode preserves it (safe). It is therefore carved out
        // here and handled by the param-aware IsNestedSceneStackUnsafe check,
        // which runs FIRST (see the call site). scene_open has no additive-safe
        // case in the batch context and stays caught here. (B-N9.)
        private static bool IsNestedReloadUnsafe(string toolName, out LifecyclePolicy policy)
        {
            policy = ToolLifecycle.Resolve(toolName);
            if (policy != LifecyclePolicy.RestartThenSettle) return false;
            // scene_create's safety depends on its `mode` param; the param-aware
            // IsNestedSceneStackUnsafe check (run before this one at the call
            // site) decides, so additive scene_create is not lumped into the
            // unconditional reload-unsafe refusal here.
            if (toolName == "unity_open_mcp_scene_create") return false;
            return true;
        }

        // scene_create defaults to NewSceneMode.Single, which replaces the active
        // scene stack — a mid-batch Single create silently discards unsaved
        // changes in the currently-open scenes, the same disruption class T5.2
        // targets. scene_open IS ReloadUnsafe and is already caught by the
        // lifecycle check above; scene_create is carved out of that check so its
        // mode can be inspected here. Additive mode preserves the scene stack and
        // is allowed. (B-N9 — this check MUST run before IsNestedReloadUnsafe so
        // an additive scene_create is accepted instead of refused wholesale.)
        private const string SingleModeReason =
            "defaults to Single mode (or has mode:\"single\"), which replaces the " +
            "active scene stack and can discard unsaved changes in open scenes";

        private static bool IsNestedSceneStackUnsafe(string toolName, string paramsBody, out string reason)
        {
            reason = null;
            if (toolName != "unity_open_mcp_scene_create")
                return false;
            var mode = JsonBody.GetString(paramsBody, "mode");
            // Additive preserves the scene stack; anything else (absent default
            // or explicit "single") replaces it.
            if (!string.IsNullOrEmpty(mode) && mode.ToLowerInvariant() == "additive")
                return false;
            reason = SingleModeReason;
            return true;
        }

        // feedback-fable-31-07 §3 — a step that writes a .cs file (and so can
        // arm a compile on the next import). script_write is the canonical case;
        // its `path` param is a project-relative .cs path. We treat any
        // script_write step as a script-write for the combo check.
        private static bool IsScriptWriteStep(BatchStep step)
        {
            if (step.Tool != "unity_open_mcp_script_write") return false;
            var path = JsonBody.GetString(step.ParamsBody, "path");
            // Only a .cs write arms a compile; script_write rejects non-.cs
            // paths at its own layer, but check here so the combo refusal is
            // precise and does not fire for an unrelated script_write.
            return !string.IsNullOrEmpty(path) && path.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase);
        }

        // A step that forces an import of pending changes (and thus a compile
        // when a .cs write is pending). assets_refresh with
        // ForceSynchronousImport is the repro trigger; reimport_package and
        // reimport_asset on a scripts folder have the same effect.
        private static bool IsImportTriggerStep(BatchStep step)
        {
            return step.Tool == "unity_open_mcp_assets_refresh"
                || step.Tool == "unity_open_mcp_reimport_package"
                || step.Tool == "unity_open_mcp_reimport_asset";
        }

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

                // Deny-list check (nesting / headless-only). These are the tools
                // blocked for non-lifecycle reasons; RestartThenSettle tools are
                // blocked separately below with a lifecycle-specific error.
                if (DeniedNestedTools.Contains(tool))
                {
                    return ToolDispatchResult.Fail(
                        "batch_tool_not_invokable",
                        $"commands[{i}] tool '{tool}' is not invokable inside a batch " +
                        "(nesting / headless-only restriction). Use it as a " +
                        "single top-level call instead.");
                }

                // B-N9 — scene_create's safety depends on its `mode` param
                // (additive preserves the scene stack; Single replaces it). Run
                // this param-aware check BEFORE the lifecycle-derived reload
                // check: scene_create is RestartThenSettle, so the reload check
                // would otherwise refuse it wholesale even with mode:"additive",
                // contradicting the schema and making this branch dead code.
                // The reload check below carves scene_create out accordingly.
                if (IsNestedSceneStackUnsafe(tool, paramsBody, out var sceneReason))
                {
                    return ToolDispatchResult.Fail(
                        "batch_nested_reload_unsafe",
                        $"commands[{i}] tool '{tool}' is not invokable inside a batch: " +
                        $"it {sceneReason}. Pass mode:\"additive\" or use it as a " +
                        "single top-level call instead.");
                }

                // T5.2 — deny RestartThenSettle nested steps. A domain reload
                // or scene switch mid-batch silently aborts every later step
                // (the settle wait can't bridge a reload). Refuse up-front with
                // a clear error naming the offending step and why. scene_create
                // is handled by the param-aware check above and carved out of
                // IsNestedReloadUnsafe so an additive scene_create is accepted.
                if (IsNestedReloadUnsafe(tool, out var unsafePolicy))
                {
                    return ToolDispatchResult.Fail(
                        "batch_nested_reload_unsafe",
                        $"commands[{i}] tool '{tool}' has lifecycle " +
                        $"{unsafePolicy.ToWireString()} and is not invokable inside a batch: " +
                        "it may trigger a domain reload or scene switch that silently aborts " +
                        "the remaining steps. Use it as a single top-level call instead.");
                }

                steps.Add(new BatchStep { Tool = tool, ParamsBody = paramsBody });
            }

            // feedback-fable-31-07 §3 — detect a script-write followed (later in
            // the batch) by an import/refresh that will trigger a compile
            // mid-batch. The settle wait runs only ONCE at the batch level after
            // all steps complete; a compile kicked off by assets_refresh after a
            // script_write can kill the HTTP response mid-write (domain reload)
            // before the batch envelope is serialized, producing
            // bridge_response_unparsable. RestartThenSettle tools are already
            // refused above, but script_write + assets_refresh is the concrete
            // repro from the field report and is not caught by the lifecycle
            // taxonomy (script_write is None, assets_refresh is EditorSettle).
            // Refuse the combination up-front; the agent should run the script
            // write as a single top-level call, let it settle, then continue.
            var scriptWriteIndex = -1;
            for (int i = 0; i < steps.Count; i++)
            {
                if (IsScriptWriteStep(steps[i]))
                {
                    scriptWriteIndex = i;
                    break;
                }
            }
            if (scriptWriteIndex >= 0)
            {
                for (int j = scriptWriteIndex + 1; j < steps.Count; j++)
                {
                    if (IsImportTriggerStep(steps[j]))
                    {
                        return ToolDispatchResult.Fail(
                            "batch_nested_reload_unsafe",
                            $"commands[{scriptWriteIndex}] writes a script and commands[{j}] " +
                            $"('{steps[j].Tool}') would trigger a compile mid-batch, which can " +
                            "kill the HTTP response mid-write via a domain reload before the " +
                            "batch result is sent. Write the script as a single top-level call, " +
                            "let it settle, then run the remaining steps in a separate batch.");
                    }
                }
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
                // step must propagate to the ToolDispatchResult.Success flag so
                // the gate envelope's `mutation.success` and the gate runner's
                // partial-failure branch (BatchExecuteGateRunner) fire. The
                // per-step detail is always present in the output regardless.
                // Returning Ok(...) here would hardcode Success=true (see
                // ToolDispatchResult.Ok) and make the "one or more steps failed"
                // guidance dead code — code-review finding B1.
                //
                // B-N10 — when at least one step committed (successCount > 0)
                // the failure is PARTIAL: stamp PartialCommit = true via the
                // PartialFailure factory so GatePolicy still runs the post-
                // mutation validate/delta on the committed work (instead of
                // returning right after the mutation) and BridgeHttpServer still
                // waits for the asset/compile settle. A total failure
                // (successCount == 0) keeps the old shape — nothing committed,
                // so there is nothing to health-check.
                bool batchSuccess = failureCount == 0;
                string batchOutput = BuildBatchOutput(
                    batchSuccess, successCount, failureCount, failFast, results, sw.ElapsedMilliseconds);
                if (batchSuccess)
                {
                    return ToolDispatchResult.Ok(batchOutput);
                }
                if (successCount > 0)
                {
                    return ToolDispatchResult.PartialFailure(
                        "batch_partial_failure",
                        failureCount + " batch step(s) failed; inspect batch.results[] for per-step " +
                        "status and error detail. Successful steps before the failure are committed " +
                        "(v1 does not roll them back) — undo with a single editor_undo if needed.",
                        batchOutput);
                }
                return new ToolDispatchResult(
                    false, batchOutput, "batch_partial_failure",
                    failureCount + " batch step(s) failed; inspect batch.results[] for per-step " +
                    "status and error detail. Successful steps before the failure are committed " +
                    "(v1 does not roll them back) — undo with a single editor_undo if needed.");
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
