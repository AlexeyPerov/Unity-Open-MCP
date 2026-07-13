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
    // The gate.delta still reports new issues introduced by the partial run,
    // and agentNextSteps points at fixes. Rollback-on-failure is v2.
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

            if (!result.Mutation.Success)
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
    }
}
