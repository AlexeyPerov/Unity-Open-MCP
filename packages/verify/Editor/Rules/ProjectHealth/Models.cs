using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.ProjectHealth
{
    public class OrphanMetaEntry
    {
        public OrphanMetaEntry(string metaPath)
        {
            MetaPath = metaPath;
        }

        public string MetaPath { get; }
    }

    public class DuplicateGuidEntry
    {
        public DuplicateGuidEntry(string guid, List<string> paths)
        {
            Guid = guid;
            Paths = paths;
        }

        public string Guid { get; }
        public List<string> Paths { get; }
    }

    public class ProjectSettingIssue
    {
        public ProjectSettingIssue(string settingsPath, string field, string detail)
        {
            SettingsPath = settingsPath;
            Field = field;
            Detail = detail;
        }

        public string SettingsPath { get; }
        public string Field { get; }
        public string Detail { get; }
    }

    /// <summary>A folder-structure entry (empty / meta-only / deep / large).</summary>
    public class FolderIssue
    {
        public FolderIssue(string folderPath, string issueCode, string detail)
        {
            FolderPath = folderPath;
            IssueCode = issueCode;
            Detail = detail;
        }

        public string FolderPath { get; }
        public string IssueCode { get; }
        public string Detail { get; }
    }

    /// <summary>An asset that failed to load (null or threw).</summary>
    public class BrokenAssetEntry
    {
        public BrokenAssetEntry(string assetPath, string detail)
        {
            AssetPath = assetPath;
            Detail = detail;
        }

        public string AssetPath { get; }
        public string Detail { get; }
    }

    /// <summary>A scene with zero root objects.</summary>
    public class EmptySceneEntry
    {
        public EmptySceneEntry(string scenePath)
        {
            ScenePath = scenePath;
        }

        public string ScenePath { get; }
    }

    public class ProjectHealthData
    {
        public List<OrphanMetaEntry> OrphanMetas { get; } = new List<OrphanMetaEntry>();
        public List<DuplicateGuidEntry> DuplicateGuids { get; } = new List<DuplicateGuidEntry>();
        public List<ProjectSettingIssue> SettingIssues { get; } = new List<ProjectSettingIssue>();
        public List<FolderIssue> FolderIssues { get; } = new List<FolderIssue>();
        public List<BrokenAssetEntry> BrokenAssets { get; } = new List<BrokenAssetEntry>();
        public List<EmptySceneEntry> EmptyScenes { get; } = new List<EmptySceneEntry>();
    }

    public struct ProjectHealthScanSettings
    {
        public bool CheckOrphanedMeta;
        public bool CheckDuplicateGuid;
        public bool CheckProjectSettings;
        public bool CheckEmptyFolders;
        public bool CheckMetaOnlyFolders;
        public bool CheckDeepNesting;
        public bool CheckLargeFolders;
        public bool CheckBrokenAssets;
        public bool CheckEmptyScenes;
        public int MaxFolderNestingDepth;
        public int MaxFilesPerFolder;

        public static ProjectHealthScanSettings Default()
        {
            return new ProjectHealthScanSettings
            {
                CheckOrphanedMeta = true,
                CheckDuplicateGuid = true,
                CheckProjectSettings = true,
                CheckEmptyFolders = true,
                CheckMetaOnlyFolders = true,
                CheckDeepNesting = true,
                CheckLargeFolders = true,
                CheckBrokenAssets = true,
                CheckEmptyScenes = true,
                MaxFolderNestingDepth = 8,
                MaxFilesPerFolder = 200,
            };
        }
    }
}
