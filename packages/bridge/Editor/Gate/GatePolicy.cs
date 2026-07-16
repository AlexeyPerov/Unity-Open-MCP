using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Cache;
using UnityOpenMcpBridge.Console;

namespace UnityOpenMcpBridge
{
    public enum GateMode
    {
        Enforce,
        Warn,
        Off
    }

    public enum GateOutcome
    {
        Skipped,
        Passed,
        Failed,
        Warned,
        // M30-polish Plan 5 / T5.3 — the mutation committed successfully, but
        // the post-mutation validate scan threw an exception so no delta could
        // be computed. Distinct from Failed (where the delta ran and reported
        // new errors): here the mutation is honest-to-goodness committed, and
        // the agent should run validate_edit / scan_paths manually to confirm
        // health. The gate result carries Mutation (success) + AgentNextSteps
        // recommending the manual check; GateFailed stays true so the dispatch
        // is surfaced as a non-passing outcome (the operator chose enforce).
        ValidateScanFailed
    }

    // Wire token for the gate outcome. Emitted as the structured `gate.outcome`
    // field in the response envelope so an agent can distinguish
    // validate_scan_failed from a real failed delta without parsing prose in
    // agentNextSteps. Shared by BuildGateEnvelope and the gate-intelligence
    // narrative summary (single source of truth for the token mapping).
    public static class GateOutcomeExtensions
    {
        public static string ToWireString(this GateOutcome outcome)
        {
            switch (outcome)
            {
                case GateOutcome.Passed: return "passed";
                case GateOutcome.Warned: return "warned";
                case GateOutcome.Failed: return "failed";
                case GateOutcome.Skipped: return "skipped";
                case GateOutcome.ValidateScanFailed: return "validate_scan_failed";
                default: return outcome.ToString().ToLowerInvariant();
            }
        }
    }

    public class DeltaData
    {
        public int NewErrors;
        public int NewWarnings;
        public int ResolvedErrors;
        public int ResolvedWarnings;
        public string[] NewIssueKeys;
        public string[] ResolvedIssueKeys;
    }

    public class GateDispatchResult
    {
        public ToolDispatchResult Mutation;
        public bool GateRan;
        public GateOutcome Outcome;
        public string CheckpointId;
        public string[] CategoriesRun;
        public long CheckpointDurationMs;
        public long ValidationDurationMs;
        public long TotalGateDurationMs;
        public DeltaData Delta;
        public bool GateFailed;
        public string[] AgentNextSteps;
        // M13 T4.1 — milliseconds spent waiting for the editor to finish
        // compiling after the mutation. Surfaced in the response envelope as
        // settleMs so callers know whether the op blocked on a settle/reload.
        public long SettleMs;
        // M13 T4.2 — dirty-scene paths collected by SceneDirtyGuard when the
        // op was refused because of unsaved scene changes. Null when allowed.
        public string[] DirtyScenePaths;
        // M22 T22.1.3 — per-call `logs`: console warnings/errors emitted
        // *during this dispatch* (checkpoint + validate + mutate). Captured as a
        // before/after delta by LogEntriesReader around DispatchWithGate. Null
        // means "not captured" (older surface); an empty list means "captured,
        // nothing new". Emitted into the envelope as the `logs` array.
        public List<LogEntryInfo> Logs;
        // M25 Plan 2 — safe auto-fix rollback. Set by ApplyFixGateRunner when a
        // non-dry-run apply_fix either failed outright or, under enforce,
        // introduced new errors (delta.NewErrors > 0). The fix's touched files
        // are restored from the FixRollback snapshot and the envelope surfaces
        // a top-level `rollback` block. False/null on every non-apply_fix tool
        // and on apply_fix runs that did not need rolling back.
        public bool RolledBack;
        public string RollbackReason;
        public string[] RestoredPaths;
        // M30-polish Plan 5 / T5.1 — set by ApplyFixGateRunner when a non-dry-
        // run apply_fix ran under gate:"off". The operator explicitly asked for
        // no gate, so the FixRollback snapshot is never consulted and a fix that
        // corrupts the asset is permanent. The envelope surfaces a top-level
        // `rollbackDisabled` warning so the agent knows the mutation committed
        // without auto-rollback protection. False on every other path.
        public bool RollbackDisabled;
    }

