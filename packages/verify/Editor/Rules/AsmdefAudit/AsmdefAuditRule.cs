using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules
{
    public class AsmdefAuditRule : IVerifyRule
    {
        public string Id => "asmdef_audit";

        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            var settings = AsmdefAudit.AsmdefScanSettings.Default();
            var data = new List<AsmdefAudit.AsmdefData>();
            AsmdefAudit.Scanner.ScanPaths(scope.Paths, settings, data);
            AsmdefAudit.IssueMapper.MapToIssues(data, settings, sink);
        }
    }
}
