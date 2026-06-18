using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;

namespace UnityOpenMcpBridge.TypedTools
{
    // M16 Plan 1 — typed project & asset-management tools (filesystem asset
    // operations). Each method parses the snake_case request body via JsonBody,
    // performs the AssetDatabase operation, and returns a ToolDispatchResult
    // whose Output is a hand-rolled JSON string (no Newtonsoft dependency).
    //
    // These tools are NOT registry-discovered: they are wired into the
    // BridgeHttpServer.DispatchTool switch alongside the other meta-tools so
    // their snake_case schemas (folders[], entries[], paths[]) parse the same
    // way as reserialize's paths[] array. Mutating members run the full gate
    // path with paths_hint; assets_refresh is a light mutation that may
    // bind a whole-project scope when whole_project: true.
    public static class AssetsTools
    {
        // Cross-platform invalid file-name characters. Path.GetInvalidFileNameChars
        // is OS-dependent (Linux/Mac returns only '/' and '\0'); Unity projects
        // must be portable across all platforms. Mirrors Unity-MCP's list.
        public static readonly char[] InvalidFileNameChars =
        {
            '/', '\\', '<', '>', ':', '"', '|', '?', '*',
            '\0', '\x01', '\x02', '\x03', '\x04', '\x05', '\x06', '\x07',
            '\x08', '\x09', '\x0A', '\x0B', '\x0C', '\x0D', '\x0E', '\x0F',
            '\x10', '\x11', '\x12', '\x13', '\x14', '\x15', '\x16', '\x17',
            '\x18', '\x19', '\x1A', '\x1B', '\x1C', '\x1D', '\x1E', '\x1F'
        };

        public static ToolDispatchResult CreateFolder(string body)
        {
            var entries = JsonBody.GetObjectArray(body, "folders");
            if (entries == null || entries.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'folders' is required and must be a non-empty array of " +
                    "{ parent_folder_path, new_folder_name } entries.");

            var created = new List<string>();
            var errors = new List<string>();

            foreach (var entry in entries)
            {
                var parent = JsonBody.GetString(entry, "parent_folder_path");
                var name = JsonBody.GetString(entry, "new_folder_name");

                if (string.IsNullOrWhiteSpace(name))
                {
                    errors.Add($"Cannot create folder in '{parent}': name is empty.");
                    continue;
                }

                var bad = name.IndexOfAny(InvalidFileNameChars);
                if (bad >= 0)
                {
                    errors.Add($"Cannot create folder '{name}' in '{parent}': invalid character '{name[bad]}'.");
                    continue;
                }

                if (string.IsNullOrEmpty(parent) || !AssetDatabase.IsValidFolder(parent))
                {
                    errors.Add($"Cannot create folder '{name}': parent '{parent}' is not a valid folder (must start with 'Assets/' and exist).");
                    continue;
                }

                var target = parent + "/" + name;
                if (AssetDatabase.IsValidFolder(target))
                {
                    errors.Add($"Cannot create folder '{name}' in '{parent}': already exists at '{target}'.");
                    continue;
                }

                var guid = AssetDatabase.CreateFolder(parent, name);
                if (string.IsNullOrEmpty(guid))
                {
                    errors.Add($"Failed to create folder '{name}' in '{parent}'.");
                    continue;
                }

                created.Add(target);
            }

            if (created.Count > 0)
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            return ToolDispatchResult.Ok(BuildFolderOpResult(created, errors, "created"));
        }

        public static ToolDispatchResult Copy(string body)
        {
            return CopyMove(body, "copy");
        }

        public static ToolDispatchResult Move(string body)
        {
            return CopyMove(body, "move");
        }