    public static class GatePolicy
    {
        private const long GateBudgetMs = 2000;

        // Test seam for the validate-scan step (M30-polish review T5.3). The
        // call chain Execute -> VerifyGateAdapter.ValidatePaths ->
        // VerifyRunner.RunScoped is fully static with no injection point, and
        // RunScoped swallows per-rule exceptions, so a test cannot otherwise
        // reach the ValidateScanFailed catch block below. When non-null,
        // Execute routes the validate scan through this delegate instead of the
        // real adapter; production callers leave it null. InternalsVisibleTo
        // the test assembly is declared in OutputSerializer.cs.
        internal static Func<string[], string[], string, VerifyResult> ValidatePathsOverride;

        public static GateDispatchResult Execute(
            GateMode mode,
            string[] pathsHint,
            Func<ToolDispatchResult> mutation)
        {
            if (mode == GateMode.Off)
            {
                var offResult = mutation();
                return new GateDispatchResult
                {
                    Mutation = offResult,
                    GateRan = false,
                    Outcome = offResult.Success ? GateOutcome.Skipped : GateOutcome.Failed,
                    GateFailed = !offResult.Success
                };
            }

            if (pathsHint == null || pathsHint.Length == 0)
            {
                var noPathResult = mutation();
                return new GateDispatchResult
                {
                    Mutation = noPathResult,
                    GateRan = false,
                    Outcome = noPathResult.Success ? GateOutcome.Skipped : GateOutcome.Failed,
                    GateFailed = !noPathResult.Success
                };
            }

            var gateSw = Stopwatch.StartNew();

            CheckpointFingerprint checkpoint;
            long checkpointMs;
            try
            {
                var cpSw = Stopwatch.StartNew();
                checkpoint = VerifyGateAdapter.CreateCheckpoint(pathsHint, null);
                checkpointMs = cpSw.ElapsedMilliseconds;

                // Mirror the gate-run checkpoint into the in-memory store so the
                // bridge window Gate tab can surface recent history (M4.5-9).
                // Best-effort; storage failures must not break the gate path.
                try
                {
                    CheckpointStore.Store(new CheckpointStoreEntry
                    {
                        CheckpointId = checkpoint.CheckpointId,
                        Timestamp = DateTime.UtcNow.ToString("o"),
                        Label = null,
                        Paths = pathsHint,
                        Categories = null,
                        Fingerprint = checkpoint
                    });
                }
                catch
                {
                    // ignored — checkpoint history capture is non-essential
                }
            }
            catch (FormatException e)
            {
                UnityEngine.Debug.LogError($"[GatePolicy] Checkpoint key validation failed: {e.Message}");
                return new GateDispatchResult
                {
                    Mutation = ToolDispatchResult.Fail("checkpoint_validation", $"Checkpoint key validation failed: {e.Message}"),
                    GateRan = true,
                    Outcome = GateOutcome.Failed,
                    GateFailed = true,
                    AgentNextSteps = new[] { $"Checkpoint key validation failed: {e.Message}" }
                };
            }

            var mutationResult = mutation();

            if (!mutationResult.Success)
            {
                gateSw.Stop();
                return new GateDispatchResult
                {
                    Mutation = mutationResult,
                    GateRan = true,
                    Outcome = GateOutcome.Failed,
                    CheckpointId = checkpoint.CheckpointId,
                    CheckpointDurationMs = checkpointMs,
                    TotalGateDurationMs = gateSw.ElapsedMilliseconds,
                    GateFailed = true,
                    AgentNextSteps = BuildNextSteps()
                };
            }

            VerifyResult validation;
            try
            {
                // Honor the test seam when set (see ValidatePathsOverride above).
                validation = ValidatePathsOverride != null
                    ? ValidatePathsOverride(pathsHint, null, UnityOpenMcpVerify.Cache.VerifyCacheService.SourceGate)
                    : VerifyGateAdapter.ValidatePaths(pathsHint, null, UnityOpenMcpVerify.Cache.VerifyCacheService.SourceGate);
            }
            catch (Exception e)
            {
                // M30-polish Plan 5 / T5.3 — the mutation already committed
                // (line above). A validate-scan exception is NOT a real delta
                // failure: the mutation succeeded but the post-mutation health
                // check could not run. Surface a distinct outcome so the agent
                // knows the mutation is in place and can verify health manually,
                // instead of treating this as "delta said new errors" and
                // retrying the mutation. Do NOT roll back — the mutation
                // succeeded and rolling back would discard a good change based
                // on an unrelated scanner failure.
                gateSw.Stop();
                return new GateDispatchResult
                {
                    Mutation = mutationResult,
                    GateRan = true,
                    Outcome = GateOutcome.ValidateScanFailed,
                    CheckpointId = checkpoint.CheckpointId,
                    CheckpointDurationMs = checkpointMs,
                    TotalGateDurationMs = gateSw.ElapsedMilliseconds,
                    GateFailed = true,
                    AgentNextSteps = new[]
                    {
                        $"Mutation committed, but the gate's validate scan threw ({e.Message}). " +
                        "Run unity_open_mcp_validate_edit (or unity_open_mcp_scan_paths) on the " +
                        "touched paths to confirm health."
                    }
                };
            }

            DeltaData delta;
            try
            {
                delta = VerifyGateAdapter.ComputeDelta(checkpoint, validation);
            }
            catch (FormatException e)
            {
                gateSw.Stop();
                UnityEngine.Debug.LogError($"[GatePolicy] Delta key validation failed: {e.Message}");
                return new GateDispatchResult
                {
                    Mutation = mutationResult,
                    GateRan = true,
                    Outcome = GateOutcome.Failed,
                    CheckpointId = checkpoint.CheckpointId,
                    CheckpointDurationMs = checkpointMs,
                    ValidationDurationMs = validation.DurationMs,
                    TotalGateDurationMs = gateSw.ElapsedMilliseconds,
                    GateFailed = true,
                    AgentNextSteps = new[] { $"Delta key validation failed: {e.Message}" }
                };
            }

            gateSw.Stop();

            if (gateSw.ElapsedMilliseconds > GateBudgetMs)
            {
                UnityEngine.Debug.LogWarning(
                    $"[GatePolicy] Total gate path took {gateSw.ElapsedMilliseconds}ms " +
                    $"(budget: {GateBudgetMs}ms, checkpoint: {checkpointMs}ms, validate: {validation.DurationMs}ms) " +
                    $"for paths: {string.Join(", ", pathsHint)}");
            }

            var (outcome, gateFailed) = ResolveOutcome(mode, delta);
            var nextSteps = GenerateAgentNextSteps(delta, outcome);

            return new GateDispatchResult
            {
                Mutation = mutationResult,
                GateRan = true,
                Outcome = outcome,
                CheckpointId = checkpoint.CheckpointId,
                CategoriesRun = validation.CategoriesRun,
                CheckpointDurationMs = checkpointMs,
                ValidationDurationMs = validation.DurationMs,
                TotalGateDurationMs = gateSw.ElapsedMilliseconds,
                Delta = delta,
                GateFailed = gateFailed,
                AgentNextSteps = nextSteps
            };
        }

