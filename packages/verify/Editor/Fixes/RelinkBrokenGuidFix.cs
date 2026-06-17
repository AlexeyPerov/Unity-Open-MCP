// T2.4 fix provider — relink broken PPtr / forward-dependency edges.
//
// Targets the `missing_guid` issue code emitted by the `missing_references`
// rule and the `broken_dependency` code emitted by the `dependencies` rule
// (both surface "an external GUID that does not resolve to a loadable asset").
//
// A broken GUID is rarely fixed deterministically: the agent usually has to
// pick the intended target out of several candidates. The provider therefore
// exposes a candidate-finding Describe path (the dry_run preview surfaces
// them) and an Apply path that takes a chosen replacement GUID. Replacement
// is text-level YAML editing of `guid: <old>` -> `guid: <new>` followed by a
// re-import — `Safe: false` because it mutates references and a wrong choice
// silently rewires an asset graph.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpVerify.Internals.RegexPatterns;
using UnityOpenMcpVerify.Internals.Serialization;

namespace UnityOpenMcpVerify.Fixes
{
    public class RelinkBrokenGuidFix : IFixProvider
    {
        public const string TargetGuidHintKey = "target_guid";

        public string FixId => "relink_broken_guid";

        public bool CanFix(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var issueCode))
                return false;

