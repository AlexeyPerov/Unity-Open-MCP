using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.AsmdefAudit
{
    public class AsmdefReference
    {
        public AsmdefReference(string reference, int line, bool resolves)
        {
            Reference = reference;
            Line = line;
            Resolves = resolves;
        }

        /// <summary>The raw reference string as declared in the asmdef JSON
        /// (GUID form <c>GUID:...</c>, bare assembly name, or assembly-name).</summary>
        public string Reference { get; }

        public int Line { get; }

        /// <summary>True when the reference resolves to a compiled assembly or
        /// a known asmdef/asmref in the project (Unity compiles it).</summary>
        public bool Resolves { get; }
    }

    public class AsmdefData
    {
        public AsmdefData(string path)
        {
            Path = path;
        }

        public string Path { get; }

        /// <summary>Declared <c>name</c> field, or null when absent / blank.</summary>
        public string Name { get; set; }

        /// <summary>Root namespace declared on the asmdef, if any.</summary>
        public string RootNamespace { get; set; }

        public List<AsmdefReference> References { get; } = new List<AsmdefReference>();

        /// <summary>True when the JSON itself failed to parse.</summary>
        public bool ParseFailed { get; set; }

        public string ParseError { get; set; }
    }
}