        internal static (GateOutcome outcome, bool gateFailed) ResolveOutcome(GateMode mode, DeltaData delta)
        {
            if (delta.NewErrors > 0)
            {
                return mode == GateMode.Enforce
                    ? (GateOutcome.Failed, true)
                    : (GateOutcome.Warned, false);
            }

            if (delta.NewWarnings > 0)
            {
                return mode == GateMode.Warn
                    ? (GateOutcome.Warned, false)
                    : (GateOutcome.Passed, false);
            }

            return (GateOutcome.Passed, false);
        }

        internal static string[] GenerateAgentNextSteps(DeltaData delta, GateOutcome outcome)
        {
            var steps = new List<string>();

            switch (outcome)
            {
                case GateOutcome.Failed:
                    AddIssueHints(steps, delta.NewIssueKeys, delta.NewErrors, delta.NewWarnings, isFailed: true);
                    break;
                case GateOutcome.Warned:
                    if (delta.NewErrors > 0)
                        AddIssueHints(steps, delta.NewIssueKeys, delta.NewErrors, delta.NewWarnings, isFailed: false);
                    else
                        steps.Add($"Gate detected {delta.NewWarnings} new warning(s). Consider reviewing with unity_open_mcp_validate_edit before proceeding.");
                    break;
                case GateOutcome.Passed:
                    if (delta.ResolvedErrors > 0)
                        steps.Add($"Gate passed — {delta.ResolvedErrors} previously reported error(s) resolved.");
                    else
                        steps.Add("Gate passed — no new issues detected.");
                    break;
            }

            return steps.ToArray();
        }