        static ToolDispatchResult CopyMove(string body, string op)
        {
            var entries = JsonBody.GetObjectArray(body, "entries");
            if (entries == null || entries.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'entries' is required and must be a non-empty array of { source, destination } pairs.");

            var done = new List<string>();
            var errors = new List<string>();

            foreach (var entry in entries)
            {
                var source = JsonBody.GetString(entry, "source");
                var dest = JsonBody.GetString(entry, "destination");

                if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dest))
                {
                    errors.Add($"Skipping entry with empty source or destination (source='{source}', dest='{dest}').");
                    continue;
                }

                if (!FileOrFolderExists(source))
                {
                    errors.Add($"{op}: source not found '{source}'.");
                    continue;
                }

                if (FileOrFolderExists(dest))
                {
                    errors.Add($"{op}: destination already exists '{dest}'.");
                    continue;
                }

                bool ok;
                string errMsg = null;
                try
                {
                    ok = op == "copy"
                        ? AssetDatabase.CopyAsset(source, dest)
                        : AssetDatabase.MoveAsset(source, dest) == "";
                    if (!ok && op == "move")
                        errMsg = AssetDatabase.MoveAsset(source, dest);
                }
                catch (System.Exception e)
                {
                    ok = false;
                    errMsg = e.Message;
                }

                if (ok) done.Add(op == "copy" ? dest : (source + " -> " + dest));
                else errors.Add($"{op} '{source}' -> '{dest}' failed: {errMsg ?? "AssetDatabase refused the operation."}");
            }

            if (done.Count > 0)
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            return ToolDispatchResult.Ok(BuildFolderOpResult(done, errors, op == "copy" ? "copied" : "moved"));
        }

        public static ToolDispatchResult Delete(string body)
        {
            var paths = JsonBody.GetStringArray(body, "paths");
            if (paths == null || paths.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'paths' is required and must be a non-empty array of asset paths to delete.");

            var deleted = new List<string>();
            var errors = new List<string>();

            foreach (var raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var path = raw.Replace('\\', '/').Trim('/');
                if (!FileOrFolderExists(path))
                {
                    errors.Add($"delete: not found '{path}'.");
                    continue;
                }

                // Prefer DeleteAsset (keeps .meta in sync); fall back to
                // MoveAssetToTrash if DeleteAsset refuses (e.g. protected asset).
                bool ok;
                string errMsg = null;
                try
                {
                    ok = AssetDatabase.DeleteAsset(path);
                    if (!ok)
                    {
                        ok = AssetDatabase.MoveAssetToTrash(path);
                        if (!ok) errMsg = "AssetDatabase.DeleteAsset returned false and MoveAssetToTrash also refused.";
                    }
                }
                catch (System.Exception e)
                {
                    ok = false;
                    errMsg = e.Message;
                }

                if (ok) deleted.Add(path);
                else errors.Add($"delete '{path}' failed: {errMsg ?? "AssetDatabase refused the operation."}");
            }

            if (deleted.Count > 0)
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            return ToolDispatchResult.Ok(BuildFolderOpResult(deleted, errors, "deleted"));
        }

        public static ToolDispatchResult Refresh(string body)
        {
            var wholeProject = JsonBody.GetBool(body, "whole_project", true);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var sb = new StringBuilder(128);
            sb.Append("{\"refreshed\":true,\"wholeProject\":").Append(wholeProject ? "true" : "false");
            sb.Append(",\"isCompiling\":").Append(EditorApplication.isCompiling ? "true" : "false");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // AssetDatabase treats files and folders uniformly through IsValidFolder
        // + System.IO.File.Exists; folders are not File.Exists-true.
        static bool FileOrFolderExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (AssetDatabase.IsValidFolder(path)) return true;
            return File.Exists(path);
        }

        public static string BuildFolderOpResult(List<string> done, List<string> errors, string doneLabel)
        {
            var sb = new StringBuilder(256 + done.Count * 64 + errors.Count * 64);
            sb.Append('{');
            sb.Append('"').Append(doneLabel).Append("\":[");
            for (int i = 0; i < done.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(done[i])).Append('"');
            }
            sb.Append(']');
            sb.Append(",\"count\":").Append(done.Count);
            sb.Append(",\"errors\":[");
            for (int i = 0; i < errors.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(errors[i])).Append('"');
            }
            sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        public static string Esc(string s)
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
