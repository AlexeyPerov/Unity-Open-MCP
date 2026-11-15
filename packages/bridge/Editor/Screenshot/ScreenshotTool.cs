using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Screenshot
{
    // M10 Plan 2 T3.1 — Screenshot meta-tool (typed/registry pattern).
    //
    // Gives the agent eyes: capture the Scene view, Game view, the composed
    // player frame, an isolated composite of a single GameObject, or a UI
    // prefab rendered in a preview scene (view "prefab", PrefabView.cs).
    // Returns the saved file path so MCP servers can pass a reference instead
    // of a huge base64 blob.
    //
    // Non-mutating: screenshots do not touch assets/scenes. Isolated mode
    // changes scene state transiently (creates a temp camera) but restores
    // everything in finally blocks; the prefab view builds and closes its own
    // preview scene.
    [BridgeToolType]
    public class Tool_Screenshot
    {
        [BridgeTool("unity_senses_screenshot", Title = "Screenshot",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off, Lifecycle = LifecyclePolicy.None)]
        [System.ComponentModel.Description(
            "Capture a screenshot from the Scene view, Game view (Camera.main), the composed " +
            "player frame (native game resolution by default; width/height of another aspect are " +
            "padded, never stretched; optional safe-area overlay), an isolated 2x2 composite of " +
            "a single GameObject, or a UI prefab / canvas rendered in a preview scene in edit mode " +
            "(view prefab). Returns the saved PNG file path and the device/resolution it was taken at.")]
        public string Screenshot(
            string view = "scene",
            int? width = null,
            int? height = null,
            string object_path = null,
            string background = "skybox",
            string size = null,
            string overlay = null,
            string region = null,
            float? scale = null,
            int? max_edge = null,
            string save_to = null,
            string asset_path = null,
            string game_object_path = null,
            string device = null,
            string orientation = null,
            string size_label = null,
            string canvas_scaler = null,
            string scaler_from = null)
        {
            view = (view ?? "scene").ToLowerInvariant();
            var argError = ScreenshotService.ValidateFrameArgs(view, width, height, size, overlay);
            if (argError != null) return ErrorJson("validation_error", argError);
            var prefab = new PrefabViewRequest
            {
                AssetPath = asset_path, GameObjectPath = game_object_path, Width = width, Height = height,
                Device = device, Orientation = orientation, SizeLabel = size_label,
                CanvasScaler = canvas_scaler, ScalerFrom = scaler_from,
                SafeAreaOverlay = overlay == "safe_area", Transparent = background == "transparent",
            };
            if (view == "prefab")
            {
                var prefabError = PrefabView.ValidateArgs(prefab, out var prefabCode);
                if (prefabError != null) return ErrorJson(prefabCode, prefabError);
            }
            else if (PrefabView.HasPrefabArgs(prefab))
                return ErrorJson("validation_error",
                    "asset_path, game_object_path, device, orientation, size_label, canvas_scaler and scaler_from apply to view \"prefab\"; pass view: \"prefab\".");
            var postError = ImagePostProcess.Parse(region, scale, max_edge, save_to, out var post);
            if (postError != null) return ErrorJson("invalid_arguments", postError);
            // scene / game / isolated default each missing axis to 1280 / 720; composed defaults to native.
            int w = width ?? 1280, h = height ?? 720;

            try
            {
                string filePath;

                switch (view)
                {
                    case "scene":
                        filePath = ScreenshotService.CaptureSceneView(w, h);
                        break;
                    case "game":
                        if (size == "native" && !ScreenshotService.TryNativeSize(out w, out h))
                            return ErrorJson("native_size_unavailable", "The Game view reports no size yet; open the Game view or pass width/height.");
                        var game = ScreenshotService.CaptureGameViewFrame(w, h);
                        game.FilePath = ScreenshotService.SavePng(game.Png, "game");
                        return BuildFrameJson(view, game, post);
                    // The player's composite frame: every camera by depth +
                    // Screen-Space Overlay canvases (in play mode, the play-mode
                    // view's own render target). Native size unless width/height
                    // are passed; another aspect is padded, never stretched.
                    case "composed":
                        return BuildFrameJson(view, ScreenshotService.CaptureComposedView(width, height, overlay == "safe_area"), post);
                    // A UI prefab (or a canvas of a loaded scene) cloned into a
                    // preview scene and rendered through a Screen Space - Camera
                    // canvas: no project scene is loaded, dirtied or changed.
                    case "prefab":
                        var rendered = PrefabView.Capture(prefab);
                        rendered.FilePath = ScreenshotService.SavePng(rendered.Png, "prefab");
                        return BuildFrameJson(view, rendered, post);
                    case "isolated":
                        if (string.IsNullOrEmpty(object_path))
                            return ErrorJson("missing_parameter",
                                "Isolated mode requires 'object_path' (hierarchy path, e.g. \"Player\" or \"Enemies/Goblin\").");

                        var target = FindByPath(object_path);
                        if (target == null)
                            return ErrorJson("asset_not_found",
                                $"No active GameObject found at path '{object_path}'.");

                        filePath = ScreenshotService.CaptureIsolated(target, w, h, background ?? "skybox");
                        break;
                    default:
                        return ErrorJson("validation_error",
                            $"Unknown view '{view}'. Use 'scene', 'game', 'composed', 'isolated', or 'prefab'.");
                }

                return BuildSuccessJson(view, w, h, filePath, post);
            }
            catch (CaptureRefusedException e)
            {
                return ErrorJson(e.Code, e.Message, e.DetailJson);
            }
            catch (System.Exception e)
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

        // Crop / downscale the saved file in place, then copy it to save_to.
        // Returns the save_to path (null when none was asked for).
        private static string PostProcessFile(string filePath, ImagePostProcess post)
        {
            if (post == null || !post.Any) return null;
            if (post.ChangesPixels)
                System.IO.File.WriteAllBytes(filePath, post.Apply(System.IO.File.ReadAllBytes(filePath)));
            if (string.IsNullOrEmpty(post.SaveTo)) return null;
            return post.Save(filePath, ImagePostProcess.ProjectRoot);
        }

        private static string BuildSuccessJson(string view, int width, int height, string filePath, ImagePostProcess post = null)
        {
            var savedTo = PostProcessFile(filePath, post);
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"status\":\"ok\",");
            sb.Append("\"view\":").Append(Esc(view)).Append(',');
            if (view == "isolated")
                sb.Append("\"composite\":\"2x2 (Front/Right/Back/Top)\",")
                  .Append("\"quadrantSize\":\"").Append(width).Append('x').Append(height).Append("\",")
                  .Append("\"fullSize\":\"").Append(width * 2).Append('x').Append(height * 2).Append("\",");
            else
                sb.Append("\"resolution\":\"").Append(width).Append('x').Append(height).Append("\",");
            sb.Append("\"filePath\":").Append(Esc(filePath));
            post?.AppendJson(sb, savedTo);
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildFrameJson(string view, FrameCapture capture, ImagePostProcess post = null)
        {
            var savedTo = PostProcessFile(capture.FilePath, post);
            var sb = new StringBuilder(512);
            sb.Append("{\"status\":\"ok\",\"view\":").Append(Esc(view));
            sb.Append(",\"resolution\":\"").Append(capture.Width).Append('x').Append(capture.Height).Append('"');
            ScreenshotService.AppendCaptureFields(sb, capture);
            sb.Append(",\"filePath\":").Append(Esc(capture.FilePath));
            post?.AppendJson(sb, savedTo);
            sb.Append('}');
            return sb.ToString();
        }

        // `detail`: a JSON object whose members join the error (candidates, labels, …).
        private static string ErrorJson(string code, string message, string detail = null)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            detail = detail?.Trim();
            if (!string.IsNullOrEmpty(detail) && detail.Length > 2 && detail[0] == '{')
                sb.Append(',').Append(detail, 1, detail.Length - 2);
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
