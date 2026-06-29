using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;

namespace UnityOpenMcpExtensions.Animation
{
    // M16 Plan 10 — shared helpers for the Animation extension pack.
    //
    // Asset load + JSON envelope builders + path validation for both asset
    // domains (.anim for AnimationClip, .controller for AnimatorController).
    // The Animation pack mutates assets on disk, so every mutator runs the
    // gate path with paths_hint scoped to the asset path.
    //
    // Naming: tool ids follow `unity_open_mcp_animation_<action>` and
    // `unity_open_mcp_animator_<action>` (snake_case domain prefix).
    static class AnimationJson
    {
        public const string ClipExtension = ".anim";
        public const string ControllerExtension = ".controller";

        public static string Ok(string body)
            => "{\"status\":\"ok\"," + (body ?? "") + "}";

        public static string Error(string code, string message)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        public static string Esc(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Normalize(string path)
            => string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');

        // Validate an 'Assets/'-rooted path with a specific extension. Returns
        // the normalized path or sets outError on a bad shape.
        public static bool ValidateAssetPath(string assetPath, string extension,
            out string normalized, out string error)
        {
            normalized = null;
            error = null;
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                error = $"'asset_path' is required and must end in '{extension}'.";
                return false;
            }
            normalized = Normalize(assetPath);
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) ||
                !normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                error = $"'asset_path' must be an 'Assets/'-rooted path ending in '{extension}' " +
                        $"(e.g. 'Assets/Animations/Foo{extension}').";
                return false;
            }
            return true;
        }

        // Ensure all intermediate folders under Assets/ exist. Creates them
        // via Directory.CreateDirectory + AssetDatabase.Refresh so the next
        // AssetDatabase.CreateAsset call succeeds.
        public static void EnsureFolders(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
        }
    }
}
