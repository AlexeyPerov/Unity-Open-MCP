using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.Materials
{
    public static class IssueMapper
    {
        public const string CodeMissingShader = "missing_shader";
        // Note: there is no `missing_texture` code here. A null texture slot is
        // NOT reported by this rule — most optional shader texture properties
        // (_BumpMap/_ParallaxMap/_OcclusionMap/_DetailMask/...) are
        // legitimately null on a freshly-created material, so flagging every
        // null slot would produce false positives. A genuinely-missing texture
        // (a PPtr whose GUID no longer resolves) is surfaced by the
        // missing_references rule as missing_guid. The only texture-related
        // warning this rule emits is builtin_texture (a slot holding a
        // unity_builtin placeholder).
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
                    // TextureWarnings today only carries builtin_texture
                    // (Scanner.FindMaterialWarnings never adds anything else —
                    // see the comment on CodeMissingShader above). Keep the
                    // Severity explicit so a future code path does not silently
                    // demote a real warning.
                    sink.Add(new VerifyIssue("materials", VerifySeverity.Warning, data.Path, tw.IssueCode,
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
