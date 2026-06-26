using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
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
            ".unity-open-mcp", "screenshots");

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
            var all = Object.FindObjectsByType<Camera>();
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
            cam.cullingMask = 1 << target.layer;

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
