// Extracted from Unity-Scanner: Editor/Categories/Dependencies/DependenciesIssueMapper.cs.
// Forward-graph issues. Distinct from missing_references (per-PPtr field view) and
// find_references (reverse-only): broken_dependency catches forward asset-graph edges
// that do not resolve, dependency_cycle catches self-referential forward cycles.

using System.Collections.Generic;
using System.Linq;

namespace UnityOpenMcpVerify.Rules.Dependencies
{
    public static class IssueMapper
    {
        public const string CodeBrokenDependency = "broken_dependency";
        public const string CodeDependencyCycle = "dependency_cycle";

        public static void MapToIssues(List<AssetDependencyData> assets, List<VerifyIssue> sink)
        {
            foreach (var asset in assets)
            {
                // One issue per distinct unresolved target GUID — dedupe across edge kinds
                // (pptr/assetref) so a single broken GUID isn't reported twice.
                var reportedGuids = new HashSet<string>();
                foreach (var edge in asset.DeclaredEdges)
                {
                    if (edge.Resolves) continue;
                    if (!reportedGuids.Add(edge.TargetGuid)) continue;

                    sink.Add(MakeIssue(asset, CodeBrokenDependency,
                        $"Forward dependency on missing asset (guid {edge.TargetGuid}, {edge.Kind} at line {edge.Line}) does not resolve",
                        VerifySeverity.Error));
                }

                foreach (var cycle in asset.CyclesThrough)
                {
                    var trail = string.Join(" -> ", cycle);
                    sink.Add(MakeIssue(asset, CodeDependencyCycle,
                        $"Forward dependency cycle: {trail}",
                        VerifySeverity.Warning));
                }
            }
        }

        static VerifyIssue MakeIssue(
            AssetDependencyData asset, string code, string description,
            VerifySeverity severity)
        {
            return new VerifyIssue("dependencies", severity, asset.Path, code, description);
        }
    }
}
