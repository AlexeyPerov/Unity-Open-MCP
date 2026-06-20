using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Screenshot
{
    // M10 Plan 2 T3.1 — Screenshot meta-tool (typed/registry pattern).
    //
    // Gives the agent eyes: capture the Scene view, Game view, or an isolated
    // composite of a single GameObject. Returns the saved file path so MCP
    // servers can pass a reference instead of a huge base64 blob.
    //
    // Non-mutating: screenshots do not touch assets/scenes. Isolated mode
    // changes scene state transiently (creates a temp camera) but restores
    // everything in finally blocks.
    [BridgeToolType]
    public class Tool_Screenshot
    {
        [BridgeTool("unity_senses_screenshot", Title = "Screenshot",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off, Lifecycle = LifecyclePolicy.None)]
        [System.ComponentModel.Description(
            "Capture a screenshot from the Scene view, Game view, or an isolated " +
            "2x2 composite of a single GameObject. Returns the saved PNG file path.")]
        public string Screenshot(
            string view = "scene",
            int width = 1280,
            int height = 720,
            string object_path = null,
            string background = "skybox")
        {
            view = (view ?? "scene").ToLowerInvariant();

            try
            {
                string filePath;

                switch (view)
                {
                    case "scene":
                        filePath = ScreenshotService.CaptureSceneView(width, height);
                        break;
                    case "game":
                        filePath = ScreenshotService.CaptureGameView(width, height);
                        break;
                    case "isolated":
                        if (string.IsNullOrEmpty(object_path))
                            return ErrorJson("missing_parameter",
                                "Isolated mode requires 'object_path' (hierarchy path, e.g. \"Player\" or \"Enemies/Goblin\").");

                        var target = FindByPath(object_path);
                        if (target == null)
                            return ErrorJson("asset_not_found",
                                $"No active GameObject found at path '{object_path}'.");

                        filePath = ScreenshotService.CaptureIsolated(target, width, height, background ?? "skybox");
                        break;
                    default:
                        return ErrorJson("validation_error",
                            $"Unknown view '{view}'. Use 'scene', 'game', or 'isolated'.");
                }

                return BuildSuccessJson(view, width, height, filePath);
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
            // FindObjectsByType<T>(FindObjectsInactive) — the sort-mode overloads
            // are deprecated in Unity 6000.4+; None-sorted is the default anyway.
            var roots = Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude);

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

        private static string BuildSuccessJson(string view, int width, int height, string filePath)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"status\":\"ok\",");
            sb.Append("\"view\":").Append(Esc(view)).Append(',');
            if (view == "isolated")
                sb.Append("\"composite\":\"2x2 (Front/Right/Back/Top)\",")
                  .Append("\"quadrantSize\":").Append(width).Append('x').Append(height).Append(',')
                  .Append("\"fullSize\":").Append(width * 2).Append('x').Append(height * 2).Append(',');
            else
                sb.Append("\"resolution\":").Append(width).Append('x').Append(height).Append(',');
            sb.Append("\"filePath\":").Append(Esc(filePath));
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
