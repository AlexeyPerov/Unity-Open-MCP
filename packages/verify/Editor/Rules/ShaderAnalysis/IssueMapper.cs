using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.ShaderAnalysis
{
    public static class IssueMapper
    {
        public const string CodeMissingShader = "missing_shader_asset";
        public const string CodeShaderCompileError = "shader_compile_error";

        public static void MapToIssues(List<ShaderData> assets, List<VerifyIssue> sink)
        {
            foreach (var asset in assets)
            {
                if (asset.FailedToLoad)
                {
                    sink.Add(new VerifyIssue(
                        "shader_analysis",
                        VerifySeverity.Error,
                        asset.Path,
                        CodeMissingShader,
                        "Shader asset failed to load — it may be corrupted or removed."));
                    continue;
                }

                if (asset.Unsupported || asset.CompileErrors.Count > 0)
                {
                    var detail = asset.CompileErrors.Count > 0
                        ? asset.CompileErrors[0].Message
                        : "Shader is reported as unsupported by Unity (compile failure).";
                    sink.Add(new VerifyIssue(
                        "shader_analysis",
                        VerifySeverity.Error,
                        asset.Path,
                        CodeShaderCompileError,
                        $"Shader has compile errors: {detail}"));
                }
            }
        }
    }
}
