using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.ProjectHealth
{
    public class OrphanMetaEntry
    {
        public OrphanMetaEntry(string metaPath)
        {
            MetaPath = metaPath;
        }

        /// <summary>A <c>.meta</c> file whose companion asset no longer exists.</summary>
        public string MetaPath { get; }
    }

    public class DuplicateGuidEntry
    {
        public DuplicateGuidEntry(string guid, List<string> paths)
        {
            Guid = guid;
            Paths = paths;
        }

        /// <summary>The GUID shared by two or more assets.</summary>
        public string Guid { get; }

        /// <summary>Every asset path that carries this GUID.</summary>
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

        /// <summary>ProjectSettings asset path (e.g.
        /// <c>ProjectSettings/ProjectSettings.asset</c>).</summary>
        public string SettingsPath { get; }

        /// <summary>The missing/invalid field path inside the settings file.</summary>
        public string Field { get; }

        public string Detail { get; }
    }

    public class ProjectHealthData
    {
        public List<OrphanMetaEntry> OrphanMetas { get; } = new List<OrphanMetaEntry>();
        public List<DuplicateGuidEntry> DuplicateGuids { get; } = new List<DuplicateGuidEntry>();
        public List<ProjectSettingIssue> SettingIssues { get; } = new List<ProjectSettingIssue>();
    }
}
