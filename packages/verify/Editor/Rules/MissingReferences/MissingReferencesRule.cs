using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules
{
    public class MissingReferencesRule : IVerifyRule
    {
        public string Id => "missing_references";

        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            var fullScan = mode != VerifyRunMode.Checkpoint;
            var results = MissingReferences.Scanner.ScanPaths(scope.Paths, fullScan);
            MissingReferences.IssueMapper.MapToIssues(results, sink);
        }
    }
}
