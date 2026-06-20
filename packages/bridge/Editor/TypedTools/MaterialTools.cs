// Deliberate use of deprecated GetInstanceID() / EditorUtility.InstanceIDToObject() — see docs/code-conventions.md §Instance IDs.
#pragma warning disable CS0618
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityOpenMcpBridge.TypedTools
{
    // M16 Plan 1 — typed material tools. Mirrors the surface UCP and Unity-MCP
    // expose (material create / get-properties / set-property / get-keywords /
    // set-keyword / set-shader). Materials are resolved by `asset_path` (.mat)
    // or by `instance_id` of a scene GameObject whose Renderer.sharedMaterial
    // is read, or the Material instance directly. Set operations record an
    // Undo before mutating and mark the asset dirty.
    //
    // Mutation operations return JSON directly consumed by the bridge; read
    // operations are gate-free and run on the live Editor thread.
    public static class MaterialTools
    {
        public static ToolDispatchResult Create(string body)
        {
            var assetPath = JsonBody.GetString(body, "asset_path");
            if (string.IsNullOrWhiteSpace(assetPath))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'asset_path' is required and must start with 'Assets/' and end with '.mat'.");

            var normalized = assetPath.Replace('\\', '/').Trim('/');
            if (!normalized.StartsWith("Assets/"))
                return ToolDispatchResult.Fail("invalid_paths",
                    $"asset_path must start with 'Assets/': '{normalized}'.");
            if (!normalized.EndsWith(".mat"))
                return ToolDispatchResult.Fail("invalid_paths",
                    $"asset_path must end with '.mat': '{normalized}'.");

            var shaderName = JsonBody.GetString(body, "shader_name");
            var shader = ResolveCreateShader(shaderName);
            if (shader == null)
                return ToolDispatchResult.Fail("shader_not_found",
                    $"Could not resolve shader '{shaderName ?? "(default)"}'. Use unity_open_mcp_shader_list_all to discover a valid shader name.");

            // Create intermediate folders if missing.
            var lastSlash = normalized.LastIndexOf('/');
            if (lastSlash > 0)
            {
                var dir = normalized.Substring(0, lastSlash);
                EnsureFolderRecursive(dir);
            }

            Material material;
            try
            {
                material = new Material(shader);
                material.name = System.IO.Path.GetFileNameWithoutExtension(normalized);
                AssetDatabase.CreateAsset(material, normalized);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("create_error",
                    $"Failed to create material at '{normalized}': {e.Message}");
            }

            var sb = new StringBuilder(256);
            sb.Append("{\"status\":\"ok\",\"path\":\"").Append(TypedTargets.Esc(normalized));
            sb.Append("\",\"name\":\"").Append(TypedTargets.Esc(material.name));
            sb.Append("\",\"shader\":\"").Append(TypedTargets.Esc(shader.name));
            sb.Append("\",\"instanceId\":").Append(material.GetInstanceID().ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult GetProperties(string body)
        {
            var resolved = ResolveMaterial(body);
            if (!resolved.Ok) return resolved.Result;

            var mat = resolved.Material;
            var shader = mat.shader;
            int maxResults = JsonBody.GetInt(body, "max_results", 100);
            if (maxResults < 1) maxResults = 1;

            int count = shader.GetPropertyCount();
            int emitted = 0;
            int truncated = 0;

            var sb = new StringBuilder(2048);
            sb.Append("{\"material\":\"").Append(TypedTargets.Esc(mat.name));
            sb.Append("\",\"shader\":\"").Append(TypedTargets.Esc(shader.name));
            sb.Append("\",\"properties\":[");

            for (int i = 0; i < count; i++)
            {
                if (emitted >= maxResults) { truncated = count - emitted; break; }
                if (emitted > 0) sb.Append(',');

                var propName = shader.GetPropertyName(i);
                var propType = shader.GetPropertyType(i);
                var propDesc = shader.GetPropertyDescription(i);

                sb.Append("{\"name\":\"").Append(TypedTargets.Esc(propName));
                sb.Append("\",\"type\":\"").Append(propType.ToString());
                sb.Append("\",\"description\":\"").Append(TypedTargets.Esc(propDesc));
                sb.Append("\",\"value\":").Append(ReadMaterialValue(mat, propName, propType));
                sb.Append('}');
                emitted++;
            }

            sb.Append("],\"count\":").Append(emitted);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult SetProperty(string body)
        {
            var resolved = ResolveMaterial(body);
            if (!resolved.Ok) return resolved.Result;
            var mat = resolved.Material;

            var property = JsonBody.GetString(body, "property");
            var typeStr = JsonBody.GetString(body, "type");
            if (string.IsNullOrEmpty(property))
                return ToolDispatchResult.Fail("missing_parameter", "'property' is required.");
            if (string.IsNullOrEmpty(typeStr))
                return ToolDispatchResult.Fail("missing_parameter", "'type' is required (color/float/int/vector/texture).");

            var shader = mat.shader;
            int idx = shader.FindPropertyIndex(property);
            if (idx < 0)
                return ToolDispatchResult.Fail("property_not_found",
                    $"Property '{property}' not found on shader '{shader.name}'.");

            var valueRaw = JsonBody.GetRawValue(body, "value");

            Undo.RecordObject(mat, $"MCP Set Material {property}");
            try
            {
                WriteMaterialValue(mat, property, typeStr.ToLowerInvariant(), valueRaw, shader.GetPropertyType(idx));
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("set_error",
                    $"Failed to set '{property}': {e.Message}");
            }
            EditorUtility.SetDirty(mat);

            var sb = new StringBuilder(128);
            sb.Append("{\"status\":\"ok\",\"material\":\"").Append(TypedTargets.Esc(mat.name));
            sb.Append("\",\"property\":\"").Append(TypedTargets.Esc(property));
            sb.Append("\",\"type\":\"").Append(TypedTargets.Esc(typeStr)).Append("\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult GetKeywords(string body)
        {
            var resolved = ResolveMaterial(body);
            if (!resolved.Ok) return resolved.Result;
            var mat = resolved.Material;

            var sb = new StringBuilder(256);
            sb.Append("{\"material\":\"").Append(TypedTargets.Esc(mat.name));
            sb.Append("\",\"keywords\":[");
            bool first = true;
            foreach (var kw in mat.enabledKeywords)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(TypedTargets.Esc(kw.name)).Append('"');
            }
            sb.Append("]}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult SetKeyword(string body)
        {
            var resolved = ResolveMaterial(body);
            if (!resolved.Ok) return resolved.Result;
            var mat = resolved.Material;

            var keyword = JsonBody.GetString(body, "keyword");
            if (string.IsNullOrEmpty(keyword))
                return ToolDispatchResult.Fail("missing_parameter", "'keyword' is required.");

            bool enabled = JsonBody.GetBool(body, "enabled", false);
            Undo.RecordObject(mat, $"MCP Set Keyword {keyword}");
            if (enabled) mat.EnableKeyword(keyword);
            else mat.DisableKeyword(keyword);
            EditorUtility.SetDirty(mat);

            var sb = new StringBuilder(128);
            sb.Append("{\"status\":\"ok\",\"material\":\"").Append(TypedTargets.Esc(mat.name));
            sb.Append("\",\"keyword\":\"").Append(TypedTargets.Esc(keyword));
            sb.Append("\",\"enabled\":").Append(enabled ? "true" : "false");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult SetShader(string body)
        {
            var resolved = ResolveMaterial(body);
            if (!resolved.Ok) return resolved.Result;
            var mat = resolved.Material;

            var shaderName = JsonBody.GetString(body, "shader_name");
            if (string.IsNullOrEmpty(shaderName))
                return ToolDispatchResult.Fail("missing_parameter", "'shader_name' is required.");

            var shader = Shader.Find(shaderName);
            if (shader == null)
                return ToolDispatchResult.Fail("shader_not_found",
                    $"Shader not found: '{shaderName}'. Use unity_open_mcp_shader_list_all to discover a valid shader name.");

            Undo.RecordObject(mat, "MCP Set Shader");
            mat.shader = shader;
            EditorUtility.SetDirty(mat);

            var sb = new StringBuilder(128);
            sb.Append("{\"status\":\"ok\",\"material\":\"").Append(TypedTargets.Esc(mat.name));
            sb.Append("\",\"shader\":\"").Append(TypedTargets.Esc(shaderName)).Append("\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --------------------------- helpers -----------------------------

        public struct ResolveResult
        {
            public bool Ok;
            public Material Material;
            public ToolDispatchResult Result;
        }

        public static ResolveResult ResolveMaterial(string body)
        {
            var assetPath = JsonBody.GetString(body, "asset_path");
            if (!string.IsNullOrEmpty(assetPath))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                if (mat == null)
                    return Fail("material_not_found", $"Material not found at path: '{assetPath}'.");
                return new ResolveResult { Ok = true, Material = mat };
            }

            int instanceId = JsonBody.GetInt(body, "instance_id", 0);
            if (instanceId != 0)
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj is Material directMat)
                    return new ResolveResult { Ok = true, Material = directMat };

                if (obj is GameObject go)
                {
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null && renderer.sharedMaterial != null)
                        return new ResolveResult { Ok = true, Material = renderer.sharedMaterial };
                }
                return Fail("material_not_found",
                    $"No material found for instance_id {instanceId}. Pass a Material instance, a GameObject with a Renderer, or use asset_path.");
            }

            return Fail("missing_parameter",
                "Provide 'asset_path' (.mat) or 'instance_id' (Material or GameObject with Renderer) to identify the material.");
        }

        static ResolveResult Fail(string code, string message)
            => new ResolveResult { Ok = false, Result = ToolDispatchResult.Fail(code, message) };

        public static Shader ResolveCreateShader(string shaderName)
        {
            if (!string.IsNullOrWhiteSpace(shaderName))
                return Shader.Find(shaderName);
            return Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard")
                ?? Shader.Find("Sprites/Default");
        }

        // Read a shader property's current value on the material as inline JSON.
        public static string ReadMaterialValue(Material mat, string propName, ShaderPropertyType type)
        {
            switch (type)
            {
                case ShaderPropertyType.Color:
                    {
                        var c = mat.GetColor(propName);
                        var sb = new StringBuilder(48);
                        sb.Append('[').Append(c.r.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(c.g.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(c.b.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(c.a.ToString("R", CultureInfo.InvariantCulture)).Append(']');
                        return sb.ToString();
                    }
                case ShaderPropertyType.Vector:
                    {
                        var v = mat.GetVector(propName);
                        var sb = new StringBuilder(48);
                        sb.Append('[').Append(v.x.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v.y.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v.z.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v.w.ToString("R", CultureInfo.InvariantCulture)).Append(']');
                        return sb.ToString();
                    }
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    return mat.GetFloat(propName).ToString("R", CultureInfo.InvariantCulture);
                case ShaderPropertyType.Int:
                    return mat.GetInteger(propName).ToString(CultureInfo.InvariantCulture);
                case ShaderPropertyType.Texture:
                    {
                        var tex = mat.GetTexture(propName);
                        if (tex == null) return "null";
                        var texPath = AssetDatabase.GetAssetPath(tex);
                        var sb = new StringBuilder(96);
                        sb.Append("{\"name\":\"").Append(TypedTargets.Esc(tex.name));
                        sb.Append("\",\"path\":\"").Append(TypedTargets.Esc(texPath));
                        sb.Append("\",\"instanceId\":").Append(tex.GetInstanceID().ToString(CultureInfo.InvariantCulture));
                        sb.Append('}');
                        return sb.ToString();
                    }
                default:
                    return "null";
            }
        }

        // Write a value to a shader property. `valueRaw` is the raw JSON token
        // for the `value` field (parsed by JsonBody.GetRawValue).
        public static void WriteMaterialValue(Material mat, string propName, string typeStr, string valueRaw, ShaderPropertyType shaderType)
        {
            switch (typeStr)
            {
                case "color":
                    {
                        var parts = ParseFloatArray(valueRaw);
                        if (parts == null || parts.Length < 3)
                            throw new System.FormatException("color value must be [r,g,b] or [r,g,b,a].");
                        float a = parts.Length >= 4 ? parts[3] : 1f;
                        mat.SetColor(propName, new Color(parts[0], parts[1], parts[2], a));
                        break;
                    }
                case "vector":
                    {
                        var parts = ParseFloatArray(valueRaw);
                        if (parts == null || parts.Length < 4)
                            throw new System.FormatException("vector value must be [x,y,z,w].");
                        mat.SetVector(propName, new Vector4(parts[0], parts[1], parts[2], parts[3]));
                        break;
                    }
                case "float":
                    {
                        if (!float.TryParse(StripQuotes(valueRaw), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                            throw new System.FormatException($"float value could not be parsed: '{valueRaw}'.");
                        mat.SetFloat(propName, f);
                        break;
                    }
                case "int":
                    {
                        if (!int.TryParse(StripQuotes(valueRaw), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                            throw new System.FormatException($"int value could not be parsed: '{valueRaw}'.");
                        mat.SetInteger(propName, n);
                        break;
                    }
                case "texture":
                    {
                        if (valueRaw == null || valueRaw.Trim() == "null")
                        {
                            mat.SetTexture(propName, null);
                            break;
                        }
                        var texPath = JsonBody.GetString("{\"v\":" + valueRaw + "}", "v");
                        if (!string.IsNullOrEmpty(texPath))
                        {
                            var tex = AssetDatabase.LoadAssetAtPath<Texture>(texPath);
                            mat.SetTexture(propName, tex);
                        }
                        else
                        {
                            // value is an object with "path"
                            var objPath = JsonBody.GetString(valueRaw, "path");
                            var tex = string.IsNullOrEmpty(objPath) ? null : AssetDatabase.LoadAssetAtPath<Texture>(objPath);
                            mat.SetTexture(propName, tex);
                        }
                        break;
                    }
                default:
                    throw new System.ArgumentException($"Unknown value type '{typeStr}'. Expected one of: color, float, int, vector, texture.");
            }
        }

        // Parse a JSON array of numbers [1,2,3,4] into floats.
        public static float[] ParseFloatArray(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            raw = raw.Trim();
            if (!raw.StartsWith("[") || !raw.EndsWith("]")) return null;
            var inner = raw.Substring(1, raw.Length - 2);
            if (string.IsNullOrWhiteSpace(inner)) return new float[0];
            var parts = inner.Split(',');
            var result = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                    return null;
            }
            return result;
        }

        static string StripQuotes(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2);
            return s;
        }

        public static void EnsureFolderRecursive(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (AssetDatabase.IsValidFolder(path)) return;
            var parts = path.Replace('\\', '/').Split('/');
            if (parts.Length == 0) return;
            var current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