            var code = issueCode ?? "";
            return (ruleId == "missing_references" && code == "missing_guid")
                || (ruleId == "dependencies" && code == "broken_dependency");
        }

        public FixDescription Describe(string issueId)
        {
            IssueKey.TryParse(issueId, out var ruleId, out _, out var assetPath, out var issueCode);
            var brokenGuid = ExtractBrokenGuidFromIssue(issueId);

            var candidates = brokenGuid != null
                ? FindCandidateAssets(brokenGuid, assetPath)
                : new List<GuidCandidate>(0);

            var desc = candidates.Count > 0
                ? $"Relink broken GUID reference in '{assetPath}'. "
                  + $"{candidates.Count} candidate target(s) found by name/type. "
                  + "Provide one via apply_fix with target_guid (the chosen replacement GUID) to apply."
                : $"Relink broken GUID reference in '{assetPath}'. No automatic candidates found — "
                  + "use unity_open_mcp_find_references to identify the intended target before applying.";

            return new FixDescription
            {
                FixId = FixId,
                IssueId = issueId,
                AssetPath = assetPath,
                Description = desc,
                // Mutates references and a wrong choice silently rewires the
                // asset graph — never auto-apply under enforce.
                Safe = false,
            };
        }

        public FixResult Apply(string issueId)
        {
            // Apply without a chosen target is a no-op for this provider —
            // relinking requires picking one of the candidates. Callers go
            // through ApplyFixTool, which passes the chosen target_guid in.
            return Apply(issueId, targetGuid: null);
        }

        public FixResult Apply(string issueId, string targetGuid)
        {
            if (!IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _))
                return new FixResult
                {
                    Success = false,
                    Description = $"Cannot parse issue id: {issueId}",
                    TouchedPaths = null
                };

            if (string.IsNullOrEmpty(assetPath))
                return new FixResult
                {
                    Success = false,
                    Description = "Issue id contains empty asset path.",
                    TouchedPaths = null
                };

            var brokenGuid = ExtractBrokenGuidFromIssue(issueId);
            if (string.IsNullOrEmpty(brokenGuid))
                return new FixResult
                {
                    Success = false,
                    Description = $"Could not determine the broken GUID to replace from issue id '{issueId}'.",
                    TouchedPaths = null
                };

            if (string.IsNullOrEmpty(targetGuid))
            {
                var candidates = FindCandidateAssets(brokenGuid, assetPath);
                return new FixResult
                {
                    Success = false,
                    Description = candidates.Count > 0
                        ? $"relink_broken_guid requires a chosen target_guid. Candidates: {FormatCandidates(candidates)}"
                        : "relink_broken_guid requires a chosen target_guid. No automatic candidates found — use unity_open_mcp_find_references to identify the intended target.",
                    TouchedPaths = null
                };
            }

            if (!SharedRegex.Guid32Hex.IsMatch(targetGuid))
                return new FixResult
                {
                    Success = false,
                    Description = $"target_guid '{targetGuid}' is not a valid 32-hex Unity GUID.",
                    TouchedPaths = null
                };

            // Sanity: the chosen target must resolve to a loadable asset.
            var targetPath = AssetDatabase.GUIDToAssetPath(targetGuid);
            if (string.IsNullOrEmpty(targetPath))
                return new FixResult
                {
                    Success = false,
                    Description = $"target_guid '{targetGuid}' does not resolve to any asset in the project.",
                    TouchedPaths = null
                };

            return RewriteGuid(assetPath, brokenGuid, targetGuid);
        }

        // -------------------------------------------------------------------
        // Broken-GUID extraction
        // -------------------------------------------------------------------
        //
        // The canonical issue id carries only {ruleId|severity|assetPath|issueCode}.
        // We re-scan the asset to find the broken GUIDs it currently references —
        // this keeps Apply honest (it operates on the file as it is now, not as it
        // was when the issue was emitted) and lets the provider be invoked with a
        // bare issue id.

        static string ExtractBrokenGuidFromIssue(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _))
                return null;

            if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath))
                return null;

            var lines = YamlUtilities.TryReadAllLines(assetPath);
            foreach (var line in lines)
            {
                var match = SharedRegex.ExternalFileAndGuid.Match(line);
                if (!match.Success) continue;
                var guid = match.Groups[2].Value;
                if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                    continue; // resolves fine — not the broken one
                return guid;
            }

            return null;
        }

        // -------------------------------------------------------------------
        // Candidate discovery — name + type heuristics
        // -------------------------------------------------------------------

        static List<GuidCandidate> FindCandidateAssets(string brokenGuid, string referencingAssetPath)
        {
            // Heuristic 1: the broken GUID may have been re-imported under a new
            // GUID but the same asset name. We cannot read the name from a GUID
            // that does not resolve, so the strongest signal we have is the
            // field name on the referencing line — but we don't carry it in the
            // issue id. Fall back to type inference from the referencing asset
            // extension (e.g. a Material referencing a missing texture).
            //
            // Practical candidate sources:
            //   * assets whose name matches a token derived from the broken GUID
            //     (last 8 hex chars are often unique enough to spot a typo).
            //   * recently added assets of plausible types.

            var candidates = new List<GuidCandidate>();
            var token = brokenGuid.Length >= 8 ? brokenGuid.Substring(brokenGuid.Length - 8) : brokenGuid;

            // Search asset paths for the token — catches copy/paste / truncation
            // mistakes where only part of the GUID was changed.
            var hits = AssetDatabase.FindAssets(token);
            var seen = new HashSet<string>();
            foreach (var hitGuid in hits)
            {
                if (hitGuid == brokenGuid) continue;
                var path = AssetDatabase.GUIDToAssetPath(hitGuid);
                if (string.IsNullOrEmpty(path) || path.StartsWith("Packages/")) continue;
                if (!seen.Add(path)) continue;
                candidates.Add(new GuidCandidate { Guid = hitGuid, AssetPath = path });
                if (candidates.Count >= 8) break;
            }

            return candidates;
        }

        static string FormatCandidates(List<GuidCandidate> candidates)
        {
            var parts = candidates
                .Take(8)
                .Select(c => $"{c.Guid} ({c.AssetPath})");
            return string.Join(", ", parts);
        }

        // -------------------------------------------------------------------
        // Apply — rewrite the broken GUID in the asset YAML and re-import
        // -------------------------------------------------------------------

        static FixResult RewriteGuid(string assetPath, string brokenGuid, string targetGuid)
        {
            if (!File.Exists(assetPath))
                return new FixResult
                {
                    Success = false,
                    Description = $"Asset file not found at '{assetPath}'.",
                    TouchedPaths = null
                };

            string contents;
            try
            {
                contents = File.ReadAllText(assetPath);
            }
            catch (System.Exception e)
            {
                return new FixResult
                {
                    Success = false,
                    Description = $"Could not read '{assetPath}': {e.Message}",
                    TouchedPaths = null
                };
            }

            // Match every `guid: <brokenGuid>` occurrence on the asset — a single
            // broken GUID is typically referenced once, but the same target may
            // be wired into several PPtr fields.
            var pattern = new Regex(
                @"guid:\s*" + Regex.Escape(brokenGuid) + @"\b",
                RegexOptions.Compiled);

            if (!pattern.IsMatch(contents))
                return new FixResult
                {
                    Success = false,
                    Description = $"Broken GUID '{brokenGuid}' not found in '{assetPath}'. The issue may have already been resolved.",
                    TouchedPaths = null
                };

            var newContents = pattern.Replace(contents, $"guid: {targetGuid}");

            try
            {
                File.WriteAllText(assetPath, newContents);
            }
            catch (System.Exception e)
            {
                return new FixResult
                {
                    Success = false,
                    Description = $"Could not write '{assetPath}': {e.Message}",
                    TouchedPaths = null
                };
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            return new FixResult
            {
                Success = true,
                Description = $"Relinked broken GUID '{brokenGuid}' -> '{targetGuid}' in '{assetPath}'.",
                TouchedPaths = new[] { assetPath }
            };
        }

        struct GuidCandidate
        {
            public string Guid;
            public string AssetPath;
        }
    }
}
