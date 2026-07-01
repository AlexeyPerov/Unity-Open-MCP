using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.AnimationAnalysis
{
    public class AnimationClipReference
    {
        public AnimationClipReference(string propertyName, string targetGuid, int line, bool resolves)
        {
            PropertyName = propertyName;
            TargetGuid = targetGuid;
            Line = line;
            Resolves = resolves;
        }

        /// <summary>Field on the controller state holding the motion ref
        /// (typically <c>m_Motion</c>).</summary>
        public string PropertyName { get; }

        public string TargetGuid { get; }
        public int Line { get; }
        public bool Resolves { get; }
    }

    public class AnimationData
    {
        public AnimationData(string path, bool isController)
        {
            Path = path;
            IsController = isController;
        }

        public string Path { get; }

        /// <summary>True for <c>.controller</c>, false for <c>.anim</c>.</summary>
        public bool IsController { get; }

        /// <summary>Motion / clip references on an animator controller.</summary>
        public List<AnimationClipReference> ClipReferences { get; } = new List<AnimationClipReference>();

        /// <summary>For <c>.anim</c> clips: true when the clip declares no
        /// curves at all (rotation/position/scale/euler/float all empty).</summary>
        public bool HasNoCurves { get; set; }

        /// <summary>Total number of curve keyframes across all curve groups.</summary>
        public int KeyframeCount { get; set; }
    }
}
