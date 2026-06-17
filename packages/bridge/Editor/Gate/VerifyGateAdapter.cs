using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Cache;
using UnityOpenMcpVerify.References;
using UnityEditor;

namespace UnityOpenMcpBridge
{
    public static class VerifyGateAdapter
    {
        static readonly string[] FallbackRuleIds = { "missing_references", "dependencies" };

        public static string[] SelectRuleIds(string[] paths)
        {
            if (paths == null || paths.Length == 0)
                return FallbackRuleIds;

            var ruleSet = new HashSet<string>();
            var hasKnownExtension = false;

            foreach (var path in paths)
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                switch (ext)
                {
                    case ".prefab":
                    case ".unity":
                        ruleSet.Add("missing_references");
                        ruleSet.Add("scene_prefab_health");
                        ruleSet.Add("dependencies");
                        hasKnownExtension = true;
                        break;
                    case ".cs":
                    case ".asmdef":
                        ruleSet.Add("missing_references");
                        ruleSet.Add("asmdef_audit");
                        hasKnownExtension = true;
                        break;
                    case ".mat":
                    case ".shader":
                    case ".shadergraph":
                        ruleSet.Add("missing_references");
                        ruleSet.Add("dependencies");
                        ruleSet.Add("materials");
                        ruleSet.Add("shader_analysis");
                        hasKnownExtension = true;
                        break;
                    case ".png":
                    case ".jpg":
                    case ".jpeg":
                    case ".tga":
                        ruleSet.Add("textures");
                        ruleSet.Add("sprite_2d_analysis");
                        hasKnownExtension = true;
                        break;
                    case ".controller":
                    case ".anim":
                        ruleSet.Add("animation_analysis");
                        ruleSet.Add("dependencies");
                        ruleSet.Add("missing_references");
                        hasKnownExtension = true;
                        break;
                    case ".asset":
                        ruleSet.Add("missing_references");
                        ruleSet.Add("dependencies");
                        hasKnownExtension = true;
                        break;
                    case ".wav":
                    case ".mp3":
                    case ".ogg":
                        ruleSet.Add("audio_analysis");
                        hasKnownExtension = true;
                        break;
                }
            }

            if (!hasKnownExtension || ruleSet.Count == 0)
                return FallbackRuleIds;

            return ruleSet.ToArray();
        }

        // Rule selection: explicit request -> auto-select -> fallback. Then
        // apply includeRules (intersection / additive allow-list) and
        // excludeRules (deny-list). includeRules is additive to the explicit
        // list only when the caller provided no explicit `ruleIds`; otherwise
        // the explicit list is the source of truth and includeRules narrows it.
        // excludeRules always wins.
        //
        // Returns null when filters reduce the set to nothing — that null is a
        // sentinel the tools check to short-circuit with an empty result rather
        // than falling into VerifyRunner's "null ruleIds = run all" branch.
        // Null/empty include and exclude arrays are no-ops so callers that
        // don't care about filtering see the historical behaviour.
        public static string[] ResolveRuleIds(
            string[] paths,
            string[] ruleIds,
            string[] includeRules,
            string[] excludeRules)
        {
            var requested = ruleIds != null && ruleIds.Length > 0
                ? new HashSet<string>(ruleIds)
                : new HashSet<string>(SelectRuleIds(paths));

            if (includeRules != null && includeRules.Length > 0)
            {
                var include = new HashSet<string>(includeRules);
                if (ruleIds != null && ruleIds.Length > 0)
                {
                    // Explicit list + includeRules: keep only rules that appear
                    // in BOTH (narrowing). This lets an agent pin the gate to a
                    // subset without re-listing every auto-selected rule.
                    requested.IntersectWith(include);
                }
                else
                {
                    // No explicit list: includeRules is an additive allow-list
                    // on top of the auto-selected set.
                    requested.UnionWith(include);
                }
            }

            if (excludeRules != null && excludeRules.Length > 0)
            {
                foreach (var id in excludeRules)
                    requested.Remove(id);
            }

            // Sentinel: empty after filter — distinct from "caller asked for
            // everything" (null). Tools check this to avoid running all rules.
            return requested.Count == 0 ? null : requested.ToArray();
        }

        public static CheckpointFingerprint CreateCheckpoint(string[] paths, string[] ruleIds)
        {
            var scope = new VerifyScope(paths);
            var ids = ruleIds ?? SelectRuleIds(paths);
            return VerifyRunner.CreateCheckpoint(scope, ids);
        }

        public static VerifyResult ValidatePaths(string[] paths, string[] ruleIds, string source = null)
        {
            var scope = new VerifyScope(paths);
            var ids = ruleIds ?? SelectRuleIds(paths);
            var result = VerifyRunner.RunScoped(scope, ids, VerifyRunMode.Validate);
            VerifyCacheService.Record(result, source ?? VerifyCacheService.SourceValidateEdit);
            return result;
        }

