using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Screenshot
{
    // Visual regression compare — the one transferable idea worth taking from
    // unity-biome-mcp's "visual baseline/diff" feature.
    //
    // A named reference snapshot is captured from a view (scene / game /
    // isolated) and stored under ~/.unity-open-mcp/screenshots/references/.
    // A later capture of the same view is compared against it: per-pixel RGBA
    // diff for an exact mismatch count, and an 8x8 grayscale average-hash
    // (aHash) for a perceptual similarity signal that tolerates minor
    // anti-aliasing / compression drift. A diff image (mismatched pixels set
    // to red over the current frame) is produced for the agent to inspect.
    //
    // "reference snapshot" / "visual compare" is used instead of "baseline" —
    // that name is already taken by the verify-issue baseline tools
    // (baseline_create, regression_check), which compare compiler-error /
    // project-health snapshots, NOT images.
    //
    // No external dependency — pure UnityEngine APIs (Texture2D.GetPixels32,
    // ImageConversion.EncodeToPNG). Mismatched images of differing dimensions
    // are resampled to the reference size via a scaled RenderTexture readback
    // so the compare is deterministic regardless of capture resolution.
    static class ImageCompareService
    {
        public static readonly string ReferencesDir = Path.Combine(
            ScreenshotService.OutputDir, "references");

        private const int PerceptualGrid = 8;       // aHash grid (8x8 = 64 bits)
        private const int PerceptualBits = PerceptualGrid * PerceptualGrid;
        private const byte ChannelEpsilon = 8;      // per-channel drift tolerated as "equal"

        // ---- public API ----

        // Persist a captured PNG as a named reference, with a sidecar .meta.json
        // recording dimensions, perceptual hash, and capture time. Overwrites an
        // existing reference of the same name.
        public static ReferenceInfo SaveReference(string name, byte[] png)
        {
            ValidateName(name);
            Directory.CreateDirectory(ReferencesDir);

            var pngPath = PngPath(name);
            File.WriteAllBytes(pngPath, png);

            Texture2D tex = null;
            try
            {
                tex = LoadTexture(png);
                var info = new ReferenceInfo
                {
                    Name = name,
                    Width = tex.width,
                    Height = tex.height,
                    AHash = ComputeAHash(tex),
                    CapturedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    PngPath = pngPath,
                };
                File.WriteAllText(MetaPath(name), SerializeMeta(info));
                return info;
            }
            finally
            {
                if (tex != null) Object.DestroyImmediate(tex);
            }
        }

        // Load a saved reference's PNG bytes, or null if it does not exist.
        public static byte[] LoadReferencePng(string name)
        {
            ValidateName(name);
            var path = PngPath(name);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        // Read a saved reference's sidecar meta, or null if it does not exist.
        public static ReferenceInfo LoadReferenceMeta(string name)
        {
            ValidateName(name);
            var path = MetaPath(name);
            if (!File.Exists(path)) return null;
            try { return DeserializeMeta(File.ReadAllText(path)); }
            catch { return null; }
        }

        // Compare two PNG byte buffers. The `current` image is resampled to the
        // reference dimensions when they differ so the per-pixel diff is over the
        // same sample grid. Returns mismatch metrics + a diff-image PNG (red
        // highlights over the current frame) when mismatched and produceDiff is
        // true; diffImageBytes is null when the images match exactly or the caller
        // opted out. The `match` flag is true when pixelDiffPercent <= sensitivity.
        public static CompareResult Compare(byte[] referencePng, byte[] currentPng,
            float sensitivity, bool produceDiff)
        {
            if (referencePng == null || referencePng.Length == 0)
                throw new ArgumentException("reference PNG is empty.");
            if (currentPng == null || currentPng.Length == 0)
                throw new ArgumentException("current PNG is empty.");

            Texture2D refTex = null;
            Texture2D curTex = null;
            Texture2D aligned = null;
            bool ownsAligned = false;
            try
            {
                refTex = LoadTexture(referencePng);
                curTex = LoadTexture(currentPng);

                // Resample current to the reference dimensions if needed so the
                // per-pixel diff runs over an identical grid.
                aligned = curTex;
                if (curTex.width != refTex.width || curTex.height != refTex.height)
                {
                    aligned = Resample(curTex, refTex.width, refTex.height);
                    ownsAligned = true;
                }

                var refPixels = refTex.GetPixels32();
                var curPixels = aligned.GetPixels32();
                int total = refPixels.Length;

                long mismatched = 0;
                var diffPixels = produceDiff ? new Color32[total] : null;

                for (int i = 0; i < total; i++)
                {
                    var a = refPixels[i];
                    var b = curPixels[i];
                    // Treat the pixel as different if ANY channel drifts beyond
                    // epsilon. Tolerates minor AA / compression noise.
                    bool differs =
                        Math.Abs(a.r - b.r) > ChannelEpsilon ||
                        Math.Abs(a.g - b.g) > ChannelEpsilon ||
                        Math.Abs(a.b - b.b) > ChannelEpsilon ||
                        Math.Abs(a.a - b.a) > ChannelEpsilon;

                    if (differs)
                    {
                        mismatched++;
                        if (produceDiff)
                            diffPixels[i] = new Color32(255, 0, 0, 255); // red highlight
                    }
                    else if (produceDiff)
                    {
                        diffPixels[i] = b; // keep the current frame for context
                    }
                }

                double diffPercent = total > 0
                    ? (mismatched / (double)total) * 100.0
                    : 0.0;
                int perceptualDistance = HammingDistance(ComputeAHash(refTex), ComputeAHash(aligned));

                byte[] diffPng = null;
                if (produceDiff && mismatched > 0)
                {
                    var diffTex = new Texture2D(refTex.width, refTex.height, TextureFormat.RGBA32, false);
                    try
                    {
                        diffTex.SetPixels32(diffPixels);
                        diffTex.Apply();
                        diffPng = ImageConversion.EncodeToPNG(diffTex);
                    }
                    finally
                    {
                        Object.DestroyImmediate(diffTex);
                    }
                }

                return new CompareResult
                {
                    PixelDiffPercent = diffPercent,
                    MismatchedPixels = mismatched,
                    TotalPixels = total,
                    PerceptualDistance = perceptualDistance,
                    Match = diffPercent <= sensitivity * 100.0,
                    DiffImageBytes = diffPng,
                };
            }
            finally
            {
                if (ownsAligned && aligned != null) Object.DestroyImmediate(aligned);
                if (curTex != null) Object.DestroyImmediate(curTex);
                if (refTex != null) Object.DestroyImmediate(refTex);
            }
        }

        // Enumerate saved references (name + dims + hash). Sorted by name.
        public static ReferenceInfo[] ListReferences()
        {
            Directory.CreateDirectory(ReferencesDir);
            var dir = new DirectoryInfo(ReferencesDir);
            var files = dir.GetFiles("*.png");
            var list = new System.Collections.Generic.List<ReferenceInfo>(files.Length);
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f.Name);
                var meta = LoadReferenceMeta(name);
                if (meta != null) { list.Add(meta); continue; }
                // No sidecar — synthesize minimal info from the PNG itself.
                list.Add(new ReferenceInfo { Name = name, PngPath = f.FullName });
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            return list.ToArray();
        }

        // Remove a named reference + its sidecar. Returns false if it did not exist.
        public static bool DeleteReference(string name)
        {
            ValidateName(name);
            var png = PngPath(name);
            var meta = MetaPath(name);
            bool existed = File.Exists(png);
            if (File.Exists(png)) File.Delete(png);
            if (File.Exists(meta)) File.Delete(meta);
            return existed;
        }

        public static bool ReferenceExists(string name)
        {
            ValidateName(name);
            return File.Exists(PngPath(name));
        }

        // ---- internals ----

        private static string PngPath(string name) => Path.Combine(ReferencesDir, name + ".png");
        private static string MetaPath(string name) => Path.Combine(ReferencesDir, name + ".meta.json");

        // Reject path separators / traversal — references are flat named files.
        private static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Reference name must not be empty.");
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.Contains("/") || name.Contains("\\") || name.Contains(".."))
                throw new ArgumentException($"Invalid reference name '{name}' (no path separators or traversal).");
        }

        // Decode a PNG into a readable RGBA32 texture. Caller owns + must destroy it.
        private static Texture2D LoadTexture(byte[] png)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(png))
            {
                Object.DestroyImmediate(tex);
                throw new ArgumentException("Could not decode PNG image.");
            }
            return tex;
        }

        // Resample a texture to new dimensions via a scaled RenderTexture readback.
        // Returns a NEW Texture2D (caller destroys it); the source is untouched.
        private static Texture2D Resample(Texture2D src, int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var prevActive = RenderTexture.active;
            try
            {
                Graphics.SetRenderTarget(rt);
                GL.Clear(false, true, Color.clear);
                UnityEngine.Graphics.Blit(src, rt);
                RenderTexture.active = rt;

                var outTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                outTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                outTex.Apply();
                return outTex;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // 8x8 grayscale average-hash. Downsample to 8x8 via GetScaledBilinear,
        // convert to luminance, threshold against the mean. Robust to resampling.
        private static ulong ComputeAHash(Texture2D tex)
        {
            double sum = 0;
            var gray = new double[PerceptualBits];
            for (int y = 0; y < PerceptualGrid; y++)
            {
                for (int x = 0; x < PerceptualGrid; x++)
                {
                    // Sample normalized [0,1] coords; GetPixelBilinear flips V for us.
                    var c = tex.GetPixelBilinear(
                        (x + 0.5f) / PerceptualGrid,
                        (y + 0.5f) / PerceptualGrid);
                    // Rec. 601 luma.
                    double l = 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
                    gray[y * PerceptualGrid + x] = l;
                    sum += l;
                }
            }
            double mean = sum / PerceptualBits;

            ulong hash = 0;
            for (int i = 0; i < PerceptualBits; i++)
            {
                if (gray[i] >= mean) hash |= (1UL << i);
            }
            return hash;
        }

        private static int HammingDistance(ulong a, ulong b)
        {
            ulong x = a ^ b;
            int count = 0;
            while (x != 0) { count += (int)(x & 1UL); x >>= 1; }
            return count;
        }

        // ---- sidecar meta serialization (hand-rolled, no JSON lib) ----

        private static string SerializeMeta(ReferenceInfo info)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"name\":").Append(Esc(info.Name)).Append(',');
            sb.Append("\"width\":").Append(info.Width).Append(',');
            sb.Append("\"height\":").Append(info.Height).Append(',');
            sb.Append("\"aHash\":\"").Append(info.AHash.ToString("X16", CultureInfo.InvariantCulture)).Append("\",");
            sb.Append("\"capturedAtUtc\":").Append(Esc(info.CapturedAtUtc)).Append(',');
            sb.Append("\"pngPath\":").Append(Esc(info.PngPath ?? ""));
            sb.Append('}');
            return sb.ToString();
        }

        private static ReferenceInfo DeserializeMeta(string json)
        {
            var info = new ReferenceInfo();
            info.Name = Extract(json, "name");
            info.CapturedAtUtc = Extract(json, "capturedAtUtc");
            info.PngPath = Extract(json, "pngPath");
            if (int.TryParse(Extract(json, "width"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)) info.Width = w;
            if (int.TryParse(Extract(json, "height"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) info.Height = h;
            var hashStr = Extract(json, "aHash");
            if (!string.IsNullOrEmpty(hashStr) &&
                ulong.TryParse(hashStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong ah))
                info.AHash = ah;
            return info;
        }

        // Minimal string-field extractor for the flat sidecar JSON. Returns "" if absent.
        private static string Extract(string json, string field)
        {
            var key = "\"" + field + "\":";
            int k = json.IndexOf(key, StringComparison.Ordinal);
            if (k < 0) return "";
            int i = k + key.Length;
            // Quoted string value.
            if (i < json.Length && json[i] == '"')
            {
                int start = i + 1;
                int end = start;
                while (end < json.Length)
                {
                    if (json[end] == '\\' && end + 1 < json.Length) { end += 2; continue; }
                    if (json[end] == '"') break;
                    end++;
                }
                return Unescape(json.Substring(start, end - start));
            }
            // Bare numeric/bool value up to the next comma/brace.
            int v = i;
            while (v < json.Length && json[v] != ',' && json[v] != '}') v++;
            return json.Substring(i, v - i).Trim();
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(s[i]);
            }
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

        // ---- result records ----

        public sealed class ReferenceInfo
        {
            public string Name;
            public int Width;
            public int Height;
            public ulong AHash;
            public string CapturedAtUtc;
            public string PngPath;
        }

        public sealed class CompareResult
        {
            public double PixelDiffPercent;
            public long MismatchedPixels;
            public int TotalPixels;
            public int PerceptualDistance;
            public bool Match;
            public byte[] DiffImageBytes;
        }
    }
}
