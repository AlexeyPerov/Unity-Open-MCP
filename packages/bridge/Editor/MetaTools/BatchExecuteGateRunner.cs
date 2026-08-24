using System.Collections.Generic;
using UnityEditor;

namespace UnityOpenMcpBridge.MetaTools
{
    // M27 Plan 4 — gate runner for `batch_execute`. Mirrors `ApplyFixGateRunner`:
    // it wraps the whole batch in ONE GatePolicy.Execute cycle so the sequence
    // gets a single checkpoint → N nested dispatches → one validate/delta,
    // instead of N independent gate cycles (which would be both slower and
    // semantically wrong — the batch is one logical mutation from the agent's
    // perspective).
    //
    // On top of the gate, it adds a single UNDO GROUP for the whole batch:
    //   Undo.GetCurrentGroup() before the loop captures the group index;
    //   Undo.SetCurrentGroupName("Open MCP Batch") labels it;
    //   Undo.CollapseUndoOperations(group) at the end folds every per-step
    //   Undo.RegisterCreatedObjectUndo / RecordObject into ONE undo step so
    //   a single Ctrl+Z reverts the entire batch (UCP competitive note §E;
    //   strictly better than Coplay's per-step undo).
    //
    // v1 does NOT roll back successful steps when a later step fails (same
    // partial-failure semantics as Coplay, documented in the tool contract).
    // A partial batch propagates `Mutation.Success = false` (code-review fix B1)
    // AND stamps `PartialCommit = true` when at least one step committed (B-N10),
    // so GatePolicy.Execute STILL runs the post-mutation validate/delta on the
    // committed work (the committed steps must be health-checked) while marking
    // the run Failed so the agent reads batch.results[]. The partial-failure
    // branch below adds the recovery guidance (inspect batch.results[], undo
    // with editor_undo). A total failure (no step committed) short-circuits the
    // validate/delta step as before. Rollback-on-failure is v2.
    public static class BatchExecuteGateRunner
    {
        public static GateDispatchResult Execute(
            string body, string gateMode, string[] pathsHint)
        {
            var mode = GatePolicy.ParseMode(gateMode);

            // Mark the undo group boundary BEFORE the batch runs. Every nested
            // typed tool calls Undo.RegisterCreatedObjectUndo / RecordObject /
            // AddComponent inside this window; CollapseUndoOperations at the end
            // folds them into a single undo step labelled "Open MCP Batch".
            int groupBefore = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Open MCP Batch");

            GateDispatchResult result;
            try
            {
                // Reuse the exact gate path (checkpoint → batch → validate →
                // delta). BatchExecuteTool.Execute runs the nested dispatch
                // loop + BridgeBatchRunHistory progress + per-step collection.
                result = GatePolicy.Execute(mode, pathsHint,
                    () => BatchExecuteTool.Execute(body));
            }
            finally
            {
                // Collapse the per-step undos into ONE undo step regardless of
                // outcome — a partial batch still produced side effects the
                // operator may want to undo as a unit. GetCurrentGroup() may
                // have advanced past groupBefore if nested tools incremented
                // it; CollapseUndoOperations(g) folds everything ABOVE g down
                // to g, so we pass groupBefore (the boundary captured before
                // the batch) — passing groupAfter would collapse nothing.
                try
                {
                    int groupAfter = Undo.GetCurrentGroup();
                    if (groupAfter > groupBefore)
                    {
                        Undo.CollapseUndoOperations(groupBefore);
                    }
                    // Stamp the final group name so the Editor's Edit > Undo
                    // menu reads "Undo Open MCP Batch" (not a generic label).
                    Undo.SetCurrentGroupName("Open MCP Batch");
                }
                catch
                {
                    // Undo collapse is best-effort; never let it mask the
                    // actual dispatch result / fault.
                }
            }

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
                steps.Add(
                    "One or more batch steps failed. Inspect batch.results[] for the per-step " +
                    "status (success / failed / skipped) and error detail. Under fail_fast:true " +
                    "the batch stopped at the first failure; later entries are 'skipped' and " +
                    "were NOT executed. Successful steps before the failure are committed (v1 " +
                    "does not roll them back) — undo with a single editor_undo if needed.");
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
