using System;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Screenshot
{
    // Visual regression compare — capture a named reference snapshot from a
    // view, then compare later captures against it.
    //
    //   unity_senses_visual_compare
    //     action: save | compare | list | delete
    //
    // 'save'    — capture a view (scene / game / isolated), store it as a named
    //             reference under ~/.unity-open-mcp/screenshots/references/.
    // 'compare' — capture fresh, diff against the named reference, return
    //             pixelDiffPercent / mismatchedPixels / perceptualDistance /
    //             match, plus an inline diff image (mismatched pixels in red
    //             over the current frame) when the diff is non-zero.
    // 'list'    — enumerate saved references.
    // 'delete'  — remove a named reference.
    //
    // Non-mutating w.r.t. project/scene state (isolated mode changes scene
    // state transiently but restores it in finally blocks, same as
    // capture_inline). 'save' writes to a user-level screenshots dir, not the
    // project, so it stays gate-free.
    //
    // The 'compare' response carries an `inlineImage` base64 PNG (the diff
    // image) so the MCP server unwraps it into an MCP image content block —
    // same mechanism capture_inline uses for its capture.
    [BridgeToolType]
    public class Tool_VisualCompare
    {
        [BridgeTool("unity_senses_visual_compare", Title = "Visual Compare",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off, Lifecycle = LifecyclePolicy.None,
            Group = "agent-senses")]
        [System.ComponentModel.Description(
            "Visual regression compare. Capture a named reference snapshot " +
            "(scene / game / isolated view), then compare later captures " +
            "against it. Returns pixelDiffPercent, mismatchedPixels, " +
            "perceptualDistance (8x8 aHash Hamming distance), and match " +
            "(true when diff <= sensitivity). On a non-zero diff, returns an " +
            "inline diff image (mismatched pixels highlighted red over the " +
            "current frame). Actions: save, compare, list, delete.")]
        public string VisualCompare(
            string action = "",
            string name = null,
            string view = "game",
            int width = 1280,
            int height = 720,
            string object_path = null,
            string background = "skybox",
            float sensitivity = 0.01f,
            bool include_diff_image = true)
        {
            var act = (action ?? "").Trim().ToLowerInvariant();
            try
            {
                switch (act)
                {
                    case "save":
                        return DoSave(name, view, width, height, object_path, background);
                    case "compare":
                        return DoCompare(name, view, width, height, object_path, background,
                            sensitivity, include_diff_image);
                    case "list":
                        return DoList();
                    case "delete":
                        return DoDelete(name);
                    default:
                        return ErrorJson("unknown_action",
                            "Unknown or missing 'action'. Expected one of: save, compare, list, delete.");
                }
            }
            catch (Exception e)
            {
                return ErrorJson("execution_error", e.Message);
            }
        }

        // ============================ save ============================

        private string DoSave(string name, string view, int width, int height,
            string objectPath, string background)
        {
            if (string.IsNullOrWhiteSpace(name))
                return ErrorJson("missing_parameter", "'name' is required for save.");

            byte[] png;
            string err = Capture(view, width, height, objectPath, background, out png);
            if (err != null) return err;

            var info = ImageCompareService.SaveReference(name, png);
            return BuildSaveJson(info);
        }

        // ============================ compare ============================

        private string DoCompare(string name, string view, int width, int height,
            string objectPath, string background, float sensitivity, bool includeDiffImage)
        {
            if (string.IsNullOrWhiteSpace(name))
                return ErrorJson("missing_parameter", "'name' is required for compare.");

            var refPng = ImageCompareService.LoadReferencePng(name);
            if (refPng == null)
                return ErrorJson("reference_not_found",
                    $"No reference snapshot named '{name}'. Run action:'save' first.");

            byte[] curPng;
            string err = Capture(view, width, height, objectPath, background, out curPng);
            if (err != null) return err;

            float sens = sensitivity < 0f ? 0f : (sensitivity > 1f ? 1f : sensitivity);
            var result = ImageCompareService.Compare(refPng, curPng, sens, includeDiffImage);
            return BuildCompareJson(name, view, width, height, result, includeDiffImage);
        }

        // ============================ list ============================

        private string DoList()
        {
            var refs = ImageCompareService.ListReferences();
            var sb = new StringBuilder(256 + refs.Length * 96);
            sb.Append('{');
            sb.Append("\"action\":\"list\",");
            sb.Append("\"count\":").Append(refs.Length).Append(',');
            sb.Append("\"references\":[");
            for (int i = 0; i < refs.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append("\"name\":").Append(Esc(refs[i].Name)).Append(',');
                sb.Append("\"width\":").Append(refs[i].Width).Append(',');
                sb.Append("\"height\":").Append(refs[i].Height).Append(',');
                sb.Append("\"aHash\":\"").Append(refs[i].AHash.ToString("X16", CultureInfo.InvariantCulture)).Append("\",");
                sb.Append("\"capturedAtUtc\":").Append(Esc(refs[i].CapturedAtUtc ?? ""));
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ============================ delete ============================

        private string DoDelete(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return ErrorJson("missing_parameter", "'name' is required for delete.");
            bool existed = ImageCompareService.DeleteReference(name);
            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"action\":\"delete\",");
            sb.Append("\"name\":").Append(Esc(name)).Append(',');
            sb.Append("\"deleted\":").Append(existed ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        // ============================ capture dispatch ============================

        // Resolves the view → PNG bytes via ScreenshotService. Returns null on
        // success (png out), or an error-json string on failure.
        private static string Capture(string view, int width, int height,
            string objectPath, string background, out byte[] png)
        {
            png = null;
            view = (view ?? "game").ToLowerInvariant();
            switch (view)
            {
                case "scene":
                    png = ScreenshotService.CaptureSceneViewBytes(width, height);
                    return null;
                case "game":
                    png = ScreenshotService.CaptureGameViewBytes(width, height);
                    return null;
                case "isolated":
                    if (string.IsNullOrEmpty(objectPath))
                        return ErrorJson("missing_parameter",
                            "Isolated mode requires 'object_path' (hierarchy path, e.g. \"Player\" or \"Enemies/Goblin\").");
                    var target = FindByPath(objectPath);
                    if (target == null)
                        return ErrorJson("asset_not_found",
                            $"No active GameObject found at path '{objectPath}'.");
                    png = ScreenshotService.CaptureIsolatedBytes(target, width, height, background ?? "skybox");
                    return null;
                default:
                    return ErrorJson("validation_error",
                        $"Unknown view '{view}'. Use 'scene', 'game', or 'isolated'.");
            }
        }

        // ============================ response builders ============================

        private static string BuildSaveJson(ImageCompareService.ReferenceInfo info)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"action\":\"save\",");
            sb.Append("\"status\":\"ok\",");
            sb.Append("\"name\":").Append(Esc(info.Name)).Append(',');
            sb.Append("\"resolution\":\"").Append(info.Width).Append('x').Append(info.Height).Append("\",");
            sb.Append("\"aHash\":\"").Append(info.AHash.ToString("X16", CultureInfo.InvariantCulture)).Append("\",");
            sb.Append("\"capturedAtUtc\":").Append(Esc(info.CapturedAtUtc ?? ""));
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildCompareJson(string name, string view, int width, int height,
            ImageCompareService.CompareResult result, bool includeDiffImage)
        {
            var sb = new StringBuilder(384 + (result.DiffImageBytes?.Length ?? 0) * 2);
            sb.Append('{');
            sb.Append("\"action\":\"compare\",");
            sb.Append("\"name\":").Append(Esc(name)).Append(',');
            sb.Append("\"view\":").Append(Esc(view)).Append(',');
            sb.Append("\"resolution\":\"").Append(width).Append('x').Append(height).Append("\",");
            sb.Append("\"match\":").Append(result.Match ? "true" : "false").Append(',');
            sb.Append("\"pixelDiffPercent\":").Append(Num(result.PixelDiffPercent)).Append(',');
            sb.Append("\"mismatchedPixels\":").Append(result.MismatchedPixels).Append(',');
            sb.Append("\"totalPixels\":").Append(result.TotalPixels).Append(',');
            sb.Append("\"perceptualDistance\":").Append(result.PerceptualDistance);

            if (includeDiffImage && result.DiffImageBytes != null && result.DiffImageBytes.Length > 0)
            {
                sb.Append(',');
                sb.Append("\"mimeType\":\"image/png\",");
                sb.Append("\"byteLength\":").Append(result.DiffImageBytes.Length).Append(',');
                sb.Append("\"inlineImage\":\"").Append(Convert.ToBase64String(result.DiffImageBytes)).Append('"');
            }
            sb.Append('}');
            return sb.ToString();
        }

        // ============================ helpers ============================

        private static GameObject FindByPath(string path)
        {
            var parts = path.Split('/');
            var roots = SceneQuery.FindActiveTransforms();

            foreach (var root in roots)
            {
                if (root.gameObject.name == parts[0])
                {
                    var current = root.gameObject;
                    bool match = true;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var child = current.transform.Find(parts[i]);
                        if (child == null) { match = false; break; }
                        current = child.gameObject;
                    }
                    if (match) return current;
                }
            }

            foreach (var root in roots)
            {
                if (root.gameObject.name == path || root.gameObject.name == parts[parts.Length - 1])
                    return root.gameObject;
            }

            return null;
        }

        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        private static string ErrorJson(string code, string message)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        private static string Esc(string s)
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
    }
}
