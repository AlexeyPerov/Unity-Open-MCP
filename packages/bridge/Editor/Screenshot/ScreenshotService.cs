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
            ".unity-agent", "screenshots");

        const TextureFormat CaptureFormat = TextureFormat.RGBA32;
        const bool CaptureMipChain = false;

        // ---- public API ----

        public static string CaptureSceneView(int width, int height)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
                throw new InvalidOperationException("No active Scene view found.");

            var cam = sv.camera;
            return CaptureCamera(cam, width, height, PathStamp("scene"));
        }

        public static string CaptureGameView(int width, int height)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var all = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
                if (all == null || all.Length == 0)
                    throw new InvalidOperationException("No camera found in the scene.");
                cam = all[0];
            }
            return CaptureCamera(cam, width, height, PathStamp("game"));
        }

        public static string CaptureIsolated(GameObject target, int quadWidth, int quadHeight, string background)
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
            RenderTexture prevTarget = null;
            Camera prevCam = null;

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

                return CompositeToPng(composites, quadWidth, quadHeight, PathStamp("isolated"));
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
                for (int i = 0; i < composites.Length; i++)
                    if (composites[i] != null) Object.DestroyImmediate(composites[i]);
            }
        }

        // ---- helpers ----

        static string CaptureCamera(Camera cam, int width, int height, string outPath)
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

                var png = ImageConversion.EncodeToPNG();
                Directory.CreateDirectory(OutputDir);
                File.WriteAllBytes(outPath, png);

                Object.DestroyImmediate(tex);
                return outPath;
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

        static Texture2D RenderQuad(Camera cam, int width, int height)
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

        static string CompositeToPng(Texture2D[] quads, int qw, int qh, string outPath)
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

            var png = ImageConversion.EncodeToPNG();
            Directory.CreateDirectory(OutputDir);
            File.WriteAllBytes(outPath, png);
            Object.DestroyImmediate(composite);
            return outPath;
        }

        static Bounds? ComputeBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0) return null;

            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);
            return b;
        }

        static CameraClearFlags ParseClearFlags(string background)
        {
            return background switch
            {
                "transparent" => CameraClearFlags.SolidColor,
                "solid" => CameraClearFlags.SolidColor,
                _ => CameraClearFlags.Skybox
            };
        }

        static string PathStamp(string label)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var name = $"screenshot-{label}-{stamp}.png";
            return Path.Combine(OutputDir, name);
        }
    }
}
