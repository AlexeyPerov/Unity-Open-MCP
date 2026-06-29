// M20 Plan 9 / T20.9.1 — 2D art pipeline: Texture import tools.
//
// Four typed tools covering the TextureImporter surface:
//
//   texture_get_importer — read-only: the TextureImporter settings
//                          (textureType / npotScale / maxTextureSize /
//                          compression / spriteImportMode / filterMode / …)
//                          for a texture asset. The import-pipeline config.
//   texture_set_import   — mutate the TextureImporter and reimport. A
//                          structured patch (texture_type / max_size /
//                          compression / filter_mode / aniso / sprite_mode /
//                          sRGB / readable / mipmap / normalmap / …). Folds
//                          sprite + normal-map presets into one typed call
//                          instead of separate tools.
//   texture_reimport     — force reimport of a texture without changing
//                          settings (useful after external file replacement).
//   texture_get          — read-only convenience: the runtime Texture info
//                          (width / height / format / filterMode) for a
//                          loaded asset (complementary to get_importer, which
//                          is the import-pipeline config).
//
// TextureImporter is built-in (UnityEditor.CoreModule) and present in every
// Unity install, so this domain ships UNGATED — no UNITY_OPEN_MCP_EXT_2D
// define. set_import / reimport use the EditorSettle lifecycle because a
// reimport can take seconds and may trigger a domain reload on platform
// switch. The `2d` tool group (shared with SpriteAtlas) is still hidden from
// ListTools until the session activates it via manage_tools.
//
// Naming: `unity_open_mcp_texture_<action>` (snake_case domain prefix).
#pragma warning disable CS0618
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Extensions.Texture
{
    // M20 Plan 9 / T20.9.1 — Texture import tools. Registry-discovered via
    // [BridgeToolType] + [BridgeTool]. set_import / reimport declare
    // IsMutating = true and EditorSettle (the reimport can take seconds and
    // may trigger a platform-switch domain reload); paths_hint is the texture
    // asset path.
    [BridgeToolType]
    public static class TextureTools
    {
        // =====================================================================
        // get_importer (read-only)
        // =====================================================================

        // Read the TextureImporter settings (the import-pipeline config) for a
        // texture asset. Read-only, gate-free.
        [BridgeTool("unity_open_mcp_texture_get_importer",
            Title = "Texture: Get Importer",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "sprite2d")]
        [System.ComponentModel.Description(
            "Read-only: the TextureImporter settings (the import-pipeline " +
            "config) for a texture asset. Reports textureType / textureShape / " +
            "npotScale / maxTextureSize / textureCompression / compressionQuality / " +
            "sRGBTexture / isReadable / mipmapEnabled / filterMode / anisoLevel / " +
            "wrapMode / alphaIsTransparency / spriteImportMode / spritePixelsPerUnit / " +
            "normalmap / crunchedCompression. Gate-free. Use texture_get for the " +
            "runtime Texture info (width / height / format).")]
        public static string GetImporter(string asset_path)
        {
            var resolved = ResolveTextureImporter(asset_path);
            if (resolved.Error != null) return resolved.Error;
            var sb = new StringBuilder(512);
            BuildImporterState(resolved.Importer, resolved.Path, sb);
            return TextureJson.Ok(sb.ToString());
        }

        // =====================================================================
        // set_import (mutating, EditorSettle)
        // =====================================================================

        // Mutate the TextureImporter and reimport. settings_json is a JSON
        // object patch (snake_case keys). Folds sprite + normal-map presets in:
        // pass sprite_mode (Single|Multiple|Polygon) to set Sprite import, or
        // pass normalmap: true to switch to a normal-map texture type. Each
        // field is optional — omit to leave unchanged. Unknown fields are
        // reported, not fatal.
        [BridgeTool("unity_open_mcp_texture_set_import",
            Title = "Texture: Set Import Settings",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "sprite2d")]
        [System.ComponentModel.Description(
            "Mutating: patch a TextureImporter and reimport the texture. " +
            "settings_json is a JSON object with optional keys: texture_type " +
            "(Default|NormalMap|Sprite|Cursor|Cookie|Lightmap|SingleChannel|...), " +
            "texture_shape (Texture2D|TextureCube), npot_scale " +
            "(None|ToNearest|ToLarger|ToSmaller), max_texture_size (32|64|...|8192), " +
            "compression (None|Uncompressed|Compressed|CompressedHQ|CompressedLQ), " +
            "compression_quality (0-100), crunched (bool), srgb (bool), readable " +
            "(bool), mipmap_enabled (bool), filter_mode (Point|Bilinear|Trilinear), " +
            "aniso_level (0-16), wrap_mode (Repeat|Clamp|Mirror|MirrorOnce), " +
            "alpha_is_transparency (bool), sprite_mode (None|Single|Multiple|Polygon), " +
            "sprite_pixels_per_unit (float), normalmap (bool). Each key is optional — " +
            "omit to leave unchanged. Unknown keys are reported in `unknownFields`, " +
            "not fatal. The reimport runs through the gate (EditorSettle) so the next " +
            "mutation sees the settled texture. Mutating: paths_hint is the asset path.")]
        public static string SetImport(
            string asset_path,
            string settings_json,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TextureJson.Error("paths_hint_required",
                    "texture_set_import is mutating; pass a non-empty paths_hint " +
                    "scoped to the texture asset path.");
            if (string.IsNullOrWhiteSpace(settings_json))
                return TextureJson.Error("missing_parameter",
                    "'settings_json' is required (e.g. " +
                    "{\"compression\":\"Compressed\",\"max_texture_size\":1024}).");

            var resolved = ResolveTextureImporter(asset_path);
            if (resolved.Error != null) return resolved.Error;
            var importer = resolved.Importer;

            var applied = new StringBuilder(256);
            var unknown = new StringBuilder(256);
            applied.Append('[');
            unknown.Append('[');
            bool firstApplied = true;
            bool firstUnknown = true;

            foreach (var entry in ApplyImporterPatch(settings_json, importer))
            {
                if (entry.Ok)
                {
                    if (!firstApplied) applied.Append(',');
                    firstApplied = false;
                    applied.Append("{\"field\":").Append(TextureJson.Esc(entry.Field));
                    applied.Append(",\"value\":").Append(entry.RawValue).Append('}');
                }
                else
                {
                    if (!firstUnknown) unknown.Append(',');
                    firstUnknown = false;
                    unknown.Append("{\"field\":").Append(TextureJson.Esc(entry.Field));
                    unknown.Append(",\"reason\":").Append(TextureJson.Esc(entry.Reason)).Append('}');
                }
            }
            applied.Append(']');
            unknown.Append(']');

            // Save + reimport. SaveAndReimport writes the .meta and triggers the
            // import pipeline; the EditorSettle lifecycle makes the dispatcher
            // wait for the import to settle before returning.
            importer.SaveAndReimport();
            AssetDatabase.SaveAssets();

            var sb = new StringBuilder(360);
            sb.Append("\"path\":").Append(TextureJson.Esc(resolved.Path));
            sb.Append(",\"applied\":").Append(applied);
            sb.Append(",\"unknownFields\":").Append(unknown);
            return TextureJson.Ok(sb.ToString());
        }

        // =====================================================================
        // reimport (mutating, EditorSettle)
        // =====================================================================

        // Force a reimport of a texture without changing settings. Useful after
        // external file replacement (e.g. a build pipeline overwrote the PNG).
        [BridgeTool("unity_open_mcp_texture_reimport",
            Title = "Texture: Reimport",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "sprite2d")]
        [System.ComponentModel.Description(
            "Mutating: force a reimport of a texture asset without changing its " +
            "import settings. Useful after an external build pipeline overwrites " +
            "the source file. Runs through the gate (EditorSettle) so the next " +
            "mutation sees the settled reimport. Mutating: paths_hint is the asset path.")]
        public static string Reimport(
            string asset_path,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TextureJson.Error("paths_hint_required",
                    "texture_reimport is mutating; pass a non-empty paths_hint.");

            var resolved = ResolveTexture(asset_path, expectImporter: true);
            if (resolved.Error != null) return resolved.Error;

            AssetDatabase.ImportAsset(resolved.Path, ImportAssetOptions.ForceUpdate);

            var sb = new StringBuilder(96);
            sb.Append("\"path\":").Append(TextureJson.Esc(resolved.Path));
            sb.Append(",\"reimported\":true");
            return TextureJson.Ok(sb.ToString());
        }

        // =====================================================================
        // get (read-only) — runtime Texture info
        // =====================================================================

        // Read the runtime Texture info (width / height / format / filterMode /
        // mipmap count) for a loaded asset. Complementary to get_importer,
        // which is the import-pipeline config.
        [BridgeTool("unity_open_mcp_texture_get",
            Title = "Texture: Get Runtime Info",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "sprite2d")]
        [System.ComponentModel.Description(
            "Read-only: the runtime Texture info (width / height / format / " +
            "filterMode / mipmap count / isReadable) for a loaded texture asset. " +
            "Complementary to texture_get_importer (the import-pipeline config). " +
            "Gate-free.")]
        public static string Get(string asset_path)
        {
            var resolved = ResolveTexture(asset_path, expectImporter: false);
            if (resolved.Error != null) return resolved.Error;
            var tex = resolved.Texture;

            var sb = new StringBuilder(256);
            sb.Append("\"path\":").Append(TextureJson.Esc(resolved.Path));
            sb.Append(",\"name\":").Append(TextureJson.Esc(tex.name));
            sb.Append(",\"type\":").Append(TextureJson.Esc(tex.GetType().Name));
            if (tex is Texture2D t2d)
            {
                sb.Append(",\"width\":").Append(t2d.width);
                sb.Append(",\"height\":").Append(t2d.height);
                sb.Append(",\"format\":").Append(TextureJson.Esc(t2d.format.ToString()));
                sb.Append(",\"mipmapCount\":").Append(t2d.mipmapCount);
            }
            sb.Append(",\"filterMode\":").Append(TextureJson.Esc(tex.filterMode.ToString()));
            sb.Append(",\"anisoLevel\":").Append(tex.anisoLevel);
            sb.Append(",\"wrapMode\":").Append(TextureJson.Esc(tex.wrapMode.ToString()));
            if (tex is Texture2D readableTex)
                sb.Append(",\"isReadable\":").Append(readableTex.isReadable ? "true" : "false");
            else
                sb.Append(",\"isReadable\":null");
            sb.Append(",\"instanceId\":").Append(tex.GetInstanceID());
            return TextureJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Helpers — resolve
        // =====================================================================

        struct ImporterResult
        {
            public TextureImporter Importer;
            public string Path;
            public string Error;
        }

        private static ImporterResult ResolveTextureImporter(string assetPath)
        {
            var resolved = ResolveTexture(assetPath, expectImporter: true);
            if (resolved.Error != null)
                return new ImporterResult { Error = resolved.Error };
            return new ImporterResult { Importer = resolved.Importer, Path = resolved.Path };
        }

        struct TextureResult
        {
            public Texture Texture;
            public TextureImporter Importer;
            public string Path;
            public string Error;
        }

        private static TextureResult ResolveTexture(string assetPath, bool expectImporter)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return new TextureResult { Error = TextureJson.Error("missing_parameter",
                    "'asset_path' is required.") };
            var normalized = assetPath.Replace('\\', '/').Trim();
            if (!normalized.StartsWith("Assets/"))
                return new TextureResult { Error = TextureJson.Error("invalid_asset_path",
                    $"asset_path must start with 'Assets/': '{normalized}'.") };

            if (expectImporter)
            {
                var importer = AssetImporter.GetAtPath(normalized) as TextureImporter;
                if (importer == null)
                    return new TextureResult { Error = TextureJson.Error("not_a_texture",
                        $"No TextureImporter at '{normalized}' (the asset is not a texture, or has not finished importing).") };
                return new TextureResult { Importer = importer, Path = normalized };
            }

            var tex = AssetDatabase.LoadAssetAtPath<Texture>(normalized);
            if (tex == null)
                return new TextureResult { Error = TextureJson.Error("asset_not_found",
                    $"No Texture asset at '{normalized}'.") };
            return new TextureResult { Texture = tex, Path = normalized };
        }

        // =====================================================================
        // Helpers — state serialization
        // =====================================================================

        private static void BuildImporterState(TextureImporter importer, string path, StringBuilder sb)
        {
            sb.Append("\"path\":").Append(TextureJson.Esc(path));
            sb.Append(",\"textureType\":").Append(TextureJson.Esc(importer.textureType.ToString()));
            sb.Append(",\"textureShape\":").Append(TextureJson.Esc(importer.textureShape.ToString()));
            sb.Append(",\"npotScale\":").Append(TextureJson.Esc(importer.npotScale.ToString()));
            sb.Append(",\"maxTextureSize\":").Append(importer.maxTextureSize);
            sb.Append(",\"textureCompression\":").Append(TextureJson.Esc(importer.textureCompression.ToString()));
            sb.Append(",\"compressionQuality\":").Append(importer.compressionQuality);
            sb.Append(",\"crunchedCompression\":").Append(importer.crunchedCompression ? "true" : "false");
            sb.Append(",\"sRGBTexture\":").Append(importer.sRGBTexture ? "true" : "false");
            sb.Append(",\"isReadable\":").Append(importer.isReadable ? "true" : "false");
            sb.Append(",\"streamingMipmaps\":").Append(importer.streamingMipmaps ? "true" : "false");
            sb.Append(",\"mipmapEnabled\":").Append(importer.mipmapEnabled ? "true" : "false");
            sb.Append(",\"filterMode\":").Append(TextureJson.Esc(importer.filterMode.ToString()));
            sb.Append(",\"anisoLevel\":").Append(importer.anisoLevel);
            sb.Append(",\"wrapMode\":").Append(TextureJson.Esc(importer.wrapMode.ToString()));
            sb.Append(",\"alphaIsTransparency\":").Append(importer.alphaIsTransparency ? "true" : "false");
            sb.Append(",\"spriteImportMode\":").Append(TextureJson.Esc(importer.spriteImportMode.ToString()));
            sb.Append(",\"spritePixelsPerUnit\":").Append(Num(importer.spritePixelsPerUnit));
            sb.Append(",\"normalmap\":").Append(importer.normalmap ? "true" : "false");
            if (importer.normalmap)
            {
                sb.Append(",\"normalmapFilter\":").Append(TextureJson.Esc(importer.normalmapFilter.ToString()));
                sb.Append(",\"convertToNormalmap\":").Append(importer.convertToNormalmap ? "true" : "false");
            }
        }

        // =====================================================================
        // Helpers — structured patch (snake_case JSON keys → TextureImporter)
        // =====================================================================

        struct PatchOutcome
        {
            public bool Ok;
            public string Field;
            public string RawValue;
            public string Reason;
        }

        // Snake-case JSON key → TextureImporter property name (camelCase).
        private static readonly Dictionary<string, string> SnakeToProperty = new()
        {
            { "max_texture_size", "maxTextureSize" },
            { "compression_quality", "compressionQuality" },
            { "crunched", "crunchedCompression" },
            { "srgb", "sRGBTexture" },
            { "readable", "isReadable" },
            { "mipmap_enabled", "mipmapEnabled" },
            { "aniso_level", "anisoLevel" },
            { "alpha_is_transparency", "alphaIsTransparency" },
            { "sprite_pixels_per_unit", "spritePixelsPerUnit" },
            { "texture_type", "textureType" },
            { "texture_shape", "textureShape" },
            { "npot_scale", "npotScale" },
            { "compression", "textureCompression" },
            { "filter_mode", "filterMode" },
            { "wrap_mode", "wrapMode" },
            { "sprite_mode", "spriteImportMode" },
            { "normalmap", "normalmap" },
        };

        // Type hint per known key: enum:{FullName}|int|float|bool.
        private static readonly Dictionary<string, string> TypeHint = new()
        {
            { "max_texture_size", "int" },
            { "compression_quality", "int" },
            { "crunched", "bool" },
            { "srgb", "bool" },
            { "readable", "bool" },
            { "mipmap_enabled", "bool" },
            { "aniso_level", "int" },
            { "alpha_is_transparency", "bool" },
            { "sprite_pixels_per_unit", "float" },
            { "texture_type", "enum:UnityEditor.TextureImporterType, UnityEditor" },
            { "texture_shape", "enum:UnityEditor.TextureImporterShape, UnityEditor" },
            { "npot_scale", "enum:UnityEditor.TextureImporterNPOTScale, UnityEditor" },
            { "compression", "enum:UnityEditor.TextureImporterCompression, UnityEditor" },
            { "filter_mode", "enum:UnityEngine.FilterMode, UnityEngine" },
            { "wrap_mode", "enum:UnityEngine.TextureWrapMode, UnityEngine" },
            { "sprite_mode", "enum:UnityEditor.SpriteImportMode, UnityEditor" },
            { "normalmap", "bool" },
        };

        private static List<PatchOutcome> ApplyImporterPatch(string json, TextureImporter importer)
        {
            var results = new List<PatchOutcome>();
            foreach (var kv in SnakeToProperty)
            {
                var rawValue = ExtractValue(json, kv.Key);
                if (rawValue == null) continue;

                var prop = typeof(TextureImporter).GetProperty(kv.Value,
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop == null)
                {
                    results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = "property missing" });
                    continue;
                }

                try
                {
                    var typeHint = TypeHint[kv.Key];
                    object converted;
                    if (typeHint == "bool")
                    {
                        if (!TryParseBool(rawValue, out var b))
                        {
                            results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = "invalid bool" });
                            continue;
                        }
                        converted = b;
                    }
                    else if (typeHint == "int")
                    {
                        if (!int.TryParse(rawValue.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        {
                            results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = "invalid int" });
                            continue;
                        }
                        converted = n;
                    }
                    else if (typeHint == "float")
                    {
                        if (!float.TryParse(rawValue.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                        {
                            results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = "invalid float" });
                            continue;
                        }
                        converted = f;
                    }
                    else if (typeHint.StartsWith("enum:"))
                    {
                        var typeName = typeHint.Substring("enum:".Length);
                        var enumType = System.Type.GetType(typeName, false, true);
                        if (enumType == null)
                        {
                            results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = "enum type missing" });
                            continue;
                        }
                        var cleaned = rawValue.Trim().Trim('"');
                        object enumValue;
                        if (System.Enum.IsDefined(enumType, cleaned))
                            enumValue = System.Enum.Parse(enumType, cleaned, ignoreCase: true);
                        else if (int.TryParse(cleaned, out var idx) && System.Enum.IsDefined(enumType, idx))
                            enumValue = System.Enum.ToObject(enumType, idx);
                        else
                        {
                            results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = "invalid enum value" });
                            continue;
                        }
                        converted = enumValue;
                    }
                    else
                    {
                        results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = "unsupported type hint" });
                        continue;
                    }

                    prop.SetValue(importer, converted);
                    results.Add(new PatchOutcome { Ok = true, Field = kv.Key, RawValue = rawValue });
                }
                catch (System.Exception e)
                {
                    results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = e.Message });
                }
            }
            return results;
        }

        // =====================================================================
        // Helpers — JSON value extraction (hand-rolled; no Newtonsoft in bridge)
        // =====================================================================

        private static bool TryParseBool(string raw, out bool value)
        {
            value = false;
            if (string.IsNullOrEmpty(raw)) return false;
            var s = raw.Trim().Trim('"');
            if (s == "true") { value = true; return true; }
            if (s == "false") { value = false; return true; }
            return false;
        }

        // Extract the raw JSON value for a top-level key. Returns null when absent.
        private static string ExtractValue(string json, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            var colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return null;

            char c = json[i];
            if (c == '"')
            {
                int end = i + 1;
                while (end < json.Length)
                {
                    if (json[end] == '\\' && end + 1 < json.Length) { end += 2; continue; }
                    if (json[end] == '"') break;
                    end++;
                }
                return json.Substring(i, System.Math.Min(end + 1, json.Length) - i);
            }
            if (c == '{' || c == '[')
            {
                char open = c;
                char close = open == '{' ? '}' : ']';
                int depth = 0;
                int end = i;
                while (end < json.Length)
                {
                    if (json[end] == '"')
                    {
                        end++;
                        while (end < json.Length)
                        {
                            if (json[end] == '\\' && end + 1 < json.Length) { end += 2; continue; }
                            if (json[end] == '"') break;
                            end++;
                        }
                    }
                    else if (json[end] == open) depth++;
                    else if (json[end] == close)
                    {
                        depth--;
                        if (depth == 0) { end++; break; }
                    }
                    end++;
                }
                return json.Substring(i, end - i);
            }
            // primitive — capture to comma or }/]
            int pEnd = i;
            while (pEnd < json.Length && json[pEnd] != ',' && json[pEnd] != '}' && json[pEnd] != ']')
                pEnd++;
            return json.Substring(i, pEnd - i).Trim();
        }

        private static string Num(float f)
            => f.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
