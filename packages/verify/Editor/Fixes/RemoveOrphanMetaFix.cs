using System.IO;
using UnityEditor;

namespace UnityOpenMcpVerify.Fixes
{
    // remove_orphan_meta — deletes a .meta file whose companion asset was
    // deleted. No asset data is lost (the .meta is already detached), so the
    // fix is Safe and auto-suggestable under enforce.
    //
    // Producers of the `orphan_meta` code:
    //   - project_health rule (live Editor, full-scan only)
    //   - offline_integrity scanner (offline, project-wide)
    // Both emit the issue with the .meta path as the issue's asset path.
    public class RemoveOrphanMetaFix : IFixProvider
    {
        public string FixId => "remove_orphan_meta";

        public bool CanFix(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var issueCode))
                return false;

            var code = issueCode ?? "";
            return (ruleId == "project_health" || ruleId == "offline_integrity")
                && code == "orphan_meta";
        }

        public FixDescription Describe(string issueId)
        {
            IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _);

            return new FixDescription
            {
                FixId = FixId,
                IssueId = issueId,
                AssetPath = assetPath,
                Description = $"Delete orphaned .meta file '{assetPath}' (its companion asset no longer exists). No asset data is lost.",
                // Deleting a detached .meta loses no asset data — Safe to
                // auto-suggest under enforce.
                Safe = true,
            };
        }

        public FixResult Apply(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out _, out _, out var metaPath, out _))
                return new FixResult
                {
                    Success = false,
                    Description = $"Cannot parse issue id: {issueId}",
                    TouchedPaths = null
                };

            if (string.IsNullOrEmpty(metaPath))
                return new FixResult
                {
                    Success = false,
                    Description = "Issue id contains empty asset path.",
                    TouchedPaths = null
                };

            // The issue's asset path IS the .meta path. Refuse if it does not
            // end in .meta — guards against an issue id built from a non-meta
            // asset path (would mean the producer diverged from the contract).
            if (Path.GetExtension(metaPath).ToLowerInvariant() != ".meta")
                return new FixResult
                {
                    Success = false,
                    Description = $"remove_orphan_meta expects a .meta path, got '{metaPath}'.",
                    TouchedPaths = null
                };

            // Refuse if the companion asset still exists — that means the meta
            // is NOT actually orphaned (the issue is stale or the asset was
            // recreated). Deleting a meta whose asset exists would force Unity
            // to re-import and regenerate it, which is noisy and not what this
            // fix is for.
            var companion = metaPath.Substring(0, metaPath.Length - ".meta".Length);
            if (File.Exists(companion) || AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(companion) != null)
                return new FixResult
                {
                    Success = true,
                    Description = $"Companion asset exists at '{companion}' — '{metaPath}' is not orphaned. No change made.",
                    TouchedPaths = null
                };

            if (!File.Exists(metaPath))
                return new FixResult
                {
                    Success = true,
                    Description = $"Orphan .meta '{metaPath}' no longer exists — already resolved.",
                    TouchedPaths = null
                };

            // DeleteAsset handles both the file removal and the AssetDatabase
            // bookkeeping. It accepts an Assets/-rooted path.
            if (!AssetDatabase.DeleteAsset(metaPath))
                return new FixResult
                {
                    Success = false,
                    Description = $"AssetDatabase.DeleteAsset failed for '{metaPath}'.",
                    TouchedPaths = null
                };

            AssetDatabase.Refresh();

            return new FixResult
            {
                Success = true,
                Description = $"Deleted orphaned .meta file '{metaPath}'.",
                TouchedPaths = new[] { metaPath }
            };
        }
    }
}
