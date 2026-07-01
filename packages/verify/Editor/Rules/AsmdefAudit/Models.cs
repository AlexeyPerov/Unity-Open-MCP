using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.AsmdefAudit
{
    public class VersionDefineData
    {
        public string Package { get; set; }
        public string Expression { get; set; }
        public string Symbol { get; set; }
    }

    public class AsmdefReference
    {
        public AsmdefReference(string reference, int line, bool resolves)
        {
            Reference = reference;
            Line = line;
            Resolves = resolves;
        }

        /// <summary>The raw reference string as declared in the asmdef JSON
        /// (GUID form <c>GUID:...</c> or a bare assembly name).</summary>
        public string Reference { get; }

        public int Line { get; }

        /// <summary>True when the reference resolves to a compiled assembly or
        /// a known asmdef/asmref in the project. Only populated for the
        /// broken-reference check (the source scanner does not resolve refs;
        /// it only walks name-based cycles/orphans).</summary>
        public bool Resolves { get; }
    }

    public class AsmdefData
    {
        public AsmdefData(string path)
        {
            Path = path;
        }

        public string Path { get; }

        /// <summary>Declared <c>name</c> field, or empty when absent.</summary>
        public string Name { get; set; } = "";

        public string RootNamespace { get; set; }

        public List<string> References { get; } = new List<string>();

        public List<string> IncludePlatforms { get; } = new List<string>();

        public List<string> ExcludePlatforms { get; } = new List<string>();

        /// <summary>Parsed <c>autoReferenced</c> (defaults true when absent).</summary>
        public bool AutoReferenced { get; set; } = true;

        /// <summary>Parsed <c>anyPlatform</c> (defaults true when absent).</summary>
        public bool AnyPlatform { get; set; } = true;

        public List<VersionDefineData> VersionDefines { get; } = new List<VersionDefineData>();

        /// <summary>References that were resolved against the compiled-assembly
        /// index (only populated when the broken-reference check runs).</summary>
        public List<AsmdefReference> ResolvedReferences { get; } = new List<AsmdefReference>();

        /// <summary>Derived: true when the assembly is editor-only. See
        /// <see cref="Scanner.DeriveIsEditorOnly"/>.</summary>
        public bool IsEditorOnly { get; set; }

        /// <summary>True when the JSON itself failed to parse.</summary>
        public bool ParseFailed { get; set; }

        public string ParseError { get; set; }
    }

    public struct AsmdefScanSettings
    {
        public bool CheckBrokenReferences;
        public bool CheckMissingName;
        public bool CheckMalformed;
        public bool CheckDuplicateName;
        public bool CheckCircularReferences;
        public bool CheckEditorInRuntime;
        public bool CheckAutoReferencedOrphan;
        public bool CheckPlatformFilterBroad;
        public bool CheckPlatformFilterContradict;
        public bool CheckVersionDefineInvalid;

        public static AsmdefScanSettings Default()
        {
            return new AsmdefScanSettings
            {
                CheckBrokenReferences = true,
                CheckMissingName = true,
                CheckMalformed = true,
                CheckDuplicateName = true,
                CheckCircularReferences = true,
                CheckEditorInRuntime = true,
                CheckAutoReferencedOrphan = true,
                CheckPlatformFilterBroad = true,
                CheckPlatformFilterContradict = true,
                CheckVersionDefineInvalid = true,
            };
        }
    }
}
