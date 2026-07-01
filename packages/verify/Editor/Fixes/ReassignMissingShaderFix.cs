using System.Collections.Generic;
using UnityEditor;

namespace UnityOpenMcpVerify.Fixes
{
    // reassign_missing_shader — reassigns a shader to a material whose shader
    // is null or the InternalErrorShader. Which shader to use is a judgment
    // call (a wrong pick silently changes the material's render behaviour),
    // so the fix is Safe=false and surfaces candidates via Describe; Apply
    // requires a chosen target_shader (shader name or asset path).
    //
    // Producer: the `materials` rule emits `missing_shader` with the .mat
    // path as the issue's asset path.
    public class ReassignMissingShaderFix : IFixProvider
    {
        private const string InternalErrorShader = "Hidden/InternalErrorShader";

        public string FixId => "reassign_missing_shader";

        public bool CanFix(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var issueCode))
                return false;
            return ruleId == "materials" && issueCode == "missing_shader";
        }

        public FixDescription Describe(string issueId)
        {
            IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _);

            var candidates = assetPath != null ? FindCandidateShaders() : new List<ShaderCandidate>(0);
            var desc = candidates.Count > 0
                ? $"Reassign a shader to '{assetPath}'. " +
                  $"{candidates.Count} candidate shader(s) found. " +
                  "Provide one via apply_fix with target_shader (shader name e.g. 'Standard', or asset path) to apply."
                : $"Reassign a shader to '{assetPath}'. No automatic candidates found — " +
                  "use find_references or list_assets to identify the intended shader before applying.";

            return new FixDescription
            {
                FixId = FixId,
                IssueId = issueId,
                AssetPath = assetPath,
                Description = desc,
                // A wrong shader silently changes rendering — never auto-apply.
                Safe = false,
            };
        }

        public FixResult Apply(string issueId)
        {
            return Apply(issueId, targetShader: null);
        }

        public FixResult Apply(string issueId, string targetShader)
        {
            if (!IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _))
                return new FixResult
                {
                    Success = false,
                    Description = $"Cannot parse issue id: {issueId}",
                    TouchedPaths = null
                };

            if (string.IsNullOrEmpty(assetPath))
                return new FixResult
                {
                    Success = false,
                    Description = "Issue id contains empty asset path.",
                    TouchedPaths = null
                };

            if (string.IsNullOrEmpty(targetShader))
                return new FixResult
                {
                    Success = false,
                    Description = "reassign_missing_shader requires a chosen target_shader (shader name or asset path). No automatic choice is made.",
                    TouchedPaths = null
                };

            Material material;
            try { material = AssetDatabase.LoadAssetAtPath<Material>(assetPath); }
            catch (System.Exception e)
            {
                return new FixResult
                {
                    Success = false,
                    Description = $"Could not load material '{assetPath}': {e.Message}",
                    TouchedPaths = null
                };
            }

            if (material == null)
                return new FixResult
                {
                    Success = false,
                    Description = $"Material not found at '{assetPath}'.",
                    TouchedPaths = null
                };

            // Resolve the target shader: try Shader.Find (handles names like
            // "Standard", "Universal Render Pipeline/Lit", and built-ins) then
            // fall back to loading by asset path.
            var shader = UnityEngine.Shader.Find(targetShader);
            string resolvedAs;
            if (shader != null)
            {
                resolvedAs = $"shader name '{targetShader}'";
            }
            else
            {
                try { shader = AssetDatabase.LoadAssetAtPath<UnityEngine.Shader>(targetShader); }
                catch (System.Exception e)
                {
                    return new FixResult
                    {
                        Success = false,
                        Description = $"Could not load shader '{targetShader}': {e.Message}",
                        TouchedPaths = null
                    };
                }
                resolvedAs = $"asset path '{targetShader}'";
            }

            if (shader == null)
                return new FixResult
                {
                    Success = false,
                    Description = $"No shader found for '{targetShader}' (tried Shader.Find and asset path).",
                    TouchedPaths = null
                };

            // Refuse to reassign to the error shader — that is the failure mode
            // this fix is meant to resolve, not a valid target.
            if (shader.name == InternalErrorShader)
                return new FixResult
                {
                    Success = false,
                    Description = $"target_shader '{targetShader}' resolves to the InternalErrorShader — pick a real shader.",
                    TouchedPaths = null
                };

            material.shader = shader;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

            return new FixResult
            {
                Success = true,
                Description = $"Reassigned shader on '{assetPath}' to {resolvedAs} ('{shader.name}').",
                TouchedPaths = new[] { assetPath }
            };
        }

        // -------------------------------------------------------------------
        // Candidate discovery — shaders under Assets/ + findable built-ins
        // -------------------------------------------------------------------

        private static List<ShaderCandidate> FindCandidateShaders()
        {
            var candidates = new List<ShaderCandidate>();
            var guids = AssetDatabase.FindAssets("t:Shader");
            var seen = new HashSet<string>();
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || path.StartsWith("Packages/")) continue;
                if (!seen.Add(path)) continue;
                var shader = AssetDatabase.LoadAssetAtPath<UnityEngine.Shader>(path);
                var name = shader != null ? shader.name : path;
                candidates.Add(new ShaderCandidate { Name = name, AssetPath = path });
                if (candidates.Count >= 8) break;
            }
            return candidates;
        }

        struct ShaderCandidate
        {
            public string Name;
            public string AssetPath;
        }
    }
}
