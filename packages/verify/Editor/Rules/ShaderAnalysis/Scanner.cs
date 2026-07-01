using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpVerify.Rules.ShaderAnalysis
{
    public static class Scanner
    {
        // ShaderUtil.GetShaderMessages is internal — resolve it via reflection
        // so this rule compiles against any Unity version without
        // InternalsVisibleTo. Mirrors the shader_get_data tool approach.
        private static readonly MethodInfo GetShaderMessagesMethod =
            typeof(ShaderUtil).GetMethod("GetShaderMessages",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        public static void ScanPaths(string[] paths, List<ShaderData> sink)
        {
            if (paths == null || paths.Length == 0) return;

            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!IsShaderAsset(path)) continue;
                if (!File.Exists(path)) continue;

                var data = new ShaderData(path);
                AnalyzeShader(path, data);
                sink.Add(data);
            }
        }

        private static void AnalyzeShader(string assetPath, ShaderData data)
        {
            // Only compiled shader assets (.shader) expose a Shader object with
            // compile state. Shader Graph (.shadergraph) is a separate asset
            // type; its compile errors surface only after a material import —
            // we still flag a failure to load as a structural issue.
            Shader shader = null;
            try { shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath); }
            catch { }

            if (shader == null)
            {
                // For .shadergraph, a null load is not necessarily an error in
                // all Unity versions (the asset is a ScriptableObject, not a
                // Shader). Only flag .shader assets that fail to load.
                if (assetPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                    data.FailedToLoad = true;
                return;
            }

            if (!shader.isSupported)
                data.Unsupported = true;

            CollectCompileErrors(shader, data);
        }

        private static void CollectCompileErrors(Shader shader, ShaderData data)
        {
            if (GetShaderMessagesMethod == null) return;

            object messagesObj;
            try { messagesObj = GetShaderMessagesMethod.Invoke(null, new object[] { shader }); }
            catch { return; }
            if (!(messagesObj is Array messages)) return;

            foreach (var msg in messages)
            {
                var message = ReadStringField(msg, "message");
                var platform = ReadStringField(msg, "platform");
                if (!string.IsNullOrEmpty(message))
                    data.CompileErrors.Add(new ShaderError(message, platform));
            }
        }

        private static string ReadStringField(object obj, string fieldName)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var field = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
                return field.GetValue(obj) as string;
            var prop = t.GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null)
                return prop.GetValue(obj) as string;
            return null;
        }

        private static bool IsShaderAsset(string path)
        {
            return path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase);
        }
    }
}
