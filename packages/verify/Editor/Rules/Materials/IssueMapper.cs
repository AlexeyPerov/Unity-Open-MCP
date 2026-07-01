using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.Materials
{
    public static class IssueMapper
    {
        public const string CodeMissingShader = "missing_shader";
        public const string CodeMissingTexture = "missing_texture";
        public const string CodeBuiltinShader = "builtin_shader";
        public const string CodeBuiltinTexture = "builtin_texture";
        public const string CodeRenderQueueOverride = "render_queue_override";
        public const string CodeUnableToLoad = "unable_to_load";
        public const string CodeDuplicateMaterial = "duplicate_material";
        public const string CodeUnusedMaterial = "unused_material";
        public const string CodeVariantParentInvalid = "variant_parent_invalid";
        public const string CodeVariantDeepChain = "variant_deep_chain";
        public const string CodeVariantHeavyOverrides = "variant_heavy_overrides";
        public const string CodeGpuInstancingOff = "gpu_instancing_off";
        public const string CodeSrpBatcherIncompatible = "srp_batcher_incompatible";
        public const string CodeNullMaterial = "null_material";
        public const string CodeNullMaterialSlot = "null_material_slot";
        public const string CodeBuiltinMaterial = "builtin_material";

        public static void MapToIssues(List<MaterialData> materials, List<RendererData> renderers, List<VerifyIssue> sink)
        {
            foreach (var data in materials)
            {
                foreach (var issue in data.Issues)
                {
                    sink.Add(new VerifyIssue("materials", issue.Severity, data.Path, issue.Code, issue.Description,
                        Evidence(("material", data.Name),
                            ("shader", data.ShaderName))));
                }
                foreach (var tw in data.TextureWarnings)
                {
                    var severity = tw.IssueCode == CodeMissingTexture ? VerifySeverity.Warning : VerifySeverity.Warning;
                    sink.Add(new VerifyIssue("materials", severity, data.Path, tw.IssueCode,
                        $"{tw.Detail}",
                        Evidence(("material", data.Name),
                            ("property", tw.PropertyName))));
                }
            }

            // Renderer-side warnings attach to the GameObject asset path.
            foreach (var rd in renderers)
            {
                foreach (var warning in rd.Warnings)
                {
                    sink.Add(new VerifyIssue("materials", VerifySeverity.Warning, rd.AssetPath, warning,
                        $"Renderer '{rd.ChildPath}': {RendererWarningText(warning)}",
                        Evidence(("renderer", rd.ChildPath),
                            ("warningCode", warning))));
                }
            }
        }

        private static IReadOnlyDictionary<string, string> Evidence(params (string, string)[] pairs)
        {
            var dict = new Dictionary<string, string>();
            foreach (var (k, v) in pairs)
            {
                if (!string.IsNullOrEmpty(k) && v != null)
                    dict[k] = v;
            }
            return dict;
        }

        private static string RendererWarningText(string code)
        {
            switch (code)
            {
                case CodeNullMaterial: return "null material";
                case CodeNullMaterialSlot: return "null material slot";
                case CodeBuiltinMaterial: return "uses a unity_builtin material";
                default: return code;
            }
        }
    }
}
