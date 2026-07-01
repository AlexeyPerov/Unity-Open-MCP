using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.Materials
{
    public class MaterialReference
    {
        public MaterialReference(string property, string targetGuid, int line, bool resolves)
        {
            Property = property;
            TargetGuid = targetGuid;
            Line = line;
            Resolves = resolves;
        }

        /// <summary>The material property holding the reference
        /// (e.g. <c>m_Shader</c>, <c>_MainTex</c>).</summary>
        public string Property { get; }

        public string TargetGuid { get; }
        public int Line { get; }
        public bool Resolves { get; }
    }

    public class MaterialData
    {
        public MaterialData(string path)
        {
            Path = path;
        }

        public string Path { get; }
        public List<MaterialReference> References { get; } = new List<MaterialReference>();
    }
}
