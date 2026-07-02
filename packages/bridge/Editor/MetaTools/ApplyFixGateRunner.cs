using System.IO;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Fixes;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.MetaTools
{
    // Orchestrates a non-dry-run apply_fix run WITH safe auto-fix rollback.
    //
    // The generic GatePolicy already does checkpoint → mutate → validate →
    // delta, and delta.NewErrors > 0 means the fix made things worse. But the
    // gate only FAILS the dispatch under enforce — it does not undo the fix.
    // This runner adds the undo step so the gate's trust contract holds: a fix
    // that introduces new errors (or throws) is rolled back to its pre-fix
    // state, and the envelope reports `rolledBack` + the restored paths.
    //
    // Rollback scope = the issue's asset path + its companion .meta (predicted
    // before the fix runs and snapshotted via FixRollback). That covers every
    // current fix: remove_missing_script (.prefab/.unity), relink_broken_guid
    // (asset YAML), remove_orphan_meta (.meta delete), fix_duplicate_guid
    // (.meta rewrite), and the two materials fixes (.mat rewrite).
    //
    // High-confidence threshold for rollback = delta.NewErrors > 0 (matches the
    // gate's own failure condition). New WARNINGS do not trigger rollback —
    // they are informational, and several fixes legitimately change a project
    // in ways that surface new warnings (e.g. a reassigned shader may now flag
    // a render-queue override).
    public static class ApplyFixGateRunner
    {
        public static GateDispatchResult Execute(
            string body, string gateMode, string[] pathsHint)
        {
            var mode = GatePolicy.ParseMode(gateMode);
            var issueId = JsonBody.GetString(body, "issue_id");

            // Predict the files the fix may touch and snapshot them BEFORE the
            // gate runs the mutation. predictedPaths is null when the issue id
            // can't be parsed (no snapshot ⇒ no rollback; the gate still runs).
            var predictedPaths = PredictTouchedPaths(issueId);
            var rollback = new FixRollback();
            if (predictedPaths != null && predictedPaths.Length > 0)
                rollback.Snapshot(predictedPaths);

            GateDispatchResult result;
            try
            {
                // Reuse the exact gate path (checkpoint → apply → validate →
                // delta). ApplyFixTool.Execute runs the provider's Apply.
                result = GatePolicy.Execute(mode, pathsHint,
                    () => ApplyFixTool.Execute(body));
            }
            catch
            {
                // The fix threw an exception (the gate wraps the mutation in a
                // try/catch internally, so this only fires for checkpoint
                // failures). Roll back to be safe, then rethrow so the bridge
                // builds the fault envelope.
                if (rollback.HasSnapshot)
                    rollback.Restore();
                rollback.Discard();
                throw;
            }

            // Decide whether to roll back. Two triggers:
            //   1. The mutation itself failed (provider returned !Success or
            //      threw before the validate step — GatePolicy marks these
            //      Outcome=Failed with GateFailed=true but no delta).
            //   2. Under ENFORCE, the gate detected new errors after the fix.
            //      (Warn mode never rolls back — the operator asked for
            //      report-only; Off mode has no delta to check.)
            bool mutationFailed = result.Mutation != null && !result.Mutation.Success;
            bool gateIntroducedErrors = mode == GateMode.Enforce
                && result.GateRan
                && result.Delta != null
                && result.Delta.NewErrors > 0;

            if ((mutationFailed || gateIntroducedErrors) && rollback.HasSnapshot)
            {
                var restore = rollback.Restore();
                AssetDatabase.Refresh();

                result.RolledBack = true;
                result.RollbackReason = mutationFailed
                    ? "fix failed to apply — restored touched files to pre-fix state"
                    : $"fix introduced {result.Delta.NewErrors} new error(s) under enforce — restored touched files to pre-fix state";
                result.RestoredPaths = restore.RestoredPaths;

                // Augment agent guidance so the next step is clear.
                var steps = result.AgentNextSteps == null
                    ? new System.Collections.Generic.List<string>()
                    : new System.Collections.Generic.List<string>(result.AgentNextSteps);
                steps.Add("The fix was rolled back — no project change remains. Inspect the issue manually before retrying.");
                result.AgentNextSteps = steps.ToArray();
            }

            rollback.Discard();
            return result;
        }

        // Predict the absolute paths a fix may touch from the issue id. The
        // issue's asset path is Assets/-relative; we resolve it against the
        // project root and also include the companion .meta (every asset has
        // one, and several fixes rewrite/delete the .meta). For remove_orphan_
        // meta the issue path IS the .meta, so the .meta-only entry is the
        // primary and the (non-existent) companion is naturally skipped by
        // FixRollback (File.Exists == false ⇒ ExistedBefore=false ⇒ create-
        // case rollback deletes it if the fix recreated the asset).
        private static string[] PredictTouchedPaths(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _))
                return null;
            if (string.IsNullOrEmpty(assetPath)) return null;

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot)) return null;

            var paths = new System.Collections.Generic.List<string>(2);
            AddIfRelevant(paths, projectRoot, assetPath);

            // Companion .meta. If assetPath already ends in .meta (orphan
            // case), don't double-add; otherwise append .meta.
            if (!assetPath.EndsWith(".meta", System.StringComparison.OrdinalIgnoreCase))
                AddIfRelevant(paths, projectRoot, assetPath + ".meta");

            return paths.Count == 0 ? null : paths.ToArray();
        }

        private static void AddIfRelevant(System.Collections.Generic.List<string> sink, string projectRoot, string assetRelativePath)
        {
            var abs = Path.GetFullPath(Path.Combine(projectRoot, assetRelativePath));
            if (!sink.Contains(abs))
                sink.Add(abs);
        }
    }
}