        private static void AddIssueHints(List<string> steps, string[] issueKeys, int newErrors, int newWarnings, bool isFailed)
        {
            var firstKey = issueKeys != null && issueKeys.Length > 0 ? issueKeys[0] : null;
            var parsed = ParseIssueKey(firstKey);

            var modeLabel = isFailed ? "" : " (warn mode)";
            steps.Add($"Gate detected {newErrors} new error(s){modeLabel}. First: {FormatIssue(parsed, firstKey)}");

            if (parsed != null)
            {
                if (TryFixIdForIssue(parsed.Value.CategoryId, parsed.Value.IssueCode, out var fixId))
                    steps.Add($"Consider unity_open_mcp_apply_fix with fix_id {fixId} (dry_run first)");

                steps.Add($"Use unity_open_mcp_find_references for {parsed.Value.AssetPath} to assess downstream impact");
            }
            else
            {
                steps.Add("Review the affected asset and fix the introduced issue before retrying.");
            }

            if (isFailed)
                steps.Add("Fix the issue and retry; use unity_open_mcp_validate_edit to verify without mutation.");
        }

        private static string FormatIssue(IssueKeyParts? parsed, string rawKey)
        {
            if (parsed == null) return rawKey ?? "unknown";
            var p = parsed.Value;
            return $"{p.IssueCode} on {p.AssetPath}";
        }

        private static bool TryFixIdForIssue(string categoryId, string issueCode, out string fixId)
        {
            // Issue codes are emitted in lowercase by the rule issue mappers, but
            // legacy delta keys (and older test fixtures) used UPPERCASE codes —
            // match case-insensitively so next-step guidance stays accurate
            // regardless of how the key was built.
            //
            // Some codes carry a GUID suffix (e.g. "missing_guid:<guid>") so the
            // fix provider can identify the exact broken reference. Strip it
            // before comparing against the bare code.
            var code = IssueKey.BareIssueCode(issueCode ?? "");

            if (categoryId == "missing_references" &&
                code.Equals("missing_script", System.StringComparison.OrdinalIgnoreCase))
            {
                fixId = "remove_missing_script";
                return true;
            }

            if ((categoryId == "missing_references" &&
                    code.Equals("missing_guid", System.StringComparison.OrdinalIgnoreCase)) ||
                (categoryId == "dependencies" &&
                    code.Equals("broken_dependency", System.StringComparison.OrdinalIgnoreCase)))
            {
                fixId = "relink_broken_guid";
                return true;
            }

            fixId = null;
            return false;
        }

        internal static IssueKeyParts? ParseIssueKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var parts = key.Split('|');
            // Match the canonical IssueKey.TryParse contract (IssueKey.cs): a key is
            // exactly four pipe-separated parts. Fewer is malformed; more means a stray
            // '|' leaked into one of the fields and the key must be rejected, not
            // silently truncated — otherwise the truncated key would masquerade as valid.
            if (parts.Length != 4) return null;
            return new IssueKeyParts(parts[0], parts[1], parts[2], parts[3]);
        }

        private static string[] BuildNextSteps()
        {
            return new[] { "Mutation failed before gate could validate. Fix the mutation error and retry." };
        }

        public static GateMode ParseMode(string mode)
        {
            return mode switch
            {
                "warn" => GateMode.Warn,
                "off" => GateMode.Off,
                _ => GateMode.Enforce
            };
        }
    }

    internal readonly struct IssueKeyParts
    {
        public readonly string CategoryId;
        public readonly string Severity;
        public readonly string AssetPath;
        public readonly string IssueCode;

        public IssueKeyParts(string categoryId, string severity, string assetPath, string issueCode)
        {
            CategoryId = categoryId;
            Severity = severity;
            AssetPath = assetPath;
            IssueCode = issueCode;
        }
    }

    public class FindReferencesResult
    {
        public string QueriedAssetPath;
        public string QueriedAssetGuid;
        public ReferencedByEntry[] ReferencedBy;
        public int TotalCount;
    }

    public class ReferencedByEntry
    {
        public string AssetPath;
        public string Guid;
    }
}
