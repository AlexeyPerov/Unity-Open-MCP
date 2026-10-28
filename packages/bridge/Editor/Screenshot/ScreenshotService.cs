using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.Config;
using UnityOpenMcpBridge.ObjectRefs;
using UnityOpenMcpBridge.Playtest;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Screenshot
{
    // Rendering logic for the three screenshot modes.
    //
    // scene    — capture the last active Scene view camera.
    // game     — capture the main (game) camera.
    // isolated — render one GameObject in a 2x2 composite (Front/Right/Back/Top)
    //            with layer culling and background choice. Scene state is restored
    //            in finally blocks so the editor is left untouched.
    static class ScreenshotService
    {
        public static readonly string OutputDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            BridgeConstants.SettingsDirName, "screenshots");

        private const TextureFormat CaptureFormat = TextureFormat.RGBA32;
        private const bool CaptureMipChain = false;

        // ---- public API ----

        public static string CaptureSceneView(int width, int height)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
                throw new InvalidOperationException("No active Scene view found.");

            return WritePng(RenderCameraToPng(sv.camera, width, height), PathStamp("scene"));
        }

        // M20 Plan 1 / T20.1.1 — byte-returning capture for unity_senses_capture_inline.
        // Mirrors the file-returning capture but skips the disk write so an MCP
        // client that doesn't read the filesystem can still receive the image as
        // an inline base64 content block.
        public static byte[] CaptureSceneViewBytes(int width, int height)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
                throw new InvalidOperationException("No active Scene view found.");

            return RenderCameraToPng(sv.camera, width, height);
        }

        public static string SavePng(byte[] png, string label) => WritePng(png, PathStamp(label));

        public static string CaptureGameView(int width, int height)
        {
            var cam = ResolveMainCamera();
            return WritePng(RenderCameraToPng(cam, width, height), PathStamp("game"));
        }

        public static byte[] CaptureGameViewBytes(int width, int height)
        {
            var cam = ResolveMainCamera();
            return RenderCameraToPng(cam, width, height);
        }

        // feedback #5 (2026-08-07) — the player's composite frame: every enabled
        // camera rendered by ascending depth into one target (so depth-stacked
        // cameras + clear-flags compose as they would on screen), PLUS Screen-
        // Space Overlay canvases captured via ScreenCapture in play mode. The
        // existing `game` view only renders Camera.main, so Overlay UI and multi-
        // camera compositions were silently absent — a frame that looked valid
        // but missed the UI an agent was asked to verify.
        //
        // play mode: ScreenCapture.CaptureScreenshotAsTexture() reads the actual
        // rendered Game view backbuffer (all cameras by depth + every Canvas,
        // including Screen-Space Overlay that no camera renders). edit mode:
        // Overlay canvases do not render at all (no player loop), so we fall back
        // to depth-ordered camera compositing — still strictly better than
        // Camera.main-only for multi-camera scenes.
        public static FrameCapture CaptureComposedView(int? width, int? height, bool safeAreaOverlay)
        {
            var capture = CaptureComposed(width, height, safeAreaOverlay);
            capture.FilePath = WritePng(capture.Png, PathStamp("composed"));
            return capture;
        }

        // The game's composed frame (all cameras + Screen Space Overlay UI).
        //
        // Size: without width/height the frame comes back at the game's own
        // resolution (the Game view size or the simulated device screen) — no
        // scaling, no Editor letterbox. With width/height of another size the
        // frame is scaled to fit and padded (black) — never stretched — and the
        // result reports `scaled_from` + `content_rect`.
        //
        // play mode: the main play-mode view's render target — exactly the frame
        // the game rendered (Overlay canvases included; no Editor chrome; no
        // Device Simulator bezel). ScreenCapture.CaptureScreenshotAsTexture()
        // called from the bridge dispatch reads whichever Editor panel is current
        // (tabs, toolbar, the Simulator's device frame), which is why it is only
        // the fallback when the view's render target is unreachable.
        // edit mode: no player loop, so Overlay canvases do not render; every
        // enabled camera is composited by depth at the game's resolution.
        public static FrameCapture CaptureComposed(int? width, int? height, bool safeAreaOverlay)
        {
            var context = DescribeGameView();
            Texture2D frame = null;
            try
            {
                string source;
                if (Application.isPlaying)
                {
                    var rt = context.View != null ? GameViewAdapter.TargetTexture(context.View) : null;
                    if (rt != null)
                    {
                        frame = ReadRenderTarget(rt, SystemInfo.graphicsUVStartsAtTop);
                        source = "play_mode_view";
                    }
                    else
                    {
                        frame = ScreenCapture.CaptureScreenshotAsTexture();
                        source = "screen_capture";
                    }
                }
                else
                {
                    int nw = context.ScreenWidth > 0 ? context.ScreenWidth : 1280;
                    int nh = context.ScreenHeight > 0 ? context.ScreenHeight : 720;
                    frame = RenderAllCamerasByDepthTexture(nw, nh);
                    source = "cameras";
                }
                if (frame == null)
                    throw new InvalidOperationException("Captured frame was empty.");

                var capture = new FrameCapture
                {
                    Source = source,
                    NativeWidth = frame.width,
                    NativeHeight = frame.height,
                    Context = context,
                };
                if (safeAreaOverlay)
                {
                    var safe = ScaleRect(context.SafeArea, context.ScreenWidth, context.ScreenHeight, frame.width, frame.height);
                    DrawSafeAreaOverlay(frame, safe);
                    capture.SafeAreaOverlay = true;
                    capture.SafeArea = context.SafeArea;
                }
                FinishCapture(capture, frame, width, height);
                return capture;
            }
            finally
            {
                if (frame != null) Object.DestroyImmediate(frame);
            }
        }

        // Camera.main capture (`view: game`) at native size when size is native.
        public static FrameCapture CaptureGameViewFrame(int width, int height)
        {
            var context = DescribeGameView();
            var png = RenderCameraToPng(ResolveMainCamera(), width, height);
            return new FrameCapture
            {
                Png = png,
                Width = width,
                Height = height,
                NativeWidth = width,
                NativeHeight = height,
                // Camera.main renders straight at the requested size: native when
                // that is the game's resolution, otherwise "requested" (no fit).
                Native = width == context.ScreenWidth && height == context.ScreenHeight,
                Source = "main_camera",
                Context = context,
            };
        }

        // The game's resolution for `size: native` (the simulated screen in the
        // Device Simulator, the Game view render size otherwise).
        public static bool TryNativeSize(out int width, out int height)
        {
            var context = DescribeGameView();
            width = context.ScreenWidth;
            height = context.ScreenHeight;
            return width > 0 && height > 0;
        }

        // Encode `frame` as is (native) or fitted into width×height (letterbox).
        private static void FinishCapture(FrameCapture capture, Texture2D frame, int? width, int? height)
        {
            if (!width.HasValue || !height.HasValue || (width.Value == frame.width && height.Value == frame.height))
            {
                capture.Png = ImageConversion.EncodeToPNG(frame);
                capture.Width = frame.width;
                capture.Height = frame.height;
                capture.Native = true;
                return;
            }
            var fit = FitRect(frame.width, frame.height, width.Value, height.Value);
            var canvas = Letterbox(frame, width.Value, height.Value, fit);
            try
            {
                capture.Png = ImageConversion.EncodeToPNG(canvas);
                capture.Width = width.Value;
                capture.Height = height.Value;
                capture.Native = false;
                capture.Content = fit;
            }
            finally
            {
                Object.DestroyImmediate(canvas);
            }
        }

        // Where a srcW×srcH frame lands when fitted (aspect kept) into dstW×dstH,
        // centred: [x, y, w, h] with a TOP-LEFT origin (image convention).
        internal static RectInt FitRect(int srcW, int srcH, int dstW, int dstH)
        {
            if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0) return new RectInt(0, 0, Mathf.Max(dstW, 0), Mathf.Max(dstH, 0));
            double scale = Math.Min((double)dstW / srcW, (double)dstH / srcH);
            int w = Math.Max(1, Math.Min(dstW, (int)Math.Round(srcW * scale, MidpointRounding.AwayFromZero)));
            int h = Math.Max(1, Math.Min(dstH, (int)Math.Round(srcH * scale, MidpointRounding.AwayFromZero)));
            return new RectInt((dstW - w) / 2, (dstH - h) / 2, w, h);
        }

        // Scale `frame` into `fit` on a black dstW×dstH canvas.
        private static Texture2D Letterbox(Texture2D frame, int dstW, int dstH, RectInt fit)
        {
            var canvas = new Texture2D(dstW, dstH, CaptureFormat, CaptureMipChain);
            var black = new Color32[dstW * dstH];
            for (int i = 0; i < black.Length; i++) black[i] = new Color32(0, 0, 0, 255);
            canvas.SetPixels32(black);
            var rt = RenderTexture.GetTemporary(fit.width, fit.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var prevActive = RenderTexture.active;
            try
            {
                frame.filterMode = FilterMode.Bilinear;
                Graphics.Blit(frame, rt);
                RenderTexture.active = rt;
                // Texture rows run bottom-up: the top-left fit.y becomes a bottom offset.
                int bottom = dstH - fit.y - fit.height;
                canvas.ReadPixels(new Rect(0, 0, fit.width, fit.height), fit.x, bottom);
                canvas.Apply();
                return canvas;
            }
            catch
            {
                Object.DestroyImmediate(canvas);
                throw;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // Read a render target into a CPU texture, upright. The play-mode view's
        // target is stored top-down on APIs whose UV origin is at the top (Metal,
        // D3D, Vulkan) — the Game view draws it flipped there — so it is flipped
        // back while copying.
        internal static Texture2D ReadRenderTarget(RenderTexture source, bool flip)
        {
            var tmp = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var prevActive = RenderTexture.active;
            var tex = new Texture2D(source.width, source.height, CaptureFormat, CaptureMipChain);
            try
            {
                if (flip) Graphics.Blit(source, tmp, new Vector2(1f, -1f), new Vector2(0f, 1f));
                else Graphics.Blit(source, tmp);
                RenderTexture.active = tmp;
                tex.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                tex.Apply();
                return tex;
            }
            catch
            {
                Object.DestroyImmediate(tex);
                throw;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(tmp);
            }
        }

        internal static Rect ScaleRect(Rect r, int fromW, int fromH, int toW, int toH)
        {
            if (fromW <= 0 || fromH <= 0 || (fromW == toW && fromH == toH)) return r;
            float sx = (float)toW / fromW, sy = (float)toH / fromH;
            return new Rect(r.x * sx, r.y * sy, r.width * sx, r.height * sy);
        }

        // Tint everything outside the safe area (the unsafe band) and outline the
        // safe rect. `safe` is in the frame's pixels, bottom-left origin (the
        // Screen.safeArea convention).
        internal static void DrawSafeAreaOverlay(Texture2D frame, Rect safe)
        {
            int w = frame.width, h = frame.height;
            var px = frame.GetPixels32();
            int x0 = Mathf.Clamp(Mathf.RoundToInt(safe.xMin), 0, w), x1 = Mathf.Clamp(Mathf.RoundToInt(safe.xMax), 0, w);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(safe.yMin), 0, h), y1 = Mathf.Clamp(Mathf.RoundToInt(safe.yMax), 0, h);
            int line = Mathf.Max(2, Mathf.RoundToInt(Mathf.Min(w, h) / 360f));
            var band = new Color32(255, 0, 80, 255);
            var edge = new Color32(0, 255, 120, 255);
            const float bandAlpha = 0.4f;
            for (int y = 0; y < h; y++)
            {
                bool rowInside = y >= y0 && y < y1;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    bool inside = rowInside && x >= x0 && x < x1;
                    if (!inside)
                    {
                        var c = px[i];
                        px[i] = new Color32(
                            (byte)(c.r + (band.r - c.r) * bandAlpha),
                            (byte)(c.g + (band.g - c.g) * bandAlpha),
                            (byte)(c.b + (band.b - c.b) * bandAlpha), 255);
                        continue;
                    }
                    if (x < x0 + line || x >= x1 - line || y < y0 + line || y >= y1 - line)
                        px[i] = edge;
                }
            }
            frame.SetPixels32(px);
            frame.Apply();
        }

        // ---- capture context (which device / resolution a frame was taken at) ----

        internal sealed class GameViewContext
        {
            public EditorWindow View;
            public string Mode = "unknown";       // game | simulator | none | unknown
            public string SizeLabel;               // Game view size entry
            public string Device;                  // Simulator device id
            public string Orientation;
            public int ScreenWidth, ScreenHeight;
            public Rect SafeArea;
            public string Note;
        }

        internal static GameViewContext DescribeGameView()
        {
            var ctx = new GameViewContext();
            try
            {
                if (!GameViewAdapter.IsGameViewAvailable(out var code, out var message))
                {
                    var size = Handles.GetMainGameViewSize();
                    ctx.ScreenWidth = (int)size.x;
                    ctx.ScreenHeight = (int)size.y;
                    ctx.SafeArea = new Rect(0, 0, ctx.ScreenWidth, ctx.ScreenHeight);
                    ctx.Note = code + ": " + message;
                    return ctx;
                }
                if (GameViewAdapter.TryGameScreen(out var view, out var kind, out int w, out int h, out var safe, out var sim))
                {
                    ctx.ScreenWidth = w;
                    ctx.ScreenHeight = h;
                    ctx.SafeArea = safe;
                }
                ctx.View = view;
                ctx.Mode = GameViewAdapter.KindName(kind);
                if (kind == PlayModeViewKind.Game)
                {
                    ctx.SizeLabel = GameViewAdapter.SelectedSize(view)?.Label;
                    ctx.Orientation = GameViewAdapter.ShapeOrientation(w, h);
                }
                else if (sim != null)
                {
                    ctx.Device = sim.Device?.Id;
                    ctx.Orientation = GameViewAdapter.OrientationName(sim.Orientation);
                }
            }
            catch (Exception ex)
            {
                ctx.Note = "Game view state unavailable: " + ex.Message;
            }
            return ctx;
        }

        internal static void AppendContext(System.Text.StringBuilder sb, GameViewContext ctx)
        {
            sb.Append("{\"mode\":").Append(BridgeJson.EscapeString(ctx.Mode));
            if (ctx.Device != null) sb.Append(",\"device\":").Append(BridgeJson.EscapeString(ctx.Device));
            if (ctx.SizeLabel != null) sb.Append(",\"size\":").Append(BridgeJson.EscapeString(ctx.SizeLabel));
            if (ctx.Orientation != null) sb.Append(",\"orientation\":").Append(BridgeJson.EscapeString(ctx.Orientation));
            sb.Append(",\"screen\":[").Append(ctx.ScreenWidth).Append(',').Append(ctx.ScreenHeight).Append(']');
            if (ctx.Mode == "simulator")
            {
                sb.Append(",\"safe_area\":");
                AppendRect(sb, ctx.SafeArea);
            }
            sb.Append(",\"playing\":").Append(Application.isPlaying ? "true" : "false");
            if (ctx.Note != null) sb.Append(",\"note\":").Append(BridgeJson.EscapeString(ctx.Note));
            sb.Append('}');
        }

        // Response fields for a frame capture (no surrounding braces, leading comma).
        internal static void AppendCaptureFields(System.Text.StringBuilder sb, FrameCapture c)
        {
            sb.Append(",\"width\":").Append(c.Width).Append(",\"height\":").Append(c.Height);
            // native: the game's own resolution, unscaled; fitted: a frame scaled
            // into width x height (padded when the aspect differs); requested:
            // Camera.main rendered directly at width x height.
            string sizeMode = c.Native ? "native" : c.Source == "main_camera" ? "requested" : "fitted";
            sb.Append(",\"size\":").Append(BridgeJson.EscapeString(sizeMode));
            if (sizeMode == "fitted")
            {
                sb.Append(",\"scaled_from\":[").Append(c.NativeWidth).Append(',').Append(c.NativeHeight).Append(']');
                sb.Append(",\"content_rect\":[").Append(c.Content.x).Append(',').Append(c.Content.y).Append(',')
                  .Append(c.Content.width).Append(',').Append(c.Content.height).Append(']');
            }
            sb.Append(",\"source\":").Append(BridgeJson.EscapeString(c.Source));
            if (c.SafeAreaOverlay)
            {
                sb.Append(",\"overlay\":\"safe_area\",\"safe_area\":");
                AppendRect(sb, c.SafeArea);
            }
            if (c.Context != null)
            {
                if (c.Source == "play_mode_view" && c.Context.ScreenWidth > 0 &&
                    (c.NativeWidth != c.Context.ScreenWidth || c.NativeHeight != c.Context.ScreenHeight))
                    sb.Append(",\"warning\":").Append(BridgeJson.EscapeString(
                        "The view's last rendered frame (" + c.NativeWidth + "x" + c.NativeHeight + ") differs from the game's screen (" +
                        c.Context.ScreenWidth + "x" + c.Context.ScreenHeight + "): the size changed and has not rendered yet, or the view is not visible."));
                if (c.Source == "screen_capture")
                    sb.Append(",\"warning\":").Append(BridgeJson.EscapeString(
                        "The play-mode view's render target was unreachable; this frame is the Editor panel ScreenCapture saw and may include Editor chrome."));
                sb.Append(",\"capturedAt\":");
                AppendContext(sb, c.Context);
            }
        }

        private static void AppendRect(System.Text.StringBuilder sb, Rect r)
        {
            sb.Append('[').Append(Fmt(r.x)).Append(',').Append(Fmt(r.y)).Append(',')
              .Append(Fmt(r.width)).Append(',').Append(Fmt(r.height)).Append(']');
        }

        private static string Fmt(float v) => Math.Round(v, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        // Argument rules shared by screenshot / capture_inline. Null when valid.
        internal static string ValidateFrameArgs(string view, int? width, int? height, string size, string overlay)
        {
            bool sizeNative = false;
            if (!string.IsNullOrEmpty(size))
            {
                if (size != "native") return "size must be \"native\" (or omitted).";
                sizeNative = true;
            }
            if (width.HasValue != height.HasValue) return "Pass width and height together (or neither).";
            if (sizeNative && width.HasValue) return "size: \"native\" returns the game's own resolution; drop width/height or drop size.";
            if (sizeNative && view != "composed" && view != "game") return "size: \"native\" applies to the composed and game views.";
            if (!string.IsNullOrEmpty(overlay) && overlay != "none")
            {
                if (overlay != "safe_area") return "overlay must be \"safe_area\" (or omitted).";
                if (view != "composed") return "overlay: \"safe_area\" applies to the composed view.";
            }
            return null;
        }

        // M20 Plan 1 / T20.1.1 — render from an arbitrary camera pose without
        // moving the scene/game camera. A transient Camera is positioned at the
        // requested pose, renders to a RenderTexture, then is destroyed. The
        // scene camera is never touched. When a main camera exists its
        // cullingMask / nearClip / farClip are mirrored so the pose render sees
        // the same layers the player would; otherwise sensible defaults apply.
        public static string CaptureFromPose(
            Vector3 position, Vector3 rotationEuler, float fov,
            int width, int height, string background)
        {
            var png = CaptureFromPoseBytes(position, rotationEuler, fov, width, height, background);
            return WritePng(png, PathStamp("camera"));
        }

        public static byte[] CaptureFromPoseBytes(
            Vector3 position, Vector3 rotationEuler, float fov,
            int width, int height, string background)
        {
            var go = new GameObject("___screenshot_pose_cam_temp");
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.transform.position = position;
                cam.transform.rotation = Quaternion.Euler(rotationEuler);
                cam.fieldOfView = fov;
                cam.clearFlags = ParseClearFlags(background);
                cam.backgroundColor = background == "transparent"
                    ? new Color(0, 0, 0, 0)
                    : new Color32(64, 64, 64, 255);

                // Mirror the main camera's view setup when available so the pose
                // render sees the same layers/depth range as the live game view.
                // Falls back to rendering everything but IgnoreRaycast.
                var main = Camera.main;
                if (main != null)
                {
                    cam.cullingMask = main.cullingMask;
                    cam.nearClipPlane = main.nearClipPlane;
                    cam.farClipPlane = main.farClipPlane;
                }
                else
                {
                    cam.cullingMask = ~(1 << 2); // everything except IgnoreRaycast
                    cam.nearClipPlane = 0.01f;
                    cam.farClipPlane = 1000f;
                }

                return RenderCameraToPng(cam, width, height);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static Camera ResolveMainCamera()
        {
            var cam = Camera.main;
            if (cam != null) return cam;
            // SceneQuery centralizes the FindObjectsByType (2023.1+) /
            // FindObjectsOfType (pre-2023.1) version dance — see SceneQuery.cs.
            var all = SceneQuery.FindAllOfType<Camera>();
            if (all == null || all.Length == 0)
                throw new InvalidOperationException("No camera found in the scene.");
            return all[0];
        }

        public static string CaptureIsolated(GameObject target, int quadWidth, int quadHeight, string background)
        {
            return WritePng(CaptureIsolatedBytes(target, quadWidth, quadHeight, background), PathStamp("isolated"));
        }

        // M20 Plan 1 / T20.1.1 — byte-returning isolated capture for the inline
        // path. Reuses CompositeToPngBytes so capture_inline can serve the same
        // 2x2 composite as the file-returning screenshot tool without a temp
        // file round-trip.
        public static byte[] CaptureIsolatedBytes(GameObject target, int quadWidth, int quadHeight, string background)
        {
            if (target == null)
                throw new InvalidOperationException("Target GameObject not found.");

            var bounds = ComputeBounds(target);
            if (!bounds.HasValue)
                throw new InvalidOperationException(
                    $"GameObject '{target.name}' has no renderers — nothing to capture.");

            var b = bounds.Value;
            var center = b.center;
            var size = Mathf.Max(b.size.x, b.size.y, b.size.z);
            var dist = size * 1.8f + 0.5f;

            var go = new GameObject("___screenshot_cam_temp");
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = ParseClearFlags(background);
            cam.backgroundColor = background == "transparent"
                ? new Color(0, 0, 0, 0)
                : new Color32(64, 64, 64, 255);
            cam.orthographic = true;
            cam.orthographicSize = size * 0.6f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = dist * 4f;
            // B33 — ComputeBounds encapsulates EVERY child renderer (including
            // children on other layers), so culling to only the root's layer
            // frames the full bounds but renders just a slice of the hierarchy.
            // Mirror the bounds walk and union every child renderer's layer so
            // the framed bounds and the rendered pixels agree.
            cam.cullingMask = ComputeLayerMask(target);

            // Directions: Front, Right, Back, Top
            var dirs = new[]
            {
                (Vector3.forward, "front"),
                (Vector3.right,   "right"),
                (Vector3.back,    "back"),
                (Vector3.up,      "top"),
            };

            var composites = new Texture2D[4];

            try
            {
                for (int i = 0; i < 4; i++)
                {
                    var dir = dirs[i].Item1;
                    var camPos = center - dir * dist;

                    go.transform.position = camPos;
                    go.transform.LookAt(center, i == 3 ? Vector3.back : Vector3.up);

                    composites[i] = RenderQuad(cam, quadWidth, quadHeight);
                }

                return CompositeToPngBytes(composites, quadWidth, quadHeight);
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
                for (int i = 0; i < composites.Length; i++)
                    if (composites[i] != null) Object.DestroyImmediate(composites[i]);
            }
        }

        // ---- helpers ----

        private static string WritePng(byte[] png, string outPath)
        {
            Directory.CreateDirectory(OutputDir);
            File.WriteAllBytes(outPath, png);
            return outPath;
        }

        // feedback #5 — render every enabled Camera in ascending depth order
        // into a single RenderTexture, mirroring how Unity composites the frame.
        // The first camera clears; subsequent cameras render on top (depth test
        // resolves overlaps by depth). Each camera's targetTexture is swapped and
        // restored, so the scene's cameras are left untouched.
        private static Texture2D RenderAllCamerasByDepthTexture(int width, int height)
        {
            var cameras = SceneQuery.FindAllOfType<Camera>();
            if (cameras == null || cameras.Length == 0)
                throw new InvalidOperationException("No camera found in the scene.");

            // Enabled, depth-sorted ascending (Unity renders low-depth first).
            var sorted = new System.Collections.Generic.List<Camera>(cameras.Length);
            for (int i = 0; i < cameras.Length; i++)
            {
                var c = cameras[i];
                if (c != null && c.isActiveAndEnabled && c.tag != "Untagged_editor_only")
                    sorted.Add(c);
            }
            if (sorted.Count == 0)
                throw new InvalidOperationException("No enabled camera found in the scene.");
            sorted.Sort((a, b) => a.depth.CompareTo(b.depth));

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var prevActive = RenderTexture.active;
            var prevTargets = new System.Collections.Generic.List<KeyValuePair<Camera, RenderTexture>>(sorted.Count);
            try
            {
                RenderTexture.active = rt;
                for (int i = 0; i < sorted.Count; i++)
                {
                    var cam = sorted[i];
                    prevTargets.Add(new KeyValuePair<Camera, RenderTexture>(cam, cam.targetTexture));
                    cam.targetTexture = rt;
                    cam.Render();
                }

                var tex = new Texture2D(width, height, CaptureFormat, CaptureMipChain);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                return tex;
            }
            finally
            {
                for (int i = 0; i < prevTargets.Count; i++)
                {
                    try { prevTargets[i].Key.targetTexture = prevTargets[i].Value; }
                    catch { /* camera destroyed mid-capture */ }
                }
                RenderTexture.active = prevActive;
                if (rt != null)
                {
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
        }

        // Render an existing Camera to PNG bytes. The camera's targetTexture is
        // swapped to a transient RenderTexture and restored in finally — the
        // caller's camera is left untouched.
        private static byte[] RenderCameraToPng(Camera cam, int width, int height)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;

            try
            {
                cam.targetTexture = rt;
                RenderTexture.active = rt;
                cam.Render();

                var tex = new Texture2D(width, height, CaptureFormat, CaptureMipChain);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                var png = ImageConversion.EncodeToPNG(tex);
                Object.DestroyImmediate(tex);
                return png;
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                if (rt != null)
                {
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
        }

        private static Texture2D RenderQuad(Camera cam, int width, int height)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;

            try
            {
                cam.targetTexture = rt;
                RenderTexture.active = rt;
                cam.Render();

                var tex = new Texture2D(width, height, CaptureFormat, CaptureMipChain);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                return tex;
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                if (rt != null)
                {
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
        }

        private static string CompositeToPng(Texture2D[] quads, int qw, int qh, string outPath)
        {
            var png = CompositeToPngBytes(quads, qw, qh);
            Directory.CreateDirectory(OutputDir);
            File.WriteAllBytes(outPath, png);
            return outPath;
        }

        private static byte[] CompositeToPngBytes(Texture2D[] quads, int qw, int qh)
        {
            int totalW = qw * 2;
            int totalH = qh * 2;

            var composite = new Texture2D(totalW, totalH, CaptureFormat, CaptureMipChain);
            var pixels = new Color32[totalW * totalH];

            // Layout:
            //   [Front] [Right]
            //   [Back ] [Top ]
            // Texture2D origin is bottom-left; we fill accordingly.
            for (int q = 0; q < 4; q++)
            {
                var srcPixels = quads[q].GetPixels32();
                int xOff, yOff;

                switch (q)
                {
                    case 0: xOff = 0;  yOff = qh; break;  // Front (top-left)
                    case 1: xOff = qw; yOff = qh; break;  // Right (top-right)
                    case 2: xOff = 0;  yOff = 0;  break;  // Back (bottom-left)
                    default: xOff = qw; yOff = 0;  break;  // Top (bottom-right)
                }

                for (int y = 0; y < qh; y++)
                {
                    for (int x = 0; x < qw; x++)
                    {
                        int srcIdx = y * qw + x;
                        int dstIdx = (yOff + y) * totalW + (xOff + x);
                        if (dstIdx < pixels.Length)
                            pixels[dstIdx] = srcPixels[srcIdx];
                    }
                }
            }

            composite.SetPixels32(pixels);
            composite.Apply();

            var png = ImageConversion.EncodeToPNG(composite);
            Object.DestroyImmediate(composite);
            return png;
        }

        private static Bounds? ComputeBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0) return null;

            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);
            return b;
        }

        // Union of every child renderer's layer. Mirrors ComputeBounds so the
        // culling mask and the framed bounds agree: a hierarchy with children
        // on multiple layers is rendered in full, not just the root's slice.
        private static int ComputeLayerMask(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0)
                return 1 << go.layer;
            int mask = 0;
            for (int i = 0; i < renderers.Length; i++)
                mask |= 1 << renderers[i].gameObject.layer;
            return mask;
        }

        private static CameraClearFlags ParseClearFlags(string background)
        {
            return background switch
            {
                "transparent" => CameraClearFlags.SolidColor,
                "solid" => CameraClearFlags.SolidColor,
                _ => CameraClearFlags.Skybox
            };
        }

        private static string PathStamp(string label)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var name = $"screenshot-{label}-{stamp}.png";
            return Path.Combine(OutputDir, name);
        }
    }

    // One frame capture and what it was taken at.
    internal sealed class FrameCapture
    {
        public byte[] Png;
        public string FilePath;
        public int Width, Height;               // the returned image
        public int NativeWidth, NativeHeight;   // the frame before any fit
        public bool Native;                     // returned at native size (no scaling)
        public RectInt Content;                 // fitted frame inside the image, top-left origin
        public string Source;                   // play_mode_view | cameras | screen_capture | main_camera
        public bool SafeAreaOverlay;
        public Rect SafeArea;                   // game screen pixels, bottom-left origin
        public ScreenshotService.GameViewContext Context;
    }
}
