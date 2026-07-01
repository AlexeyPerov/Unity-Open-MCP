using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.Materials
{
    public class MaterialPropertyData
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Value { get; set; }
    }

    public class RendererData
    {
        public RendererData(string assetPath, string childPath)
        {
            AssetPath = assetPath;
            ChildPath = childPath;
        }

        public string AssetPath { get; }
        public string ChildPath { get; }
        public List<string> Warnings { get; } = new List<string>();
    }

    public class MaterialData
    {
        public MaterialData(string path)
        {
            Path = path;
        }

        public string Path { get; }
        public string Name { get; set; }
        public string ShaderName { get; set; }
        public int RenderQueue { get; set; }
        public int? ShaderDefaultRenderQueue { get; set; }

        public bool HasRenderQueueOverride => ShaderDefaultRenderQueue.HasValue && RenderQueue != ShaderDefaultRenderQueue.Value;

        public bool IsMissingShader { get; set; }
        public bool IsBuiltinShader { get; set; }

        public List<string> EnabledKeywords { get; set; } = new List<string>();
        public List<MaterialPropertyData> Properties { get; set; } = new List<MaterialPropertyData>();

        // Texture warnings collected per-property.
        public List<TextureWarning> TextureWarnings { get; } = new List<TextureWarning>();

        public string Fingerprint { get; set; }
        public List<string> DuplicatePaths { get; } = new List<string>();
        public bool IsDuplicate { get; set; }

        public List<string> ReferencedByPaths { get; } = new List<string>();

        public bool IsVariant { get; set; }
        public int VariantChainDepth { get; set; }
        public int? VariantOverrideCount { get; set; }
        public bool ParentLinkBroken { get; set; }

        public bool? SupportsGpuInstancing { get; set; }
        public bool GpuInstancingEnabled { get; set; }
        public bool? SrpBatcherCompatible { get; set; }

        public List<MaterialIssue> Issues { get; } = new List<MaterialIssue>();
    }

    public class TextureWarning
    {
        public TextureWarning(string propertyName, string issueCode, string detail)
        {
            PropertyName = propertyName;
            IssueCode = issueCode;
            Detail = detail;
        }

        public string PropertyName { get; }
        public string IssueCode { get; }
        public string Detail { get; }
    }

    /// <summary>Accumulated issue attached to a material (code + severity +
    /// description). The mapper flattens these into VerifyIssue records.</summary>
    public class MaterialIssue
    {
        public MaterialIssue(string code, string description, VerifySeverity severity)
        {
            Code = code;
            Description = description;
            Severity = severity;
        }

        public string Code { get; }
        public string Description { get; }
        public VerifySeverity Severity { get; }
    }

    public struct MaterialsScanSettings
    {
        public bool CheckMissingShader;
        public bool CheckMissingTexture;
        public bool CheckBuiltinShader;
        public bool CheckBuiltinTexture;
        public bool CheckRenderQueueOverride;
        public bool CheckDuplicateMaterials;
        public bool CheckUnusedMaterials;
        public bool CheckVariants;
        public bool CheckGpuInstancing;
        public bool CheckSrpBatcher;
        public int VariantDeepChainThreshold;
        public int VariantHeavyOverridesThreshold;

        public static MaterialsScanSettings Default()
        {
            return new MaterialsScanSettings
            {
                CheckMissingShader = true,
                CheckMissingTexture = true,
                CheckBuiltinShader = true,
                CheckBuiltinTexture = true,
                CheckRenderQueueOverride = true,
                CheckDuplicateMaterials = true,
                CheckUnusedMaterials = true,
                CheckVariants = true,
                CheckGpuInstancing = true,
                CheckSrpBatcher = true,
                VariantDeepChainThreshold = 3,
                VariantHeavyOverridesThreshold = 8,
            };
        }
    }
}
