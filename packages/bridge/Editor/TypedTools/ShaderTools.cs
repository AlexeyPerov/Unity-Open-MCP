using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.TypedTools
{
    // M16 Plan 1 — read-only shader tools. shader_list_all enumerates shader
    // assets via AssetDatabase.FindAssets("t:Shader"); shader_get_data reads
    // shader properties + compile errors (folding UCP shader/errors into a
    // single tool). Both are gate-free (read-only).
    public static class ShaderTools
    {
        public static ToolDispatchResult ListAll(string body)
        {
            int maxResults = JsonBody.GetInt(body, "max_results", 200);
            if (maxResults < 1) maxResults = 1;

            var guids = AssetDatabase.FindAssets("t:Shader");
            var entries = new List<Entry>();
            int truncated = 0;

            foreach (var guid in guids)
            {
                if (entries.Count >= maxResults) { truncated++; continue; }

                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath)) continue;

                var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
                var name = shader != null ? shader.name : System.IO.Path.GetFileNameWithoutExtension(assetPath);

                entries.Add(new Entry { Name = name, Path = assetPath });
            }

            // Sort by name for stable output.
            entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            var sb = new StringBuilder(2048 + entries.Count * 64);
            sb.Append("{\"count\":").Append(entries.Count);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append(",\"shaders\":[");
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":\"").Append(TypedTargets.Esc(entries[i].Name));
                sb.Append("\",\"path\":\"").Append(TypedTargets.Esc(entries[i].Path)).Append("\"}");
            }
            sb.Append("]}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        public static ToolDispatchResult GetData(string body)
        {
            var assetPath = JsonBody.GetString(body, "asset_path");
            Shader shader = null;
            if (!string.IsNullOrEmpty(assetPath))
            {
                shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
                if (shader == null)
                    return ToolDispatchResult.Fail("shader_not_found",
                        $"Shader not found at asset_path '{assetPath}'.");
            }
            else
            {
                var name = JsonBody.GetString(body, "name");
                if (string.IsNullOrEmpty(name))
                    return ToolDispatchResult.Fail("missing_parameter",
                        "Provide 'asset_path' or 'name' to identify the shader.");
                shader = Shader.Find(name);
                if (shader == null)
                    return ToolDispatchResult.Fail("shader_not_found",
                        $"Shader not found by name '{name}'.");
                assetPath = AssetDatabase.GetAssetPath(shader);
            }

            int maxResults = JsonBody.GetInt(body, "max_results", 100);
            if (maxResults < 1) maxResults = 1;

            int propCount = shader.GetPropertyCount();
            int emitted = 0;
            int truncated = 0;

            var sb = new StringBuilder(2048);
            sb.Append("{\"name\":\"").Append(TypedTargets.Esc(shader.name));
            sb.Append("\",\"path\":\"").Append(TypedTargets.Esc(assetPath));
            sb.Append("\",\"isSupported\":").Append(shader.isSupported ? "true" : "false");
            sb.Append(",\"propertyCount\":").Append(propCount);
            sb.Append(",\"properties\":[");
            for (int i = 0; i < propCount; i++)
            {
                if (emitted >= maxResults) { truncated = propCount - emitted; break; }
                if (emitted > 0) sb.Append(',');
                var propName = shader.GetPropertyName(i);
                var propType = shader.GetPropertyType(i);
                var propDesc = shader.GetPropertyDescription(i);
                var attrs = shader.GetPropertyAttributes(i);
                sb.Append("{\"name\":\"").Append(TypedTargets.Esc(propName));
                sb.Append("\",\"type\":\"").Append(propType.ToString());
                sb.Append("\",\"description\":\"").Append(TypedTargets.Esc(propDesc)).Append("\"");
                if (attrs != null && attrs.Length > 0)
                {
                    sb.Append(",\"attributes\":[");
                    for (int a = 0; a < attrs.Length; a++)
                    {
                        if (a > 0) sb.Append(',');
                        sb.Append('"').Append(TypedTargets.Esc(attrs[a])).Append('"');
                    }
                    sb.Append("]");
                }
                sb.Append('}');
                emitted++;
            }
            sb.Append("],\"propertiesEmitted\":").Append(emitted);
            sb.Append(",\"propertiesTruncated\":").Append(truncated);

            // Fold UCP shader/errors into an `errors` field. A shader can carry
            // ShaderMessage entries when it failed to compile. Shader.messages
            // and UnityEditor.ShaderUtil.GetShaderMessages are internal — we
            // resolve them via reflection so this compiles against any Unity
            // version without InternalsVisibleTo.
            sb.Append(",\"errors\":");
            sb.Append(RenderShaderErrors(shader));
            sb.Append('}');

            return ToolDispatchResult.Ok(sb.ToString());
        }

        struct Entry { public string Name; public string Path; }

        static readonly System.Reflection.MethodInfo GetShaderMessagesMethod =
            typeof(UnityEditor.ShaderUtil).GetMethod("GetShaderMessages",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        // Render shader compile errors as a JSON array via reflection. The
        // returned array elements are internal UnityEditor.ShaderMessage
        // structs exposing `message` (string) and `platform` (string) fields.
        // Returns "[]" when the API is unavailable or the shader is clean.
        static string RenderShaderErrors(Shader shader)
        {
            if (shader == null || GetShaderMessagesMethod == null) return "[]";

            object messagesObj;
            try { messagesObj = GetShaderMessagesMethod.Invoke(null, new object[] { shader }); }
            catch { return "[]"; }
            if (!(messagesObj is System.Array messages) || messages.Length == 0) return "[]";

            var sb = new StringBuilder(256);
            sb.Append('[');
            for (int i = 0; i < messages.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var msg = messages.GetValue(i);
                var messageStr = ReadStringField(msg, "message");
                var platformStr = ReadStringField(msg, "platform");
                sb.Append("{\"message\":\"").Append(TypedTargets.Esc(messageStr ?? ""));
                sb.Append("\",\"platform\":\"").Append(TypedTargets.Esc(platformStr ?? ""));
                sb.Append("\"}");
            }
            sb.Append(']');
            return sb.ToString();
        }

        static string ReadStringField(object obj, string fieldName)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var field = t.GetField(fieldName);
            if (field != null) return field.GetValue(obj) as string;
            var prop = t.GetProperty(fieldName);
            if (prop != null) return prop.GetValue(obj) as string;
            return null;
        }
    }
}
