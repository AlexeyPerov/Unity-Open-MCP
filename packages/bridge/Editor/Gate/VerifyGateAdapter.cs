using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityAgentVerify;
using UnityAgentVerify.References;
using UnityEditor;

namespace UnityAgentBridge
{
    public static class VerifyGateAdapter
    {
        static readonly string[] FallbackRuleIds = { "missing_references" };

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
                        ruleSet.Add("missing_references");
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

        public static CheckpointFingerprint CreateCheckpoint(string[] paths, string[] ruleIds)
        {
            var scope = new VerifyScope(paths);
            var ids = ruleIds ?? SelectRuleIds(paths);
            return VerifyRunner.CreateCheckpoint(scope, ids);
        }

        public static VerifyResult ValidatePaths(string[] paths, string[] ruleIds)
        {
            var scope = new VerifyScope(paths);
            var ids = ruleIds ?? SelectRuleIds(paths);
            return VerifyRunner.RunScoped(scope, ids, VerifyRunMode.Validate);
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
}
