using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Cache;

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
        Warned
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
    }

    public static class GatePolicy
    {
        const long GateBudgetMs = 2000;

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
                validation = VerifyGateAdapter.ValidatePaths(pathsHint, null, UnityOpenMcpVerify.Cache.VerifyCacheService.SourceGate);
            }
            catch (Exception e)
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
                    AgentNextSteps = new[] { $"Validation scan exception: {e.Message}" }
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

        static void AddIssueHints(List<string> steps, string[] issueKeys, int newErrors, int newWarnings, bool isFailed)
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

        static string FormatIssue(IssueKeyParts? parsed, string rawKey)
        {
            if (parsed == null) return rawKey ?? "unknown";
            var p = parsed.Value;
            return $"{p.IssueCode} on {p.AssetPath}";
        }

        static bool TryFixIdForIssue(string categoryId, string issueCode, out string fixId)
        {
            // Issue codes are emitted in lowercase by the rule issue mappers, but
            // legacy delta keys (and older test fixtures) used UPPERCASE codes —
            // match case-insensitively so next-step guidance stays accurate
            // regardless of how the key was built.
            var code = issueCode ?? "";

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

        static string[] BuildNextSteps()
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
