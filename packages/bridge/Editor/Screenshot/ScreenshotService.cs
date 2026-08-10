using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.Config;
using UnityOpenMcpBridge.ObjectRefs;
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
        public static string CaptureComposedView(int width, int height)
        {
            return WritePng(CaptureComposedViewBytes(width, height), PathStamp("composed"));
        }

        public static byte[] CaptureComposedViewBytes(int width, int height)
        {
            if (Application.isPlaying)
            {
                // CaptureScreenshotAsTexture grabs the last rendered frame's
                // backbuffer (all cameras + Overlay UI). The overload taking a
                // resolution scales the capture; passing the exact size avoids a
                // resize. Reads back synchronously from the GPU backbuffer.
                var captured = ScreenCapture.CaptureScreenshotAsTexture();
                try
                {
                    return TextureToPngScaled(captured, width, height);
                }
                finally
                {
                    Object.DestroyImmediate(captured);
                }
            }

            // edit mode — no player loop, so Overlay canvases cannot render.
            // Composite every enabled camera in ascending depth order.
            return RenderAllCamerasByDepth(width, height);
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

        // feedback #5 — encode a captured Texture2D to PNG, scaling it to the
        // requested size when the backbuffer resolution differs (ScreenCapture
        // returns the screen's native resolution). Uses RenderTexture + a
        // scaled Graphics.Blit so the output matches the caller's width/height.
        private static byte[] TextureToPngScaled(Texture src, int width, int height)
        {
            if (src == null)
                throw new InvalidOperationException("Captured frame was empty.");

            // Fast path: native size already matches.
            if (src.width == width && src.height == height)
            {
                var tmp = new Texture2D(width, height, CaptureFormat, CaptureMipChain);
                var prevActive = RenderTexture.active;
                try
                {
                    var rt = RenderTexture.GetTemporary(width, height, 0);
                    Graphics.Blit(src, rt);
                    RenderTexture.active = rt;
                    tmp.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    tmp.Apply();
                    var png = ImageConversion.EncodeToPNG(tmp);
                    RenderTexture.ReleaseTemporary(rt);
                    return png;
                }
                finally
                {
                    Object.DestroyImmediate(tmp);
                    RenderTexture.active = prevActive;
                }
            }

            // Scale path.
            var scaled = new Texture2D(width, height, CaptureFormat, CaptureMipChain);
            var prevActiveScale = RenderTexture.active;
            try
            {
                var rt = RenderTexture.GetTemporary(width, height, 0);
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                scaled.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                scaled.Apply();
                var png = ImageConversion.EncodeToPNG(scaled);
                RenderTexture.ReleaseTemporary(rt);
                return png;
            }
            finally
            {
                Object.DestroyImmediate(scaled);
                RenderTexture.active = prevActiveScale;
            }
        }

        // feedback #5 — render every enabled Camera in ascending depth order
        // into a single RenderTexture, mirroring how Unity composites the frame.
        // The first camera clears; subsequent cameras render on top (depth test
        // resolves overlaps by depth). Each camera's targetTexture is swapped and
        // restored, so the scene's cameras are left untouched.
        private static byte[] RenderAllCamerasByDepth(int width, int height)
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
                var png = ImageConversion.EncodeToPNG(tex);
                Object.DestroyImmediate(tex);
                return png;
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
}
