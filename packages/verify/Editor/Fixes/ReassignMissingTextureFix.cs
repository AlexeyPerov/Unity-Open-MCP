using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityOpenMcpVerify.Internals.RegexPatterns;

namespace UnityOpenMcpVerify.Fixes
{
    // reassign_missing_texture — reassigns a texture to the material's null
    // texture slot(s). Which texture to use is a judgment call the operator
    // must make (a wrong pick silently changes the material's appearance), so
    // the fix is Safe=false and surfaces candidates via Describe; Apply
    // requires a chosen target_texture (asset path or GUID).
    //
    // Producer: the `materials` rule emits `missing_texture` with the .mat
    // path as the issue's asset path.
    public class ReassignMissingTextureFix : IFixProvider
    {
        public string FixId => "reassign_missing_texture";

        public bool CanFix(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var issueCode))
                return false;
            return ruleId == "materials" && issueCode == "missing_texture";
        }

        public FixDescription Describe(string issueId)
        {
            IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _);

            var candidates = assetPath != null ? FindCandidateTextures() : new List<TextureCandidate>(0);
            var desc = candidates.Count > 0
                ? $"Reassign a texture to the null texture slot(s) on '{assetPath}'. " +
                  $"{candidates.Count} candidate texture(s) found by name. " +
                  "Provide one via apply_fix with target_texture (asset path or GUID) to apply."
                : $"Reassign a texture to the null texture slot(s) on '{assetPath}'. No automatic candidates found — " +
                  "use unity_open_mcp_find_references or list_assets to identify the intended texture before applying.";

            return new FixDescription
            {
                FixId = FixId,
                IssueId = issueId,
                AssetPath = assetPath,
                Description = desc,
                // A wrong texture silently changes the material's look — never
                // auto-apply under enforce.
                Safe = false,
            };
        }

        public FixResult Apply(string issueId)
        {
            return Apply(issueId, targetTexture: null);
        }

        public FixResult Apply(string issueId, string targetTexture)
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

            if (string.IsNullOrEmpty(targetTexture))
                return new FixResult
                {
                    Success = false,
                    Description = "reassign_missing_texture requires a chosen target_texture (asset path or GUID). No automatic choice is made.",
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

            var shader = material.shader;
            if (shader == null)
                return new FixResult
                {
                    Success = false,
                    Description = $"Material '{assetPath}' has no shader — assign a shader first (reassign_missing_shader).",
                    TouchedPaths = null
                };

            // Resolve the target texture: a 32-hex string is treated as a GUID,
            // otherwise as an asset path.
            Texture texture;
            string resolvedPath;
            if (SharedRegex.Guid32Hex.IsMatch(targetTexture))
            {
                resolvedPath = AssetDatabase.GUIDToAssetPath(targetTexture);
                if (string.IsNullOrEmpty(resolvedPath))
                    return new FixResult
                    {
                        Success = false,
                        Description = $"target_texture GUID '{targetTexture}' does not resolve to any asset.",
                        TouchedPaths = null
                    };
            }
            else
            {
                resolvedPath = targetTexture;
            }

            try { texture = AssetDatabase.LoadAssetAtPath<Texture>(resolvedPath); }
            catch (System.Exception e)
            {
                return new FixResult
                {
                    Success = false,
                    Description = $"Could not load texture '{resolvedPath}': {e.Message}",
                    TouchedPaths = null
                };
            }

            if (texture == null)
                return new FixResult
                {
                    Success = false,
                    Description = $"No texture found at '{resolvedPath}'.",
                    TouchedPaths = null
                };

            // Reassign to every TexEnv property that is currently null. A
            // material usually has a single _MainTex, but we cover the case of
            // several missing slots so the operator doesn't need one call per
            // property.
            var assigned = new List<string>();
            var propCount = shader.GetPropertyCount();
            for (var i = 0; i < propCount; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var propName = shader.GetPropertyName(i);
                if (material.GetTexture(propName) != null) continue;
                material.SetTexture(propName, texture);
                assigned.Add(propName);
            }

            if (assigned.Count == 0)
                return new FixResult
                {
                    Success = true,
                    Description = $"No null texture slots found on '{assetPath}' — the issue may already be resolved.",
                    TouchedPaths = null
                };

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

            return new FixResult
            {
                Success = true,
                Description = $"Reassigned texture '{resolvedPath}' to {assigned.Count} slot(s) on '{assetPath}': {string.Join(", ", assigned)}.",
                TouchedPaths = new[] { assetPath }
            };
        }

        // -------------------------------------------------------------------
        // Candidate discovery — textures under Assets/ (name heuristic)
        // -------------------------------------------------------------------

        private static List<TextureCandidate> FindCandidateTextures()
        {
            var candidates = new List<TextureCandidate>();
            var guids = AssetDatabase.FindAssets("t:Texture");
            var seen = new HashSet<string>();
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || path.StartsWith("Packages/")) continue;
                if (!seen.Add(path)) continue;
                candidates.Add(new TextureCandidate { Guid = guid, AssetPath = path });
                if (candidates.Count >= 8) break;
            }
            return candidates;
        }

        struct TextureCandidate
        {
            public string Guid;
            public string AssetPath;
        }
    }
}
