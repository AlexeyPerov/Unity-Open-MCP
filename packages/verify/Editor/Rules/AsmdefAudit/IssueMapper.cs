using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.AsmdefAudit
{
    public static class IssueMapper
    {
        public const string CodeBrokenReference = "broken_asmdef_reference";
        public const string CodeMissingName = "asmdef_missing_name";
        public const string CodeMalformedAsmdef = "malformed_asmdef";

        public static void MapToIssues(List<AsmdefData> assets, List<VerifyIssue> sink)
        {
            foreach (var asset in assets)
            {
                if (asset.ParseFailed)
                {
                    sink.Add(MakeIssue(asset, CodeMalformedAsmdef,
                        $"Assembly definition failed to parse: {asset.ParseError ?? "unknown error"}",
                        VerifySeverity.Error));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(asset.Name))
                {
                    sink.Add(MakeIssue(asset, CodeMissingName,
                        "Assembly definition has no 'name' field — Unity cannot compile it.",
                        VerifySeverity.Error));
                }

                foreach (var reference in asset.References)
                {
                    if (reference.Resolves) continue;
                    sink.Add(MakeIssue(asset, CodeBrokenReference,
                        $"Assembly reference '{reference.Reference}' (line {reference.Line}) does not resolve to a compiled assembly or known asmdef.",
                        VerifySeverity.Error));
                }
            }
        }

        private static VerifyIssue MakeIssue(
            AsmdefData asset, string code, string description,
            VerifySeverity severity)
        {
            return new VerifyIssue("asmdef_audit", severity, asset.Path, code, description);
        }
    }
}
