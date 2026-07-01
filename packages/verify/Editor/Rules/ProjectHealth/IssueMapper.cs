using System.Collections.Generic;
using System.Linq;

namespace UnityOpenMcpVerify.Rules.ProjectHealth
{
    public static class IssueMapper
    {
        // Canonical codes reused across offline_integrity + project_health.
        public const string CodeOrphanMeta = "orphan_meta";
        public const string CodeDuplicateGuid = "duplicate_guid";
        public const string CodeMissingProjectSetting = "missing_project_setting";

        // Ported folder / asset / scene codes.
        public const string CodeEmptyFolder = "project_empty_folder";
        public const string CodeMetaOnlyFolder = "project_meta_only_folder";
        public const string CodeDeepNesting = "project_deep_nesting";
        public const string CodeLargeFolder = "project_large_folder";
        public const string CodeBrokenAsset = "project_broken_asset";
        public const string CodeEmptyScene = "project_empty_scene";

        public static void MapToIssues(ProjectHealthData data, ProjectHealthScanSettings settings, List<VerifyIssue> sink)
        {
            foreach (var orphan in data.OrphanMetas)
            {
                sink.Add(new VerifyIssue("project_health", VerifySeverity.Warning,
                    orphan.MetaPath, CodeOrphanMeta,
                    $"Orphaned .meta file (no companion asset): {orphan.MetaPath}",
                    Evidence(("metaPath", orphan.MetaPath))));
            }

            foreach (var dup in data.DuplicateGuids)
            {
                var paths = string.Join(", ", dup.Paths.OrderBy(p => p));
                sink.Add(new VerifyIssue("project_health", VerifySeverity.Error,
                    dup.Paths[0], CodeDuplicateGuid,
                    $"Duplicate GUID {dup.Guid} shared by {dup.Paths.Count} assets: {paths}",
                    Evidence(("guid", dup.Guid),
                        ("assetPaths", paths),
                        ("assetCount", dup.Paths.Count.ToString()))));
            }

            foreach (var setting in data.SettingIssues)
            {
                sink.Add(new VerifyIssue("project_health", VerifySeverity.Error,
                    setting.SettingsPath, CodeMissingProjectSetting,
                    $"ProjectSettings issue: {setting.Field} — {setting.Detail}",
                    Evidence(("field", setting.Field),
                        ("settingsPath", setting.SettingsPath))));
            }

            foreach (var folder in data.FolderIssues)
            {
                // Source severity is Info for all folder issues; mapped to
                // Warning (Info → Warning per the severity mapping rule).
                sink.Add(new VerifyIssue("project_health", VerifySeverity.Warning,
                    folder.FolderPath, folder.IssueCode, folder.Detail,
                    Evidence(("folderPath", folder.FolderPath))));
            }

            foreach (var broken in data.BrokenAssets)
            {
                sink.Add(new VerifyIssue("project_health", VerifySeverity.Error,
                    broken.AssetPath, CodeBrokenAsset, broken.Detail,
                    Evidence(("assetPath", broken.AssetPath))));
            }

            foreach (var scene in data.EmptyScenes)
            {
                sink.Add(new VerifyIssue("project_health", VerifySeverity.Warning,
                    scene.ScenePath, CodeEmptyScene,
                    "Scene has zero root objects — effectively empty",
                    Evidence(("scenePath", scene.ScenePath))));
            }
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
