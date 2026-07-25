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
    // Apply narrows to a single slot when target_property is supplied (sourced
    // by the caller from the issue's evidence.property). Without it the fix
    // fills every null texture slot — which on a Standard material corrupts
    // _BumpMap/_OcclusionMap/_EmissionMap/etc. with the chosen texture, so
    // callers should pass target_property whenever the issue names one.
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
                  "Provide one via apply_fix with target_texture (asset path or GUID) to apply. " +
                  "Pass target_property (from the issue's evidence.property) to narrow to a single slot — " +
                  "without it the texture is written into every null slot, which corrupts optional slots like _BumpMap."
                : $"Reassign a texture to the null texture slot(s) on '{assetPath}'. No automatic candidates found — " +
                  "use unity_open_mcp_find_references or list_assets to identify the intended texture before applying. " +
                  "Pass target_property (from the issue's evidence.property) to narrow to a single slot.";

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
            return Apply(issueId, targetTexture: null, targetProperty: null);
        }

        // Two-arg overload retained for the IFixProvider surface and any caller
        // that does not narrow to a single property (fill-all-null-slots
        // behaviour). apply_fix routes through the three-arg overload when the
        // caller supplies target_property.
        public FixResult Apply(string issueId, string targetTexture)
        {
            return Apply(issueId, targetTexture, targetProperty: null);
        }

        public FixResult Apply(string issueId, string targetTexture, string targetProperty)
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

            // Assignment target resolution. When target_property is supplied
            // (sourced from the issue's evidence.property by the caller), the
            // fix narrows to that single slot. This matters because a Standard
            // material legitimately leaves _BumpMap / _ParallaxMap /
            // _OcclusionMap / _EmissionMap / _DetailMask / _DetailAlbedoMap /
            // _DetailNormalMap null, and the historical fill-all-null-slots
            // loop stuffed the chosen texture into every one of them —
            // visibly corrupting the material. Without target_property we keep
            // the fill-all behaviour as a convenience default.
            var hasTargetProperty = !string.IsNullOrEmpty(targetProperty);

            int targetIndex = -1;
            if (hasTargetProperty)
            {
                var propCount = shader.GetPropertyCount();
                for (var i = 0; i < propCount; i++)
                {
                    if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    if (shader.GetPropertyName(i) != targetProperty) continue;
                    targetIndex = i;
                    break;
                }
                if (targetIndex < 0)
                {
                    return new FixResult
                    {
                        Success = false,
                        Description = $"target_property '{targetProperty}' is not a texture property on shader '{shader.name}' for material '{assetPath}'. " +
                                      "Omit target_property to fill all null texture slots, or pass the property name from the issue's evidence.property.",
                        TouchedPaths = null
                    };
                }
            }

            var assigned = new List<string>();
            if (hasTargetProperty)
            {
                // Narrowed to the single requested slot. Failing here (slot
                // already filled) is preferable to silently no-oping or to
                // overwriting an existing texture the operator did not name.
                var propName = shader.GetPropertyName(targetIndex);
                if (material.GetTexture(propName) != null)
                {
                    return new FixResult
                    {
                        Success = false,
                        Description = $"target_property '{propName}' on material '{assetPath}' already has a texture. The issue may already be resolved; " +
                                      "remove the existing texture first if you intend to replace it.",
                        TouchedPaths = null
                    };
                }
                material.SetTexture(propName, texture);
                assigned.Add(propName);
            }
            else
            {
                // Fill-all fallback: every TexEnv property that is currently
                // null. A material usually has a single _MainTex, but we cover
                // the case of several missing slots so the operator doesn't
                // need one call per property when no specific slot is named.
                var propCount = shader.GetPropertyCount();
                for (var i = 0; i < propCount; i++)
                {
                    if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    var propName = shader.GetPropertyName(i);
                    if (material.GetTexture(propName) != null) continue;
                    material.SetTexture(propName, texture);
                    assigned.Add(propName);
                }
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
                Description = hasTargetProperty
                    ? $"Reassigned texture '{resolvedPath}' to slot '{assigned[0]}' on '{assetPath}'."
                    : $"Reassigned texture '{resolvedPath}' to {assigned.Count} slot(s) on '{assetPath}': {string.Join(", ", assigned)}. " +
                      "Pass target_property to narrow to a single slot.",
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
