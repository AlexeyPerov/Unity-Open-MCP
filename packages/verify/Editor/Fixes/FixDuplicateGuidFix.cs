using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityOpenMcpVerify.Internals.RegexPatterns;

namespace UnityOpenMcpVerify.Fixes
{
    // fix_duplicate_guid — regenerates the GUID of one of the colliding assets
    // so the duplicate is resolved. Re-GUIDing silently rewires the asset
    // graph (every reference to the old GUID must track the new one), so the
    // fix is Safe=false and never auto-applied under enforce: the operator
    // chooses WHICH of the colliding assets to re-GUID (usually the
    // less-referenced one) by passing that asset's issue id.
    //
    // Producers of the `duplicate_guid` code:
    //   - project_health rule (live Editor, full-scan only)
    //   - offline_integrity scanner (offline, project-wide)
    // Both emit the issue with the asset path (NOT the .meta) as the issue's
    // asset path. The provider re-derives the duplicate set from the live
    // AssetDatabase so it operates on the project as it is now.
    public class FixDuplicateGuidFix : IFixProvider
    {
        public string FixId => "fix_duplicate_guid";

        public bool CanFix(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var issueCode))
                return false;

            var code = issueCode ?? "";
            return (ruleId == "project_health" || ruleId == "offline_integrity")
                && code == "duplicate_guid";
        }

        public FixDescription Describe(string issueId)
        {
            IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _);

            var guid = assetPath != null ? AssetDatabase.AssetPathToGUID(assetPath) : "";
            var siblings = guid != "" ? FindSiblingPaths(assetPath, guid) : new List<string>(0);

            var desc = siblings.Count > 0
                ? $"Regenerate the GUID of '{assetPath}' so it no longer collides with {siblings.Count} sibling(s): " +
                  $"{string.Join(", ", siblings.Take(8))}. " +
                  "Re-GUIDing silently rewires the asset graph — confirm THIS is the asset to re-key (usually the less-referenced one) before applying."
                : $"Regenerate the GUID of '{assetPath}'. No live sibling collision found — the duplicate may already be resolved.";

            return new FixDescription
            {
                FixId = FixId,
                IssueId = issueId,
                AssetPath = assetPath,
                Description = desc,
                // A wrong pick rewrites the asset's identity; references that
                // meant the OTHER asset keep pointing at the old GUID and now
                // silently track the wrong asset. Never auto-apply.
                Safe = false,
            };
        }

        public FixResult Apply(string issueId)
        {
            return Apply(issueId, regenerateGuid: null);
        }

        // regenerateGuid lets tests pin the new GUID deterministically. When
        // null (the production path) a fresh random 32-hex GUID is generated.
        public FixResult Apply(string issueId, string regenerateGuid)
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

            // Validate an explicit regenerate_guid before touching the asset so
            // callers learn the format error without needing a resolvable file.
            if (!string.IsNullOrEmpty(regenerateGuid) && !SharedRegex.Guid32Hex.IsMatch(regenerateGuid))
                return new FixResult
                {
                    Success = false,
                    Description = $"regenerate_guid '{regenerateGuid}' is not a valid 32-hex GUID.",
                    TouchedPaths = null
                };

            var metaPath = assetPath + ".meta";
            if (!File.Exists(metaPath))
                return new FixResult
                {
                    Success = false,
                    Description = $"Meta file not found at '{metaPath}'.",
                    TouchedPaths = null
                };

            var oldGuid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(oldGuid))
                return new FixResult
                {
                    Success = false,
                    Description = $"Could not resolve current GUID for '{assetPath}'.",
                    TouchedPaths = null
                };

            // Determine the new GUID. Deterministic override (tests) or a fresh
            // random one. Unity GUIDs are 32 hex chars; System.Guid.ToString
            // ("N") yields exactly that. The format of an explicit override was
            // already validated above.
            string newGuid;
            if (!string.IsNullOrEmpty(regenerateGuid))
            {
                newGuid = regenerateGuid.ToLowerInvariant();
            }
            else
            {
                newGuid = System.Guid.NewGuid().ToString("N");
            }

            if (newGuid == oldGuid)
                return new FixResult
                {
                    Success = false,
                    Description = "Generated GUID matches the existing one — no change.",
                    TouchedPaths = null
                };

            return RewriteMetaGuid(metaPath, assetPath, oldGuid, newGuid);
        }

        // -------------------------------------------------------------------
        // Sibling discovery
        // -------------------------------------------------------------------

        private static List<string> FindSiblingPaths(string assetPath, string guid)
        {
            var siblings = new List<string>();
            if (string.IsNullOrEmpty(guid)) return siblings;

            // Walk Assets/ and collect every path whose GUID matches — this is
            // the live counterpart of the offline pathsByGuid index. Scoped to
            // Assets/ so Packages/ (shared, GUID-stable) is never flagged.
            var allPaths = AssetDatabase.GetAllAssetPaths();
            foreach (var path in allPaths)
            {
                if (path == assetPath) continue;
                if (path == null || !path.StartsWith("Assets/")) continue;
                if (AssetDatabase.AssetPathToGUID(path) == guid)
                    siblings.Add(path);
            }
            return siblings;
        }

        // -------------------------------------------------------------------
        // Rewrite — replace the `guid:` line in the .meta and re-import
        // -------------------------------------------------------------------

        private static FixResult RewriteMetaGuid(string metaPath, string assetPath, string oldGuid, string newGuid)
        {
            string contents;
            try
            {
                contents = File.ReadAllText(metaPath);
            }
            catch (System.Exception e)
            {
                return new FixResult
                {
                    Success = false,
                    Description = $"Could not read '{metaPath}': {e.Message}",
                    TouchedPaths = null
                };
            }

            // Match the `guid: <32hex>` line. Unity writes it as
            // `guid: abcd...` (single space). Anchor to start-of-line so we
            // don't touch any incidental `guid:` substring elsewhere.
            var pattern = new Regex(
                @"(?m)^guid:\s*" + Regex.Escape(oldGuid) + @"\s*$",
                RegexOptions.Compiled);

            if (!pattern.IsMatch(contents))
                return new FixResult
                {
                    Success = false,
                    Description = $"GUID '{oldGuid}' not found in '{metaPath}'. The duplicate may already be resolved.",
                    TouchedPaths = null
                };

            var newContents = pattern.Replace(contents, $"guid: {newGuid}", 1);

            try
            {
                File.WriteAllText(metaPath, newContents);
            }
            catch (System.Exception e)
            {
                return new FixResult
                {
                    Success = false,
                    Description = $"Could not write '{metaPath}': {e.Message}",
                    TouchedPaths = null
                };
            }

            // Re-import both the meta and its companion asset so Unity picks up
            // the new GUID and refreshes its internal GUID->path index.
            AssetDatabase.ImportAsset(metaPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            return new FixResult
            {
                Success = true,
                Description = $"Regenerated GUID for '{assetPath}': {oldGuid} -> {newGuid}.",
                TouchedPaths = new[] { metaPath, assetPath }
            };
        }
    }
}
