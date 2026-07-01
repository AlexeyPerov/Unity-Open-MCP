using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules
{
    public class MaterialsRule : IVerifyRule
    {
        public string Id => "materials";

        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            var data = new List<Materials.MaterialData>();
            Materials.Scanner.ScanPaths(scope.Paths, data);
            Materials.IssueMapper.MapToIssues(data, sink);
        }
    }
}
