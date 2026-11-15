using System.Collections.Generic;

namespace UnityOpenMcpBridge.MetaTools
{
    // Preflight precedes checkpoint and undo setup. Read batches dispatch without
    // either; mutating batches share one scoped gate and an isolated undo group.
    // Partial mutations are retained and validated. Operation success and gate
    // outcome are independent; a failed step does not imply failed validation.
    public static class BatchExecuteGateRunner
    {
        public static GateDispatchResult Execute(
            string body, string gateMode, string[] pathsHint)
            => Execute(body, gateMode, pathsHint, null);

        // `plan` is the preflight output the HTTP handler already computed for
        // this request; null means "not preflighted yet" (direct/test callers).
        internal static GateDispatchResult Execute(
            string body, string gateMode, string[] pathsHint, BatchExecuteTool.BatchPlan plan)
        {
            if (plan == null)
            {
                var refusal = BatchExecuteTool.Preflight(body, out _, out plan);
                if (refusal != null) return GatePolicy.Skipped(refusal, "request_rejected");
            }
            if (!plan.IsMutating)
            {
                var read = BatchExecuteTool.Execute(body, plan);
                var skipped = GatePolicy.Skipped(read, read.Success ? "read_only" : "mutation_failed");
                skipped.EffectiveReadOnly = true;
                return skipped;
            }
            if (pathsHint == null || pathsHint.Length == 0)
                return GatePolicy.Skipped(ToolDispatchResult.Fail("paths_hint_required",
                    "A mixed/mutating batch requires the explicit union paths_hint before any step runs."), "request_rejected");
            var mode = GatePolicy.ParseMode(gateMode);

            // The whole batch is ONE undo step ("MCP batch_execute"): every
            // nested typed tool records into the group opened here, and the
            // group is collapsed regardless of outcome — a partial batch still
            // produced side effects the operator may want to undo as a unit.
            // Nested steps dispatch below the top-level path, so they open no
            // group of their own.
            var result = ToolUndoStep.Run("unity_open_mcp_batch_execute",
                // Reuse the exact gate path (checkpoint → batch → validate →
                // delta). BatchExecuteTool.Execute runs the nested dispatch
                // loop + BridgeBatchRunHistory progress + per-step collection.
                () => GatePolicy.Execute(mode, pathsHint, () => BatchExecuteTool.Execute(body, plan)));

            // Augment agentNextSteps with batch-specific guidance so the agent
            // knows how to interpret a partial result + where to look next.
            var steps = result.AgentNextSteps == null
                ? new List<string>()
                : new List<string>(result.AgentNextSteps);

            if (IsPreflightRefusal(result.Mutation.ErrorCode))
            {
                // specs/feedback.md 2026-08-24 — a pre-flight refusal happens
                // BEFORE the dispatch loop: there is no batch.results[], nothing
                // executed, and nothing was committed. Pointing the agent at
                // per-step results and at editor_undo (as the partial-failure
                // guidance below does) sends it looking for state that does not
                // exist. The refusal message itself carries the fix — including
                // the reachable-alternative hint for the tools that have one.
                steps.Add(
                    "The batch was REFUSED before any step ran — batch.results[] is empty, no " +
                    "mutation was committed, and there is nothing to undo. Read " +
                    "mutation.error.message: it names the offending commands[i] entry and what " +
                    "to do instead. Fix the batch and re-send it.");
            }
            else if (!result.Mutation.Success)
            {
                steps.Add(result.Mutation.PartialCommit
                    ? "Successful mutations remain committed; undo with a single editor_undo if needed."
                    : "No successful mutating step was committed; read-only successes do not require undo.");
                steps.Add(
                    "One or more batch steps failed. Inspect batch.results[] for the per-step " +
                    "status (success / failed / skipped) and error detail. Under fail_fast:true " +
                    "the batch stopped at the first failure; later entries are 'skipped' and " +
                    "were NOT executed.");
            }
            else if (result.GateRan && result.Delta != null && result.Delta.NewErrors > 0)
            {
                steps.Add(
                    "All batch steps succeeded, but the gate detected new errors after the run " +
                    "(gate.delta.newErrors > 0). Inspect gate.delta.newIssues and apply fixes " +
                    "via unity_open_mcp_apply_fix (dry_run first).");
            }

            if (steps.Count > 0)
            {
                result.AgentNextSteps = steps.ToArray();
            }

            return result;
        }

        // specs/feedback.md 2026-08-24 — the error codes BatchExecuteTool can
        // return from its pre-flight classification, before the dispatch loop
        // starts. Every one of them means "nothing ran": no per-step results, no
        // committed side effects. Kept next to the runner (not in the tool) so
        // the guidance branch and the refusal sites stay visibly paired; a new
        // pre-flight code must be added here in the same change.
        private static bool IsPreflightRefusal(string errorCode)
        {
            switch (errorCode)
            {
                case "missing_parameter":
                case "batch_invalid_step":
                case "batch_too_many_commands":
                case "batch_tool_not_invokable":
                case "batch_nested_reload_unsafe":
                case "batch_step_requires_server_poll":
                    return true;
                default:
                    return false;
            }
        }
    }
}
