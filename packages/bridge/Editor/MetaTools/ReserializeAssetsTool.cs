using System.IO;
using System.Text;
using UnityEditor;

namespace UnityOpenMcpBridge.MetaTools
{
    // M9 Plan 1 — reserialize round-trip. Wraps AssetDatabase.ForceReserializeAssets
    // so an agent can text-edit a .prefab/.unity/.asset/.mat/.controller/.anim and
    // normalize through Unity's own serializer to catch missing fields, wrong
    // indents, and stale fileIDs. Counts as a mutation — runs the full gate path
    // (checkpoint -> reserialize -> validate -> delta).
    //
    // Scope: explicit `paths` array only. Whole-project reserialize is intentionally
    // not exposed because the gate needs scoped paths_hint to validate the delta;
    // enumerating affected assets is the safe failure mode.
    public static class ReserializeAssetsTool
    {
        public static readonly string[] SupportedExtensions =
        {
            ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim"
        };

        public static ToolDispatchResult Execute(string body)
        {
            var paths = JsonBody.GetStringArray(body, "paths");
            if (paths == null || paths.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'paths' is required and must be a non-empty array of asset paths to reserialize. " +
                    "Whole-project reserialize is not supported via this tool — enumerate the assets you edited.");

            var normalized = NormalizePaths(paths);
            var invalid = CollectInvalid(normalized);
            if (invalid.Count > 0)
                return ToolDispatchResult.Fail("invalid_paths",
                    "One or more paths failed pre-flight checks: " + string.Join("; ", invalid));

            // Default: assets-only round-trip (ReserializeAssets) so a direct YAML
            // edit on the asset body does not churn the companion .meta with empty
            // importer-field whitespace (userData:/assetBundleName:). Callers that
            // need to round-trip importer metadata (upgrade workflows) opt in via
            // include_meta: true -> ReserializeAssetsAndMetadata.
            var includeMeta = JsonBody.GetBool(body, "include_meta", false);
            var options = ResolveOptions(includeMeta);

            try
            {
                AssetDatabase.ForceReserializeAssets(normalized, options);
                AssetDatabase.Refresh();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("reserialize_error",
                    $"AssetDatabase.ForceReserializeAssets threw: {e.Message}");
            }

            return ToolDispatchResult.Ok(BuildResult(normalized, includeMeta));
        }

        // Maps the user-facing include_meta flag to Unity's ForceReserializeAssetsOptions.
        // Exposed as a pure function so the mapping can be unit-tested without driving
        // AssetDatabase from EditMode.
        public static ForceReserializeAssetsOptions ResolveOptions(bool includeMeta)
        {
            return includeMeta
                ? ForceReserializeAssetsOptions.ReserializeAssetsAndMetadata
                : ForceReserializeAssetsOptions.ReserializeAssets;
        }

        // Normalize for AssetDatabase: forward slashes, no leading slash, rooted under Assets/.
        // Paths outside Assets/ or with unsupported extensions are rejected before mutation.
        private static System.Collections.Generic.List<string> NormalizePaths(string[] rawPaths)
        {
            var result = new System.Collections.Generic.List<string>(rawPaths.Length);
            foreach (var raw in rawPaths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var p = raw.Replace('\\', '/').Trim('/');
                if (p.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
                {
                    // already rooted
                }
                else if (p.Equals("Assets", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                else
                {
                    p = "Assets/" + p;
                }
                result.Add(p);
            }
            return result;
        }

        private static System.Collections.Generic.List<string> CollectInvalid(System.Collections.Generic.List<string> paths)
        {
            var invalid = new System.Collections.Generic.List<string>();
            foreach (var p in paths)
            {
                var ext = Path.GetExtension(p).ToLowerInvariant();
                bool extOk = false;
                foreach (var supported in SupportedExtensions)
                {
                    if (supported == ext) { extOk = true; break; }
                }
                if (!extOk)
                {
                    invalid.Add($"{p} (unsupported extension '{ext}'; supported: {string.Join(", ", SupportedExtensions)})");
                    continue;
                }

                var full = p;
                if (!File.Exists(full))
                {
                    invalid.Add($"{p} (file not found)");
                }
            }
            return invalid;
        }

        private static string BuildResult(System.Collections.Generic.List<string> paths, bool includeMeta)
        {
            var sb = new StringBuilder(256 + paths.Count * 64);
            sb.Append("{\"reserialized\":[");
            for (int i = 0; i < paths.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(paths[i])).Append('"');
            }
            sb.Append("],\"totalCount\":").Append(paths.Count);
            sb.Append(",\"wholeProject\":false");
            sb.Append(",\"includeMeta\":").Append(includeMeta ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
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
            return sb.ToString();
        }
    }
}
