using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.AnimationAnalysis
{
    public static class IssueMapper
    {
        public const string CodeMissingClip = "missing_clip";
        public const string CodeEmptyClip = "empty_clip";
        public const string CodeUnreachableState = "unreachable_state";
        public const string CodeComplexityOverThreshold = "complexity_over_threshold";
        public const string CodeAnyStateOveruse = "anystate_overuse";
        public const string CodeParameterMismatch = "parameter_mismatch";
        public const string CodeExpensiveCurvesDensity = "expensive_curves_density";
        public const string CodeExpensiveCurvesCount = "expensive_curves_count";
        public const string CodeDuplicateClip = "duplicate_clip";

        public static void MapToIssues(
            List<AnimatorData> animators,
            List<AnimationClipData> clips,
            AnimationAnalysisScanSettings settings,
            List<VerifyIssue> sink)
        {
            foreach (var data in animators)
            {
                foreach (var missing in data.MissingReferences)
                {
                    sink.Add(new VerifyIssue("animation_analysis", VerifySeverity.Error,
                        data.Path, CodeMissingClip, missing));
                }

                foreach (var state in data.UnreachableStates)
                {
                    sink.Add(new VerifyIssue("animation_analysis", VerifySeverity.Warning,
                        data.Path, CodeUnreachableState,
                        $"State '{state}' is unreachable from any entry/default/any-state transition."));
                }

                if (settings.DetectComplexityOverThreshold && data.StateCount > settings.StateMachineComplexityThreshold)
                {
                    sink.Add(new VerifyIssue("animation_analysis", VerifySeverity.Warning,
                        data.Path, CodeComplexityOverThreshold,
                        $"State count {data.StateCount} exceeds threshold {settings.StateMachineComplexityThreshold}."));
                }

                if (settings.DetectAnyStateOveruse && data.AnyStateTransitionCount > settings.AnyStateTransitionThreshold)
                {
                    sink.Add(new VerifyIssue("animation_analysis", VerifySeverity.Warning,
                        data.Path, CodeAnyStateOveruse,
                        $"AnyState transition count {data.AnyStateTransitionCount} exceeds threshold {settings.AnyStateTransitionThreshold}."));
                }

                foreach (var mismatch in data.ParameterMismatches)
                {
                    sink.Add(new VerifyIssue("animation_analysis", VerifySeverity.Warning,
                        data.Path, CodeParameterMismatch, mismatch));
                }
            }

            foreach (var clip in clips)
            {
                if (clip.HasNoCurves)
                {
                    sink.Add(new VerifyIssue("animation_analysis", VerifySeverity.Warning,
                        clip.Path, CodeEmptyClip,
                        $"Animation clip '{clip.Name}' declares no animation curves ({clip.TotalKeyframes} keyframes) — it animates nothing."));
                }

                if (clip.KeyframeDensity > settings.CurveKeyframeDensityThreshold)
                {
                    sink.Add(new VerifyIssue("animation_analysis", VerifySeverity.Warning,
                        clip.Path, CodeExpensiveCurvesDensity,
                        $"Curve keyframe density {clip.KeyframeDensity}/s exceeds threshold {settings.CurveKeyframeDensityThreshold}."));
                }

                if (clip.CurveCount > settings.CurveCountThreshold)
                {
                    sink.Add(new VerifyIssue("animation_analysis", VerifySeverity.Warning,
                        clip.Path, CodeExpensiveCurvesCount,
                        $"Curve count {clip.CurveCount} exceeds threshold {settings.CurveCountThreshold}."));
                }

                if (clip.IsDuplicate && clip.DuplicatePaths.Count > 0)
                {
                    sink.Add(new VerifyIssue("animation_analysis", VerifySeverity.Warning,
                        clip.Path, CodeDuplicateClip,
                        $"Duplicate clip (byte-size match): also at {string.Join(", ", clip.DuplicatePaths)}."));
                }
            }
        }
    }
}
