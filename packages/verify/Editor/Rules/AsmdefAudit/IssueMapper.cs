using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UnityOpenMcpVerify.Rules.AsmdefAudit
{
    public static class IssueMapper
    {
        // Verify-package additions (not in the source scanner).
        public const string CodeBrokenReference = "broken_asmdef_reference";
        public const string CodeMissingName = "asmdef_missing_name";
        public const string CodeMalformedAsmdef = "malformed_asmdef";

        // Ported from the source scanner.
        public const string CodeDuplicateName = "asmdef_duplicate_name";
        public const string CodeCircularReference = "asmdef_circular_reference";
        public const string CodeEditorInRuntime = "asmdef_editor_in_runtime";
        public const string CodeAutoReferencedOrphan = "asmdef_auto_referenced_orphan";
        public const string CodePlatformFilterBroad = "asmdef_platform_filter_broad";
        public const string CodePlatformFilterContradict = "asmdef_platform_filter_contradict";
        public const string CodeVersionDefineInvalid = "asmdef_version_define_invalid";

        public static void MapToIssues(List<AsmdefData> assets, AsmdefScanSettings settings, List<VerifyIssue> sink)
        {
            foreach (var asset in assets)
            {
                if (asset.ParseFailed)
                {
                    if (settings.CheckMalformed)
                    {
                        sink.Add(MakeIssue(asset, CodeMalformedAsmdef,
                            $"Assembly definition failed to parse: {asset.ParseError ?? "unknown error"}",
                            VerifySeverity.Error,
                            Evidence(("parseError", asset.ParseError ?? "unknown error"))));
                    }
                    continue;
                }

                if (settings.CheckMissingName && string.IsNullOrWhiteSpace(asset.Name))
                {
                    sink.Add(MakeIssue(asset, CodeMissingName,
                        "Assembly definition has no 'name' field — Unity cannot compile it.",
                        VerifySeverity.Error,
                        Evidence(("assemblyName", asset.Name))));
                }

                if (settings.CheckBrokenReferences)
                {
                    var reported = new HashSet<string>();
                    foreach (var r in asset.ResolvedReferences)
                    {
                        if (r.Resolves) continue;
                        if (!reported.Add(r.Reference)) continue;
                        sink.Add(MakeIssue(asset, CodeBrokenReference,
                            $"Assembly reference '{r.Reference}' does not resolve to a compiled assembly or known asmdef.",
                            VerifySeverity.Error,
                            Evidence(("reference", r.Reference),
                                ("line", r.Line.ToString()))));
                    }
                }

                if (settings.CheckEditorInRuntime)
                    CheckEditorInRuntime(asset, sink);
                if (settings.CheckPlatformFilterBroad)
                    CheckPlatformFilterBroad(asset, sink);
                if (settings.CheckPlatformFilterContradict)
                    CheckPlatformFilterContradict(asset, sink);
                if (settings.CheckVersionDefineInvalid)
                    CheckVersionDefineInvalid(asset, sink);
            }

            // Cross-asset detections (need the full scoped set).
            if (settings.CheckDuplicateName)
                CheckDuplicateName(assets, sink);
            if (settings.CheckCircularReferences)
                CheckCircularReferences(assets, sink);
            if (settings.CheckAutoReferencedOrphan)
                CheckAutoReferencedOrphan(assets, sink);
        }

        // -------------------------------------------------------------------
        // Per-asset detections (ported)
        // -------------------------------------------------------------------

        private static void CheckEditorInRuntime(AsmdefData data, List<VerifyIssue> sink)
        {
            if (data.IsEditorOnly) return;
            foreach (var reference in data.References)
            {
                if (reference.Contains("UnityEditor") || reference.Contains(".Editor"))
                {
                    if (data.IncludePlatforms.Contains("Editor")) continue;
                    sink.Add(MakeIssue(data, CodeEditorInRuntime,
                        $"Runtime assembly references editor assembly '{reference}' but is not editor-only.",
                        VerifySeverity.Warning,
                        Evidence(("reference", reference),
                            ("isEditorOnly", data.IsEditorOnly.ToString()))));
                }
            }
        }

        private static void CheckPlatformFilterBroad(AsmdefData data, List<VerifyIssue> sink)
        {
            if (data.IncludePlatforms.Count == 0 && data.ExcludePlatforms.Count == 0 && data.AnyPlatform)
            {
                sink.Add(MakeIssue(data, CodePlatformFilterBroad,
                    "Assembly compiles for all platforms (no platform filters).",
                    VerifySeverity.Warning,
                    Evidence(("anyPlatform", data.AnyPlatform.ToString()))));
            }
        }

        private static void CheckPlatformFilterContradict(AsmdefData data, List<VerifyIssue> sink)
        {
            if (data.IncludePlatforms.Count > 0 && data.ExcludePlatforms.Count > 0)
            {
                sink.Add(MakeIssue(data, CodePlatformFilterContradict,
                    $"Assembly has both includePlatforms ({string.Join(", ", data.IncludePlatforms)}) and excludePlatforms ({string.Join(", ", data.ExcludePlatforms)}).",
                    VerifySeverity.Warning,
                    Evidence(("includePlatforms", string.Join(", ", data.IncludePlatforms)),
                        ("excludePlatforms", string.Join(", ", data.ExcludePlatforms)))));
            }
        }

        private static void CheckVersionDefineInvalid(AsmdefData data, List<VerifyIssue> sink)
        {
            foreach (var vd in data.VersionDefines)
            {
                if (!string.IsNullOrEmpty(vd.Package) && vd.Package.StartsWith("com."))
                {
                    sink.Add(MakeIssue(data, CodeVersionDefineInvalid,
                        $"Version define references package '{vd.Package}' (symbol '{vd.Symbol}') — could silently fail.",
                        VerifySeverity.Warning,
                        Evidence(("package", vd.Package),
                            ("symbol", vd.Symbol),
                            ("expression", vd.Expression))));
                }
            }
        }

        // -------------------------------------------------------------------
        // Cross-asset detections (ported; need the full scoped set)
        // -------------------------------------------------------------------

        private static void CheckDuplicateName(List<AsmdefData> assets, List<VerifyIssue> sink)
        {
            var groups = assets
                .Where(a => !a.ParseFailed && !string.IsNullOrWhiteSpace(a.Name))
                .GroupBy(a => a.Name)
                .Where(g => g.Count() > 1);

            foreach (var group in groups)
            {
                var paths = string.Join(", ", group.Select(a => a.Path));
                // One issue per duplicate group (matches source behaviour).
                var first = group.First();
                sink.Add(MakeIssue(first, CodeDuplicateName,
                    $"Duplicate assembly name '{group.Key}' found in {group.Count()} files: {paths}.",
                    VerifySeverity.Error,
                    Evidence(("assemblyName", group.Key),
                        ("assetCount", group.Count().ToString()),
                        ("assetPaths", paths))));
            }
        }

        private static void CheckCircularReferences(List<AsmdefData> assets, List<VerifyIssue> sink)
        {
            var nameMap = assets
                .Where(a => !a.ParseFailed && !string.IsNullOrWhiteSpace(a.Name))
                .ToDictionary(a => a.Name, a => a);

            foreach (var start in assets.Where(a => !a.ParseFailed && !string.IsNullOrWhiteSpace(a.Name)))
            {
                var cycle = FindCycle(start, nameMap);
                if (cycle != null)
                {
                    sink.Add(MakeIssue(start, CodeCircularReference,
                        $"Circular reference detected: {cycle}.",
                        VerifySeverity.Error,
                        Evidence(("cycle", cycle))));
                }
            }
        }

        // Verbatim DFS port: shared visited set, strips literal "GUID:" prefix,
        // path.Count > 1 guard (self-reference alone is not a cycle).
        private static string FindCycle(AsmdefData start, Dictionary<string, AsmdefData> nameMap)
        {
            var visited = new HashSet<string>();
            var path = new List<string>();
            if (Dfs(start.Name, start.Name, nameMap, visited, path))
                return string.Join(" -> ", path) + " -> " + start.Name;
            return null;
        }

        private static bool Dfs(string current, string start, Dictionary<string, AsmdefData> nameMap,
            HashSet<string> visited, List<string> path)
        {
            if (visited.Contains(current)) return false;
            visited.Add(current);
            path.Add(current);

            if (!nameMap.TryGetValue(current, out var data))
            {
                path.RemoveAt(path.Count - 1);
                return false;
            }

            foreach (var reference in data.References)
            {
                var refName = reference.Replace("GUID:", "").Trim();
                if (refName == start && path.Count > 1)
                    return true;
                if (Dfs(refName, start, nameMap, visited, path))
                    return true;
            }

            path.RemoveAt(path.Count - 1);
            return false;
        }

        private static void CheckAutoReferencedOrphan(List<AsmdefData> assets, List<VerifyIssue> sink)
        {
            foreach (var data in assets.Where(a => !a.ParseFailed && !a.AutoReferenced))
            {
                var isReferenced = assets.Any(other =>
                    !other.ParseFailed &&
                    other.Name != data.Name &&
                    other.References.Contains(data.Name));
                if (!isReferenced)
                {
                    sink.Add(MakeIssue(data, CodeAutoReferencedOrphan,
                        "Assembly has autoReferenced=false but no other assembly references it.",
                        VerifySeverity.Warning,
                        Evidence(("assemblyName", data.Name),
                            ("autoReferenced", data.AutoReferenced.ToString()))));
                }
            }
        }

        private static VerifyIssue MakeIssue(
            AsmdefData asset, string code, string description,
            VerifySeverity severity,
            IReadOnlyDictionary<string, string> evidence = null)
        {
            return new VerifyIssue("asmdef_audit", severity, asset.Path, code, description, evidence);
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
