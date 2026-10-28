using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Screenshot
{
    // M20 Plan 1 / T20.1.1 — inline-image capture meta-tool.
    //
    // Same capture targets as unity_senses_screenshot (scene / game / isolated),
    // but returns the PNG as a base64 `inlineImage` field in the JSON envelope
    // instead of writing a temp file. The MCP server unwraps `inlineImage` into
    // an MCP image content block ({ type: "image", data, mimeType: "image/png" })
    // so agents that don't read the filesystem still get a viewable image.
    //
    // Non-mutating: isolated mode changes scene state transiently (creates a
    // temp camera) but restores everything in finally blocks.
    [BridgeToolType]
    public class Tool_CaptureInline
    {
        [BridgeTool("unity_senses_capture_inline", Title = "Capture (inline image)",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off, Lifecycle = LifecyclePolicy.None,
            Group = "agent-senses")]
        [System.ComponentModel.Description(
            "Capture a screenshot from the Scene view, Game view (Camera.main), the " +
            "composed player frame (native game resolution by default), or an " +
            "isolated 2x2 composite of a single GameObject and return it as an " +
            "inline base64 PNG image (no temp file written). Useful for agents " +
            "that don't read the filesystem.")]
        public string CaptureInline(
            string view = "scene",
            int? width = null,
            int? height = null,
            string object_path = null,
            string background = "skybox",
            string size = null,
            string overlay = null)
        {
            view = (view ?? "scene").ToLowerInvariant();
            var argError = ScreenshotService.ValidateFrameArgs(view, width, height, size, overlay);
            if (argError != null) return ErrorJson("validation_error", argError);
            // scene / game / isolated default each missing axis to 1280 / 720; composed defaults to native.
            int w = width ?? 1280, h = height ?? 720;

            try
            {
                byte[] png;

                switch (view)
                {
                    case "scene":
                        png = ScreenshotService.CaptureSceneViewBytes(w, h);
                        break;
                    case "game":
                        if (size == "native" && !ScreenshotService.TryNativeSize(out w, out h))
                            return ErrorJson("native_size_unavailable", "The Game view reports no size yet; open the Game view or pass width/height.");
                        return BuildFrameJson(view, ScreenshotService.CaptureGameViewFrame(w, h));
                    // Composed view: the full player frame (all cameras + Overlay
                    // UI) at native size unless width/height are passed.
                    case "composed":
                        return BuildFrameJson(view, ScreenshotService.CaptureComposed(width, height, overlay == "safe_area"));
                    case "isolated":
                        if (string.IsNullOrEmpty(object_path))
                            return ErrorJson("missing_parameter",
                                "Isolated mode requires 'object_path' (hierarchy path, e.g. \"Player\" or \"Enemies/Goblin\").");

                        var target = FindByPath(object_path);
                        if (target == null)
                            return ErrorJson("asset_not_found",
                                $"No active GameObject found at path '{object_path}'.");

                        png = ScreenshotService.CaptureIsolatedBytes(target, w, h, background ?? "skybox");
                        break;
                    default:
                        return ErrorJson("validation_error",
                            $"Unknown view '{view}'. Use 'scene', 'game', 'composed', or 'isolated'.");
                }

                return BuildSuccessJson(view, w, h, png);
            }
            catch (CaptureRefusedException e)
            {
                return ErrorJson(e.Code, e.Message);
            }
            catch (Exception e)
            {
                return ErrorJson("execution_error", e.Message);
            }
        }

        private static GameObject FindByPath(string path)
        {
            // Try as hierarchy path first (slash-separated from root).
            var parts = path.Split('/');
            // SceneQuery centralizes the FindObjectsByType (2023.1+) /
            // FindObjectsOfType (pre-2023.1) version dance — see SceneQuery.cs.
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

            // Fallback: name-only search.
            foreach (var root in roots)
            {
                if (root.gameObject.name == path || root.gameObject.name == parts[parts.Length - 1])
                    return root.gameObject;
            }

            return null;
        }

        private static string BuildSuccessJson(string view, int width, int height, byte[] png)
        {
            var sb = new StringBuilder(png.Length * 2 + 256);
            sb.Append('{');
            sb.Append("\"status\":\"ok\",");
            sb.Append("\"view\":").Append(Esc(view)).Append(',');
            if (view == "isolated")
                sb.Append("\"composite\":\"2x2 (Front/Right/Back/Top)\",")
                  .Append("\"quadrantSize\":\"").Append(width).Append('x').Append(height).Append("\",")
                  .Append("\"fullSize\":\"").Append(width * 2).Append('x').Append(height * 2).Append("\",");
            else
                sb.Append("\"resolution\":\"").Append(width).Append('x').Append(height).Append("\",");
            sb.Append("\"mimeType\":\"image/png\",");
            sb.Append("\"byteLength\":").Append(png.Length).Append(',');
            sb.Append("\"inlineImage\":\"").Append(Convert.ToBase64String(png)).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildFrameJson(string view, FrameCapture capture)
        {
            var sb = new StringBuilder(capture.Png.Length * 2 + 512);
            sb.Append("{\"status\":\"ok\",\"view\":").Append(Esc(view));
            sb.Append(",\"resolution\":\"").Append(capture.Width).Append('x').Append(capture.Height).Append('"');
            ScreenshotService.AppendCaptureFields(sb, capture);
            sb.Append(",\"mimeType\":\"image/png\"");
            sb.Append(",\"byteLength\":").Append(capture.Png.Length);
            sb.Append(",\"inlineImage\":\"").Append(Convert.ToBase64String(capture.Png)).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

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
