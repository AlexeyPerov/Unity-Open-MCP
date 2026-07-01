using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules
{
    public class ShaderAnalysisRule : IVerifyRule
    {
        public string Id => "shader_analysis";

        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            var data = new List<ShaderAnalysis.ShaderData>();
            ShaderAnalysis.Scanner.ScanPaths(scope.Paths, data);
            ShaderAnalysis.IssueMapper.MapToIssues(data, sink);
        }
    }
}