        // Validate with include/exclude filters. Returns the result plus the
        // effective rule set used (after filtering) so the tool envelope can
        // surface `rulesApplied` to the agent.
        public static FilteredVerifyResult ValidateFiltered(
            string[] paths,
            string[] ruleIds,
            string[] includeRules,
            string[] excludeRules,
            string source = null)
        {
            var effective = ResolveRuleIds(paths, ruleIds, includeRules, excludeRules);
            if (effective == null)
            {
                // Filters narrowed the set to nothing — return an explicit
                // empty result rather than running every registered rule.
                return new FilteredVerifyResult
                {
                    Result = new VerifyResult(new List<VerifyIssue>(), new string[0], 0),
                    RulesApplied = new string[0],
                };
            }
            var scope = new VerifyScope(paths);
            var result = VerifyRunner.RunScoped(scope, effective, VerifyRunMode.Validate);
            VerifyCacheService.Record(result, source ?? VerifyCacheService.SourceValidateEdit);
            return new FilteredVerifyResult { Result = result, RulesApplied = effective };
        }

        public static VerifyResult ScanPaths(string[] paths, string[] ruleIds)
        {
            var scope = new VerifyScope(paths);
            var ids = ruleIds ?? SelectRuleIds(paths);
            var result = VerifyRunner.RunScoped(scope, ids, VerifyRunMode.Full);
            VerifyCacheService.Record(result, VerifyCacheService.SourceScanPaths);
            return result;
        }

        public static FilteredVerifyResult ScanFiltered(
            string[] paths,
            string[] ruleIds,
            string[] includeRules,
            string[] excludeRules)
        {
            var effective = ResolveRuleIds(paths, ruleIds, includeRules, excludeRules);
            if (effective == null)
            {
                return new FilteredVerifyResult
                {
                    Result = new VerifyResult(new List<VerifyIssue>(), new string[0], 0),
                    RulesApplied = new string[0],
                };
            }
            var scope = new VerifyScope(paths);
            var result = VerifyRunner.RunScoped(scope, effective, VerifyRunMode.Full);
            VerifyCacheService.Record(result, VerifyCacheService.SourceScanPaths);
            return new FilteredVerifyResult { Result = result, RulesApplied = effective };
        }

        public static DeltaData ComputeDelta(CheckpointFingerprint before, VerifyResult after)
        {
            var beforeKeys = new HashSet<string>();
            foreach (var fp in before.Fingerprints.Values)
            {
                foreach (var key in fp.IssueKeys)
                {
                    IssueKey.ValidateKey(key);
                    beforeKeys.Add(key);
                }
            }

            var afterKeys = new HashSet<string>();
            foreach (var issue in after.Issues)
            {
                var key = IssueKey.Build(issue);
                afterKeys.Add(key);
            }

            var newKeys = new HashSet<string>(afterKeys);
            newKeys.ExceptWith(beforeKeys);

            var resolvedKeys = new HashSet<string>(beforeKeys);
            resolvedKeys.ExceptWith(afterKeys);

            var newErrors = 0;
            var newWarnings = 0;
            foreach (var issue in after.Issues)
            {
                var key = IssueKey.Build(issue);
                if (newKeys.Contains(key))
                {
                    if (issue.Severity == VerifySeverity.Error) newErrors++;
                    else newWarnings++;
                }
            }

            var resolvedErrors = 0;
            var resolvedWarnings = 0;
            foreach (var key in resolvedKeys)
            {
                var parts = key.Split('|');
                if (parts.Length >= 2)
                {
                    if (parts[1] == "ERROR") resolvedErrors++;
                    else if (parts[1] == "WARN") resolvedWarnings++;
                }
            }

            return new DeltaData
            {
                NewErrors = newErrors,
                NewWarnings = newWarnings,
                ResolvedErrors = resolvedErrors,
                ResolvedWarnings = resolvedWarnings,
                NewIssueKeys = newKeys.ToArray(),
                ResolvedIssueKeys = resolvedKeys.ToArray()
            };
        }

        public static FindReferencesResult FindReferences(string assetPathOrGuid, int maxResults = 100)
        {
            var graph = ReferenceGraph.Find(assetPathOrGuid);

            var allPaths = graph.ReferencedByPaths;
            var totalCount = allPaths.Count;
            var truncated = allPaths;
            if (maxResults > 0 && totalCount > maxResults)
                truncated = allPaths.GetRange(0, maxResults);

            var entries = new List<ReferencedByEntry>(truncated.Count);
            foreach (var path in truncated)
            {
                entries.Add(new ReferencedByEntry
                {
                    AssetPath = path,
                    Guid = AssetDatabase.AssetPathToGUID(path)
                });
            }

            return new FindReferencesResult
            {
                QueriedAssetPath = graph.QueriedAssetPath,
                QueriedAssetGuid = graph.QueriedAssetGuid,
                ReferencedBy = entries.ToArray(),
                TotalCount = totalCount
            };
        }
    }

    // Bundles a VerifyResult with the effective rule set after include/exclude
    // filtering. The tools surface `rulesApplied` so agents can see which rules
    // actually ran when they combine auto-select with include/exclude filters.
    public struct FilteredVerifyResult
    {
        public VerifyResult Result;
        public string[] RulesApplied;
    }
}
