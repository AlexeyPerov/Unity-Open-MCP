using System.Collections.Generic;
using UnityEngine;

namespace UnityOpenMcpVerify.Rules.AnimationAnalysis
{
    public class AnimatorData
    {
        public AnimatorData(string path)
        {
            Path = path;
        }

        public string Path { get; }
        public string Name { get; set; }
        public int StateCount { get; set; }
        public int TransitionCount { get; set; }
        public int AnyStateTransitionCount { get; set; }
        public int LayerCount { get; set; }
        public List<string> MissingReferences { get; } = new List<string>();
        public List<string> UnreachableStates { get; } = new List<string>();
        public List<string> ParameterMismatches { get; } = new List<string>();
    }

    public class AnimationClipData
    {
        public AnimationClipData(string path)
        {
            Path = path;
        }

        public string Path { get; }
        public string Name { get; set; }
        public int CurveCount { get; set; }
        public int TotalKeyframes { get; set; }
        public float Duration { get; set; }
        public int KeyframeDensity { get; set; }
        public long FileSizeBytes { get; set; }
        public bool IsDuplicate { get; set; }
        public List<string> DuplicatePaths { get; } = new List<string>();
        public bool HasNoCurves { get; set; }
    }

    public struct AnimationAnalysisScanSettings
    {
        public bool DetectMissingClip;
        public bool DetectEmptyClip;
        public bool DetectUnreachableStates;
        public bool DetectComplexityOverThreshold;
        public bool DetectAnyStateOveruse;
        public bool DetectParameterMismatches;
        public bool DetectExpensiveCurves;
        public bool DetectDuplicateClips;
        public int StateMachineComplexityThreshold;
        public int AnyStateTransitionThreshold;
        public int CurveKeyframeDensityThreshold;
        public int CurveCountThreshold;
        public int MaxTransitionCount;

        public static AnimationAnalysisScanSettings Default()
        {
            return new AnimationAnalysisScanSettings
            {
                DetectMissingClip = true,
                DetectEmptyClip = true,
                DetectUnreachableStates = true,
                DetectComplexityOverThreshold = true,
                DetectAnyStateOveruse = true,
                DetectParameterMismatches = true,
                DetectExpensiveCurves = true,
                DetectDuplicateClips = true,
                StateMachineComplexityThreshold = 50,
                AnyStateTransitionThreshold = 5,
                CurveKeyframeDensityThreshold = 100,
                CurveCountThreshold = 20,
                MaxTransitionCount = 30,
            };
        }
    }
}
