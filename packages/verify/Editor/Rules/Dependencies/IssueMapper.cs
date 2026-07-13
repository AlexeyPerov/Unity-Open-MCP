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

                    sink.Add(MakeIssue(asset, CodeBrokenDependency + ":" + edge.TargetGuid,
                        $"Forward dependency on missing asset (guid {edge.TargetGuid}, {edge.Kind} at line {edge.Line}) does not resolve",
                        VerifySeverity.Error,
                        Evidence(("guid", edge.TargetGuid),
                            ("edgeKind", edge.Kind),
                            ("line", edge.Line.ToString()))));
                }

                foreach (var cycle in asset.CyclesThrough)
                {
                    var trail = string.Join(" -> ", cycle);
                    sink.Add(MakeIssue(asset, CodeDependencyCycle,
                        $"Forward dependency cycle: {trail}",
                        VerifySeverity.Warning,
                        Evidence(("cycle", trail))));
                }
            }
        }

        private static VerifyIssue MakeIssue(
            AssetDependencyData asset, string code, string description,
            VerifySeverity severity,
            IReadOnlyDictionary<string, string> evidence = null)
        {
            return new VerifyIssue("dependencies", severity, asset.Path, code, description, evidence);
        }

        private static IReadOnlyDictionary<string, string> Evidence(params (string, string)[] pairs)
        {
            var dict = new Dictionary<string, string>();
            foreach (var (k, v) in pairs)
            {
                if (!string.IsNullOrEmpty(k) && v != null)
                    dict[k] = v;
            }
            return dict;
        }
    }
}
