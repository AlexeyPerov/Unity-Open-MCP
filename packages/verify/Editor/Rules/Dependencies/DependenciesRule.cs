using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules
{
    public class DependenciesRule : IVerifyRule
    {
        public string Id => "dependencies";

        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            var data = new List<Dependencies.AssetDependencyData>();
            Dependencies.Scanner.ScanPaths(scope.Paths, data);
            Dependencies.IssueMapper.MapToIssues(data, sink);
        }
    }
}
