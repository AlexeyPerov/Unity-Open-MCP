using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Screenshot
{
    // M20 Plan 1 / T20.1.1 — arbitrary-pose screenshot meta-tool.
    //
    // Gives the agent a free camera: render from any world-space pose without
    // moving the scene/game camera. A transient Camera is positioned at the
    // requested pose, renders to a RenderTexture, and is destroyed — the live
    // scene camera is never touched (restored in finally blocks inside
    // ScreenshotService). Returns the saved PNG file path.
    //
    // Non-mutating: no assets/scenes are modified. Gate off.
    [BridgeToolType]
    public class Tool_ScreenshotCamera
    {
        [BridgeTool("unity_senses_screenshot_camera", Title = "Screenshot (camera pose)",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off, Lifecycle = LifecyclePolicy.None,
            Group = "agent-senses")]
        [System.ComponentModel.Description(
            "Render a screenshot from an arbitrary world-space camera pose " +
            "(position + rotation in degrees + field of view) without moving " +
            "the scene/game camera. Returns the saved PNG file path.")]
        public string ScreenshotCamera(
            string position = "0,0,0",
            string rotation = "0,0,0",
            float fov = 60f,
            int width = 1280,
            int height = 720,
            string background = "skybox")
        {
            try
            {
                if (width <= 0 || height <= 0)
                    return ErrorJson("validation_error", "width and height must be greater than 0.");

                var pos = ParseVector3(position, Vector3.zero);
                var rot = ParseVector3(rotation, Vector3.zero);
                if (fov <= 0f || fov >= 180f)
                    return ErrorJson("validation_error",
                        "fov must be in the open interval (0, 180) degrees.");

                var filePath = ScreenshotService.CaptureFromPose(
                    pos, rot, fov, width, height, background ?? "skybox");

                return BuildSuccessJson(pos, rot, fov, width, height, filePath);
            }
            catch (System.Exception e)
            {
                return ErrorJson("execution_error", e.Message);
            }
        }

        private static Vector3 ParseVector3(string raw, Vector3 fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            var parts = raw.Split(',');
            if (parts.Length < 3) return fallback;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return fallback;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return fallback;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z)) return fallback;
            return new Vector3(x, y, z);
        }

        private static string BuildSuccessJson(
            Vector3 position, Vector3 rotation, float fov,
            int width, int height, string filePath)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"status\":\"ok\",");
            sb.Append("\"camera\":{");
            sb.Append("\"position\":[").Append(position.x).Append(',').Append(position.y).Append(',').Append(position.z).Append("],");
            sb.Append("\"rotation\":[").Append(rotation.x).Append(',').Append(rotation.y).Append(',').Append(rotation.z).Append("],");
            sb.Append("\"fov\":").Append(fov);
            sb.Append("},");
            sb.Append("\"resolution\":\"").Append(width).Append('x').Append(height).Append("\",");
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
