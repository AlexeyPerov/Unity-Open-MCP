using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityOpenMcpVerify.Internals.AssetDatabase;
using UnityOpenMcpVerify.Internals.RegexPatterns;

namespace UnityOpenMcpVerify.Rules.Materials
{
    public static class Scanner
    {
        public static void ScanPaths(string[] paths, List<MaterialData> sink)
        {
            if (paths == null || paths.Length == 0) return;

            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!AssetTypeUtilities.IsTextSerializedYaml(path)) continue;
                if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(path)) continue;

                var data = new MaterialData(path);
                CollectMaterialReferences(path, data);
                sink.Add(data);
            }
        }

        // A material's text serialization carries:
        //   m_Shader: {fileID: <id>, guid: <guid>, type: <t>}
        // and, inside m_SavedProperties.m_TexEnvs, per-texture:
        //   m_Texture: {fileID: <id>, guid: <guid>, type: <t>}
        //
        // Both are standard PPtr edges. We scan for `guid:` lines and resolve
        // each against the asset DB. Built-in shaders/textures use all-zero
        // GUIDs with a non-zero fileID (e.g. the Standard shader GUID
        // 0000000000000000f000000000000000) — those resolve to nothing in the
        // DB but are valid Unity built-ins, so we treat all-zero GUIDs as
        // resolving (they are not broken external edges).
        private static void CollectMaterialReferences(string assetPath, MaterialData data)
        {
            string[] lines;
            try { lines = File.ReadAllLines(assetPath); }
            catch { return; }

            var pptr = SharedRegex.ExternalFileAndGuid;

            // Track the current property name so issues carry a useful field
            // label. Material YAML indents texture refs two levels under
            // m_TexEnvs; the most recent property-starter line above a
            // reference is the owning field.
            var currentProperty = "";

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // Property starters: a key at the material-property level. We
                // capture both the top-level shader field and the per-texenv
                // property names (the `- _MainTex:` line).
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("m_Shader", StringComparison.Ordinal) ||
                    trimmed.StartsWith("m_Texture", StringComparison.Ordinal))
                {
                    var colon = trimmed.IndexOf(':');
                    if (colon > 0)
                    {
                        var name = trimmed.StartsWith("- ", StringComparison.Ordinal)
                            ? trimmed.Substring(2, colon - 2).Trim()
                            : trimmed.Substring(0, colon).Trim();
                        if (!string.IsNullOrEmpty(name))
                            currentProperty = name;
                    }
                }

                var matches = pptr.Matches(line);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    if (!m.Success) continue;
                    var guid = m.Groups[2].Value;
                    if (!IsRealGuid(guid)) continue;

                    var resolves = !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid));
                    var property = string.IsNullOrEmpty(currentProperty) ? "<unknown>" : currentProperty;
                    data.References.Add(new MaterialReference(property, guid, i + 1, resolves));
                }
            }
        }

        // Mirrors the dependencies rule's real-GUID check: drop the all-zero
        // built-in GUIDs (Standard shader etc.) since they are valid Unity
        // built-ins, not broken external edges.
        private static bool IsRealGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return false;
            if (guid.Length != 32) return false;
            if (guid.StartsWith("0000000000", StringComparison.Ordinal)) return false;
            for (var i = 0; i < 32; i++)
            {
                var c = guid[i];
                var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex) return false;
            }
            return true;
        }
    }
}
