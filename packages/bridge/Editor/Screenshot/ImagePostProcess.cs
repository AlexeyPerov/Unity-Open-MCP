using System;
using System.IO;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Screenshot
{
    // Crop / downscale one captured PNG and optionally copy it out — the
    // single-screenshot counterpart of capture_series' region / scale. Agents
    // otherwise shelled out to sips / PIL / ImageMagick after nearly every
    // screenshot to crop a panel, shrink a 2556 px frame or move the file into
    // their task folder.
    //
    // Region: [x, y, w, h] in the captured image's pixels, bottom-left origin —
    // for a 'composed' capture at native size that is the game screen, the space
    // of ui_snapshot rects, so a node's rect can be passed as is. Applied first,
    // then scale, then max_edge.
    internal sealed class ImagePostProcess
    {
        internal const int MinMaxEdge = 16;
        internal const int MaxMaxEdge = 8192;

        public RectInt? Region;
        public float Scale = 1f;
        public int MaxEdge;
        public string SaveTo;

        public bool ChangesPixels => Region.HasValue || Scale < 1f || MaxEdge > 0;
        public bool Any => ChangesPixels || !string.IsNullOrEmpty(SaveTo);

        // Filled by Apply.
        public int SourceWidth, SourceHeight, Width, Height;
        public RectInt? AppliedRegion;

        /// <summary>Validate the raw tool arguments; null error means <paramref name="result"/> is usable.</summary>
        internal static string Parse(string region, float? scale, int? maxEdge, string saveTo, out ImagePostProcess result)
        {
            result = new ImagePostProcess();
            if (region != null)
            {
                if (!FrameSchedule.TryRect(region, out var rect, out var error, "region"))
                    return error.Replace("in screen pixels", "in image pixels");
                result.Region = rect;
            }
            if (scale.HasValue)
            {
                if (float.IsNaN(scale.Value) || scale.Value < 0.05f || scale.Value > 1f)
                    return "scale must be between 0.05 and 1 (1 = captured size).";
                result.Scale = scale.Value;
            }
            if (maxEdge.HasValue)
            {
                if (maxEdge.Value < MinMaxEdge || maxEdge.Value > MaxMaxEdge)
                    return "max_edge must be between " + MinMaxEdge + " and " + MaxMaxEdge + " pixels.";
                result.MaxEdge = maxEdge.Value;
            }
            if (!string.IsNullOrWhiteSpace(saveTo))
            {
                var error = CheckSaveTo(saveTo);
                if (error != null) return error;
                result.SaveTo = saveTo.Trim();
            }
            return null;
        }

        // An image written under Assets/ or Packages/ is imported as an asset —
        // a project mutation from a read-only tool — so those roots are refused.
        internal static string CheckSaveTo(string saveTo)
        {
            var normalized = saveTo.Trim().Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) || normalized.Equals("Packages", StringComparison.OrdinalIgnoreCase))
                return "save_to must be outside Assets/ and Packages/ (an image there would be imported as an asset).";
            var ext = Path.GetExtension(normalized);
            if (!normalized.EndsWith("/") && !string.IsNullOrEmpty(ext) && !ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
                return "save_to must be a .png file path or a directory.";
            return null;
        }

        /// <summary>Crop / downscale <paramref name="png"/>; returns it unchanged when no pixel option is set.</summary>
        internal byte[] Apply(byte[] png)
        {
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Texture2D output = null;
            try
            {
                if (!source.LoadImage(png)) throw new InvalidOperationException("The captured PNG could not be decoded.");
                SourceWidth = source.width;
                SourceHeight = source.height;
                Width = SourceWidth;
                Height = SourceHeight;
                if (!ChangesPixels) return png;

                // LoadImage picks the texture format from the PNG (ARGB32 / RGB24 /
                // …), so read through GetPixels32: always RGBA, bottom-up rows.
                var rgba = ToBytes(source.GetPixels32());
                int w = SourceWidth, h = SourceHeight;
                if (Region.HasValue)
                {
                    var r = Region.Value;
                    int x0 = Mathf.Clamp(r.x, 0, w), y0 = Mathf.Clamp(r.y, 0, h);
                    int x1 = Mathf.Clamp(r.x + r.width, 0, w), y1 = Mathf.Clamp(r.y + r.height, 0, h);
                    if (x1 - x0 < 1 || y1 - y0 < 1)
                        throw new CaptureRefusedException("invalid_arguments",
                            "region lies outside the captured image (" + w + "x" + h + ").");
                    AppliedRegion = new RectInt(x0, y0, x1 - x0, y1 - y0);
                    rgba = Crop(rgba, w, AppliedRegion.Value);
                    w = AppliedRegion.Value.width;
                    h = AppliedRegion.Value.height;
                }
                int dw = w, dh = h;
                TargetSize(w, h, Scale, MaxEdge, out dw, out dh);
                if (dw != w || dh != h) rgba = FrameSeriesRecorder.Downscale(rgba, w, h, dw, dh);
                Width = dw;
                Height = dh;

                output = new Texture2D(dw, dh, TextureFormat.RGBA32, false);
                output.SetPixels32(ToColors(rgba));
                output.Apply(false, false);
                return output.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(source);
                if (output != null) Object.DestroyImmediate(output);
            }
        }

        private static byte[] ToBytes(Color32[] pixels)
        {
            var bytes = new byte[pixels.Length * 4];
            for (int i = 0, o = 0; i < pixels.Length; i++, o += 4)
            {
                bytes[o] = pixels[i].r; bytes[o + 1] = pixels[i].g; bytes[o + 2] = pixels[i].b; bytes[o + 3] = pixels[i].a;
            }
            return bytes;
        }

        private static Color32[] ToColors(byte[] rgba)
        {
            var pixels = new Color32[rgba.Length / 4];
            for (int i = 0, o = 0; i < pixels.Length; i++, o += 4)
                pixels[i] = new Color32(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]);
            return pixels;
        }

        // Rows are bottom-up, so a bottom-left-origin rect maps directly.
        internal static byte[] Crop(byte[] rgba, int width, RectInt rect)
        {
            var dst = new byte[rect.width * rect.height * 4];
            for (int row = 0; row < rect.height; row++)
                Buffer.BlockCopy(rgba, ((rect.y + row) * width + rect.x) * 4, dst, row * rect.width * 4, rect.width * 4);
            return dst;
        }

        internal static void TargetSize(int w, int h, float scale, int maxEdge, out int dw, out int dh)
        {
            double factor = scale > 0f && scale < 1f ? scale : 1.0;
            if (maxEdge > 0)
            {
                int longest = Math.Max(w, h);
                if (longest * factor > maxEdge) factor = (double)maxEdge / longest;
            }
            dw = Math.Max(1, (int)Math.Round(w * factor));
            dh = Math.Max(1, (int)Math.Round(h * factor));
        }

        /// <summary>
        /// Copy <paramref name="sourceFile"/> to <see cref="SaveTo"/> (a .png path, or a
        /// directory — trailing slash or existing — that keeps the file name);
        /// relative paths resolve against the project root. Returns the full path.
        /// </summary>
        internal string Save(string sourceFile, string projectRoot)
        {
            var target = SaveTo.Replace('\\', '/');
            if (!Path.IsPathRooted(target)) target = Path.Combine(projectRoot, target);
            bool directory = SaveTo.EndsWith("/") || SaveTo.EndsWith("\\") || Directory.Exists(target);
            if (directory) target = Path.Combine(target, Path.GetFileName(sourceFile));
            target = Path.GetFullPath(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(sourceFile, target, true);
            return target;
        }

        internal void AppendJson(StringBuilder sb, string savedTo)
        {
            if (ChangesPixels)
            {
                sb.Append(",\"postProcess\":{\"source_size\":[").Append(SourceWidth).Append(',').Append(SourceHeight).Append(']');
                if (AppliedRegion.HasValue)
                {
                    var r = AppliedRegion.Value;
                    sb.Append(",\"region\":[").Append(r.x).Append(',').Append(r.y).Append(',').Append(r.width).Append(',').Append(r.height).Append(']');
                }
                sb.Append(",\"size\":[").Append(Width).Append(',').Append(Height).Append("]}");
            }
            if (savedTo != null) sb.Append(",\"savedTo\":").Append(BridgeJson.EscapeString(savedTo));
        }
    }
}
