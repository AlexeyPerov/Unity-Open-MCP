using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.AnimationAnalysis
{
    public static class IssueMapper
    {
        public const string CodeMissingClip = "missing_clip";
        public const string CodeEmptyClip = "empty_clip";

        public static void MapToIssues(List<AnimationData> assets, List<VerifyIssue> sink)
        {
            foreach (var asset in assets)
            {
                if (asset.IsController)
                {
                    var reportedGuids = new HashSet<string>();
                    foreach (var reference in asset.ClipReferences)
                    {
                        if (reference.Resolves) continue;
                        if (!reportedGuids.Add(reference.TargetGuid)) continue;

                        sink.Add(new VerifyIssue(
                            "animation_analysis",
                            VerifySeverity.Error,
                            asset.Path,
                            CodeMissingClip,
                            $"Animator state references missing animation clip (guid {reference.TargetGuid}, property '{reference.PropertyName}' at line {reference.Line})"));
                    }
                }
                else
                {
                    // .anim clip: flag only when it declares curve groups but
                    // every one is empty — that is a clip that animates nothing
                    // and was likely left as a stub or corrupted.
                    if (asset.HasNoCurves)
                    {
                        sink.Add(new VerifyIssue(
                            "animation_analysis",
                            VerifySeverity.Warning,
                            asset.Path,
                            CodeEmptyClip,
                            $"Animation clip declares no animation curves ({asset.KeyframeCount} keyframes) — it animates nothing"));
                    }
                }
            }
        }
    }
}
