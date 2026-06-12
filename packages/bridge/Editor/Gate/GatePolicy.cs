using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityAgentBridge
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
        public long ValidationDurationMs;
        public DeltaData Delta;
        public bool GateFailed;
        public string[] AgentNextSteps;
    }

    public static class GatePolicy
    {
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

            var checkpoint = VerifyGateAdapter.CreateCheckpoint(pathsHint, null);

            var mutationResult = mutation();

            if (!mutationResult.Success)
            {
                return new GateDispatchResult
                {
                    Mutation = mutationResult,
                    GateRan = true,
                    Outcome = GateOutcome.Failed,
                    CheckpointId = checkpoint.CheckpointId,
                    GateFailed = true,
                    AgentNextSteps = BuildNextSteps()
                };
            }

            VerifyResult validation;
            try
            {
                validation = VerifyGateAdapter.ValidatePaths(pathsHint, null);
            }
            catch (Exception e)
            {
                return new GateDispatchResult
                {
                    Mutation = mutationResult,
                    GateRan = true,
                    Outcome = GateOutcome.Failed,
                    CheckpointId = checkpoint.CheckpointId,
                    GateFailed = true,
                    AgentNextSteps = new[] { $"Validation scan exception: {e.Message}" }
                };
            }

            var delta = VerifyGateAdapter.ComputeDelta(checkpoint, validation);

            var (outcome, gateFailed) = ResolveOutcome(mode, delta);
            var nextSteps = GenerateAgentNextSteps(delta, outcome);

            return new GateDispatchResult
            {
                Mutation = mutationResult,
                GateRan = true,
                Outcome = outcome,
                CheckpointId = checkpoint.CheckpointId,
                CategoriesRun = validation.CategoriesRun,
                ValidationDurationMs = validation.DurationMs,
                Delta = delta,
                GateFailed = gateFailed,
                AgentNextSteps = nextSteps
            };
        }

        static (GateOutcome outcome, bool gateFailed) ResolveOutcome(GateMode mode, DeltaData delta)
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

        static string[] GenerateAgentNextSteps(DeltaData delta, GateOutcome outcome)
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
                        steps.Add($"Gate detected {delta.NewWarnings} new warning(s). Consider reviewing with unity_agent_validate_edit before proceeding.");
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
                    steps.Add($"Consider unity_agent_apply_fix with fix_id {fixId} (dry_run first)");

                steps.Add($"Use unity_agent_find_references for {parsed.Value.AssetPath} to assess downstream impact");
            }
            else
            {
                steps.Add("Review the affected asset and fix the introduced issue before retrying.");
            }

            if (isFailed)
                steps.Add("Fix the issue and retry; use unity_agent_validate_edit to verify without mutation.");
        }

        static string FormatIssue(IssueKeyParts? parsed, string rawKey)
        {
            if (parsed == null) return rawKey ?? "unknown";
            var p = parsed.Value;
            return $"{p.IssueCode} on {p.AssetPath}";
        }

        static bool TryFixIdForIssue(string categoryId, string issueCode, out string fixId)
        {
            if (categoryId == "missing_references" && issueCode == "MISSING_SCRIPT")
            {
                fixId = "remove_missing_script";
                return true;
            }

            fixId = null;
            return false;
        }

        static IssueKeyParts? ParseIssueKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var parts = key.Split('|');
            if (parts.Length < 4) return null;
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
