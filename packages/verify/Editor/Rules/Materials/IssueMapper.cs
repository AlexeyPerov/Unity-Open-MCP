using System.Collections.Generic;
using System.Linq;

namespace UnityOpenMcpVerify.Rules.Materials
{
    public static class IssueMapper
    {
        public const string CodeMissingShader = "missing_shader";
        public const string CodeMissingTexture = "missing_texture";

        public static void MapToIssues(List<MaterialData> assets, List<VerifyIssue> sink)
        {
            foreach (var asset in assets)
            {
                // One issue per distinct unresolved GUID — a shader and a
                // texture pointing at the same broken GUID is one problem.
                var reportedGuids = new HashSet<string>();

                foreach (var reference in asset.References)
                {
                    if (reference.Resolves) continue;
                    if (!reportedGuids.Add(reference.TargetGuid)) continue;

                    // Shader vs texture classification: the m_Shader field owns
                    // the shader ref; everything else under m_TexEnvs is a
                    // texture. We classify by property name so the issue code
                    // matches what the operator needs to fix.
                    var isShader = reference.Property == "m_Shader";
                    var code = isShader ? CodeMissingShader : CodeMissingTexture;
                    var label = isShader ? "shader" : "texture";

                    sink.Add(new VerifyIssue(
                        "materials",
                        VerifySeverity.Error,
                        asset.Path,
                        code,
                        $"Material references missing {label} (guid {reference.TargetGuid}, property '{reference.Property}' at line {reference.Line})"));
                }
            }
        }
    }
}
