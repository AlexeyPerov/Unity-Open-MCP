using System.Collections.Generic;
using System.Linq;

namespace UnityOpenMcpVerify.Rules.ProjectHealth
{
    public static class IssueMapper
    {
        // Reuses the canonical offline_integrity codes so the existing
        // remove_orphan_meta / fix_duplicate_guid fix providers link to both
        // producers. project_health is the in-Editor aggregator (live gate
        // scans); offline_integrity is the offline aggregator.
        public const string CodeOrphanMeta = "orphan_meta";
        public const string CodeDuplicateGuid = "duplicate_guid";
        public const string CodeMissingProjectSetting = "missing_project_setting";

        public static void MapToIssues(ProjectHealthData data, List<VerifyIssue> sink)
        {
            foreach (var orphan in data.OrphanMetas)
            {
                sink.Add(new VerifyIssue(
                    "project_health",
                    VerifySeverity.Warning,
                    orphan.MetaPath,
                    CodeOrphanMeta,
                    $"Orphaned .meta file (no companion asset): {orphan.MetaPath}"));
            }

            foreach (var dup in data.DuplicateGuids)
            {
                var paths = string.Join(", ", dup.Paths.OrderBy(p => p));
                sink.Add(new VerifyIssue(
                    "project_health",
                    VerifySeverity.Error,
                    dup.Paths[0],
                    CodeDuplicateGuid,
                    $"Duplicate GUID {dup.Guid} shared by {dup.Paths.Count} assets: {paths}"));
            }

            foreach (var setting in data.SettingIssues)
            {
                sink.Add(new VerifyIssue(
                    "project_health",
                    VerifySeverity.Error,
                    setting.SettingsPath,
                    CodeMissingProjectSetting,
                    $"ProjectSettings issue: {setting.Field} — {setting.Detail}"));
            }
        }
    }
}
