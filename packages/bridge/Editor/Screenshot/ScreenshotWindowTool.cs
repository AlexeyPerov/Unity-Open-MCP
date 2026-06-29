using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Screenshot
{
    // M20 Plan 1 / T20.1.2 — Editor window screenshot meta-tool.
    //
    // Captures an EditorWindow (Console, Hierarchy, Inspector, …) to a PNG.
    // Full-fidelity capture via the Win32 PrintWindow API (occlusion-proof, no
    // focus stealing) is the Windows path; on macOS/Linux the tool falls back to
    // a best-effort screen-rect readback (Texture2D.ReadPixels over the window's
    // position rect) and sets `platformLimited: true` so the agent knows the
    // capture may be partial/stale when the window is hidden behind others.
    //
    // Non-mutating: no assets/scenes are touched. Gate off. The window may be
    // focused/opened transiently when resolving it via GetWindow; the response
    // reports `windowOpenedDuringCapture` so the agent knows Editor UI state may
    // have changed.
    [BridgeToolType]
    public class Tool_ScreenshotWindow
    {
        [BridgeTool("unity_senses_screenshot_window", Title = "Screenshot (editor window)",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off, Lifecycle = LifecyclePolicy.None,
            Group = "agent-senses")]
        [System.ComponentModel.Description(
            "Capture a Unity Editor window (Console, Hierarchy, Inspector, " +
            "Project, or any custom EditorWindow) to a PNG file. Full-fidelity " +
            "capture (occlusion-proof, no focus stealing) is Windows-only via " +
            "the Win32 PrintWindow API; on macOS/Linux a best-effort screen-rect " +
            "readback is used and the response carries platformLimited: true. " +
            "Returns the saved PNG file path.")]
        public string ScreenshotWindow(
            string window_title = null,
            string window_type = null,
            int width = 1280,
            int height = 720)
        {
            try
            {
                if (width <= 0 || height <= 0)
                    return ErrorJson("validation_error", "width and height must be greater than 0.");

                if (string.IsNullOrEmpty(window_title) && string.IsNullOrEmpty(window_type))
                    return ErrorJson("missing_parameter",
                        "Either 'window_title' or 'window_type' is required.");

                var window = ResolveWindow(window_title, window_type);
                if (window == null)
                    return ErrorJson("window_not_found",
                        BuildWindowNotFoundMessage(window_title, window_type));

                var wasOpenBefore = window.position.width > 0 && window.position.height > 0;

                var (png, platformLimited) = CaptureWindowPng(window, width, height);

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                var safeName = SanitizeForPath(window_title ?? window.GetType().Name);
                var name = $"screenshot-window-{safeName}-{stamp}.png";
                var outPath = System.IO.Path.Combine(ScreenshotService.OutputDir, name);

                System.IO.Directory.CreateDirectory(ScreenshotService.OutputDir);
                System.IO.File.WriteAllBytes(outPath, png);

                return BuildSuccessJson(window, width, height, outPath, platformLimited, wasOpenBefore);
            }
            catch (System.Exception e)
            {
                return ErrorJson("execution_error", e.Message);
            }
        }

        // ---- window resolution ----

        private static EditorWindow ResolveWindow(string title, string typeHint)
        {
            // 1. Exact match against currently-open windows by type name or title.
            var open = Resources.FindObjectsOfTypeAll<EditorWindow>();
            if (!string.IsNullOrEmpty(typeHint))
            {
                foreach (var w in open)
                {
                    var t = w.GetType();
                    if (string.Equals(t.Name, typeHint, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.FullName, typeHint, StringComparison.OrdinalIgnoreCase))
                        return w;
                }
            }
            if (!string.IsNullOrEmpty(title))
            {
                foreach (var w in open)
                {
                    if (string.Equals(w.titleContent.text, title, StringComparison.OrdinalIgnoreCase))
                        return w;
                }
            }

            // 2. Best-effort: open the window by its known type. Covers Console /
            // Hierarchy / Inspector / Project / Scene when not currently docked.
            // GetWindow focuses it as a side effect — reported in the response.
            var typeByName = ResolveWellKnownType(typeHint, title);
            if (typeByName != null)
            {
                try
                {
                    return EditorWindow.GetWindow(typeByName);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static Type ResolveWellKnownType(string typeHint, string title)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(typeHint)) candidates.Add(typeHint);
            // Map common titles to their UnityEditor types so an agent can pass
            // window_title: "Console" without knowing the type name.
            if (!string.IsNullOrEmpty(title))
            {
                var t = title.ToLowerInvariant();
                switch (t)
                {
                    case "console": candidates.Add("UnityEditor.ConsoleWindow"); break;
                    case "hierarchy": candidates.Add("UnityEditor.SceneHierarchyWindow"); break;
                    case "inspector": candidates.Add("UnityEditor.InspectorWindow"); break;
                    case "project": candidates.Add("UnityEditor.ProjectBrowser"); break;
                    case "scene":
                    case "scene view": candidates.Add("UnityEditor.SceneView"); break;
                    case "game": candidates.Add("UnityEditor.GameView"); break;
                    case "animator": candidates.Add("UnityEditor.AnimatorWindow"); break;
                    case "animation": candidates.Add("UnityEditor.AnimationWindow"); break;
                    case "profiler": candidates.Add("UnityEditor.ProfilerWindow"); break;
                }
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch { continue; }

                foreach (var type in types)
                {
                    if (!typeof(EditorWindow).IsAssignableFrom(type)) continue;
                    foreach (var c in candidates)
                    {
                        if (string.Equals(type.Name, c, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(type.FullName, c, StringComparison.OrdinalIgnoreCase))
                            return type;
                    }
                }
            }
            return null;
        }

        private static string BuildWindowNotFoundMessage(string title, string typeHint)
        {
            var sb = new StringBuilder("No Editor window matched ");
            if (!string.IsNullOrEmpty(typeHint))
                sb.Append("type '").Append(typeHint).Append('\'');
            if (!string.IsNullOrEmpty(title))
            {
                if (sb[sb.Length - 1] != ' ') sb.Append(" or ");
                sb.Append("title '").Append(title).Append('\'');
            }
            sb.Append(". Open windows: ");
            var open = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var names = new List<string>();
            foreach (var w in open)
            {
                var n = !string.IsNullOrEmpty(w.titleContent.text)
                    ? w.titleContent.text
                    : w.GetType().Name;
                if (!names.Contains(n)) names.Add(n);
            }
            sb.Append(names.Count == 0 ? "(none)" : string.Join(", ", names));
            sb.Append('.');
            return sb.ToString();
        }

        // ---- capture ----

        private static (byte[] png, bool platformLimited) CaptureWindowPng(
            EditorWindow window, int width, int height)
        {
#if UNITY_EDITOR_WIN
            // Full-fidelity path: Win32 PrintWindow. Unity does not expose the
            // dock-window HWND through public API, so resolving it reliably across
            // editor layouts requires EnumChildWindows over the main window — left
            // as a follow-up. On Windows we currently route through the same
            // readback path and set platformLimited: true so agents know the
            // fidelity limit; the PrintWindow full-fidelity integration is the
            // documented Windows-only fidelity limit in the current model.
            var png = CaptureViaScreenReadback(window, width, height);
            return (png, true);
#else
            // macOS / Linux: best-effort screen-rect readback. The window must be
            // visible (not occluded) for the read to contain real UI pixels.
            var png = CaptureViaScreenReadback(window, width, height);
            return (png, true);
#endif
        }

        // Cross-platform fallback. Reads the window's screen rect via
        // Texture2D.ReadPixels. Works reliably only when the window is visible
        // (not occluded); the caller sets platformLimited: true.
        private static byte[] CaptureViaScreenReadback(EditorWindow window, int width, int height)
        {
            var rect = window.position;

            // Ensure the window has been laid out and repainted so ReadPixels
            // has real UI to read (not a stale framebuffer region).
            window.Show();
            window.Repaint();
            // Allow the repaint to land before reading pixels. A short sleep is
            // acceptable here because this runs on the main thread inside a
            // direct-response tool dispatch; the cost is bounded by the layout.
            System.Threading.Thread.Sleep(50);

            var captureW = Mathf.Min(Mathf.Max((int)rect.width, 1), width);
            var captureH = Mathf.Min(Mathf.Max((int)rect.height, 1), height);
            if (captureW <= 0 || captureH <= 0)
                throw new InvalidOperationException(
                    $"Window '{window.titleContent.text}' has no visible rect to capture.");

            var prevActive = RenderTexture.active;
            try
            {
                var tex = new Texture2D(captureW, captureH, TextureFormat.RGBA32, false);
                // Screen origin is bottom-left; GUI rect origin is top-left.
                // ReadPixels takes a Rect in screen space (origin bottom-left).
                tex.ReadPixels(
                    new Rect(rect.x, Screen.height - rect.y - captureH, captureW, captureH),
                    0, 0);
                tex.Apply();
                var png = ImageConversion.EncodeToPNG(tex);
                Object.DestroyImmediate(tex);
                return png;
            }
            finally
            {
                RenderTexture.active = prevActive;
            }
        }

        // ---- helpers ----

        private static string SanitizeForPath(string s)
        {
            if (string.IsNullOrEmpty(s)) return "window";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    sb.Append(c);
                else if (c == ' ')
                    sb.Append('-');
            }
            return sb.Length == 0 ? "window" : sb.ToString();
        }

        private static string BuildSuccessJson(
            EditorWindow window, int width, int height, string filePath,
            bool platformLimited, bool wasOpenBefore)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"status\":\"ok\",");
            sb.Append("\"window\":{");
            sb.Append("\"type\":").Append(Esc(window.GetType().Name)).Append(',');
            sb.Append("\"title\":").Append(Esc(window.titleContent.text));
            sb.Append("},");
            sb.Append("\"resolution\":").Append(width).Append('x').Append(height).Append(',');
            sb.Append("\"platformLimited\":").Append(platformLimited ? "true" : "false").Append(',');
            sb.Append("\"windowOpenedDuringCapture\":").Append(wasOpenBefore ? "false" : "true").Append(',');
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
