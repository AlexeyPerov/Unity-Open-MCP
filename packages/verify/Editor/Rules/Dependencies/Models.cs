// Extracted from Unity-Scanner: Editor/Categories/Dependencies/DependenciesScanner.cs +
// Editor/UI/Window/FindReferencesWindow.cs (RefsMapBuilder). Forward-graph view of what
// each scoped asset depends on — complements the reverse ReferenceGraph (find_references)
// and the per-PPtr-field MissingReferencesRule.

using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.Dependencies
{
    public class DependencyEdge
    {
        public DependencyEdge(string sourcePath, string targetGuid, string targetPath, int line, string kind)
        {
            SourcePath = sourcePath;
            TargetGuid = targetGuid;
            TargetPath = targetPath;
            Line = line;
            Kind = kind;
        }

        public string SourcePath { get; }
        public string TargetGuid { get; }
        public string TargetPath { get; }
        public int Line { get; }

        /// <summary>"pptr" (fileID+guid) | "assetref" (m_AssetGUID) | "bare" (other guid:).</summary>
        public string Kind { get; }

        public bool Resolves => !string.IsNullOrEmpty(TargetPath);
    }

    public class AssetDependencyData
    {
        public AssetDependencyData(string path)
        {
            Path = path;
        }

        public string Path { get; }
        public List<DependencyEdge> DeclaredEdges { get; } = new List<DependencyEdge>();
        public List<string> ForwardDeps { get; } = new List<string>();
        public List<List<string>> CyclesThrough { get; } = new List<List<string>>();
    }
}
