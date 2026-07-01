using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules
{
    public class ProjectHealthRule : IVerifyRule
    {
        public string Id => "project_health";

        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            // project_health is a whole-project rule by nature (duplicate GUID,
            // broken-asset, empty-scene, ProjectSettings integrity all need the
            // full tree). Only run in Full mode so a scoped checkpoint /
            // validate_edit does not pay the whole-tree cost or surface
            // unrelated project-wide issues as gate deltas.
            if (mode != VerifyRunMode.Full) return;

            var paths = scope.Paths ?? new string[0];
            var settings = ProjectHealth.ProjectHealthScanSettings.Default();
            var data = ProjectHealth.Scanner.Scan(paths, settings, fullScan: true);
            ProjectHealth.IssueMapper.MapToIssues(data, settings, sink);
        }
    }
}
