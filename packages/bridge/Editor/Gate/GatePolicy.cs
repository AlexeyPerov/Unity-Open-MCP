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
                    steps.Add($"Gate detected {delta.NewErrors} new error(s). First: {delta.NewIssueKeys.FirstOrDefault() ?? "unknown"}");
                    steps.Add("Review the affected asset and fix the introduced issue before retrying.");
                    break;
                case GateOutcome.Warned:
                    if (delta.NewErrors > 0)
                    {
                        steps.Add($"Gate detected {delta.NewErrors} new error(s) (warn mode). First: {delta.NewIssueKeys.FirstOrDefault() ?? "unknown"}");
                        steps.Add("Consider fixing before committing.");
                    }
                    else
                    {
                        steps.Add($"Gate detected {delta.NewWarnings} new warning(s). Consider reviewing before proceeding.");
                    }
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

        static string[] BuildNextSteps()
        {
            return new[] { "Gate passed — no new issues detected." };
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
