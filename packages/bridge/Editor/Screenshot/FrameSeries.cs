// Frame-scheduled captures inside the player loop, shared by
// unity_senses_capture_series and inputsim_gesture `capture_at_ms`.
//
// Timing. The play-mode scheduler ticks at the start of EarlyUpdate; at that
// point the play-mode view's render target holds the frame the game rendered
// last (frame F-1 at tick F). A slot is captured at the first tick whose
// previous frame reached the slot's time / frame offset from the series base,
// and is labelled with THAT frame (number, Time.time, elapsed) — the image
// and its label always describe the same frame. `unchanged` marks a capture
// whose pixels equal the previous capture's (a static screen — or a hidden
// Game view tab, which does not render).
//
// Cost. A capture is a GPU blit + synchronous read-back of the native frame
// (optionally scaled / cropped) into managed memory; PNG encoding happens after
// the last capture, spread over later ticks (so encoding never delays a
// scheduled capture). Bounded: at most MaxFrames captures and MaxRawBytes of
// pixels held at once; files go to the capture folder
// (~/.unity-open-mcp/screenshots/series-<stamp>-<id>/).
//
// Metric (optional, per capture, inside `metric_region` or the captured area):
// mean Rec. 709 luminance (0..1) and, against the previous capture, the mean
// absolute luminance difference and the fraction of pixels whose luminance
// moved by more than ChangedThreshold.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityOpenMcpBridge.Playtest;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Screenshot
{
    internal sealed class FrameSlot
    {
        public int Index;
        public double AtMs = -1;      // time offset from the base (ms), or -1
        public int AtFrame = -1;      // frame offset from the base, or -1
        public bool Taken;
        public int Image = -1;        // index into the recorder's images
        public int Frame;
        public double PlayTime;
        public double ElapsedMs;      // offset from the base, in the series' time base
        public double RealtimeMs;
        public bool Unchanged;
        public string Dropped;        // reason when not captured
        public double Luminance = double.NaN;
        public double Diff = double.NaN;
        public double Changed = double.NaN;
    }

    // at_ms | every_ms + duration_ms | at_frames.
    internal sealed class FrameSchedule
    {
        internal const int MaxFrames = 120;
        internal const double MaxMs = 600_000;
        internal const int MaxFrameOffset = 36_000;
        internal const int MinEveryMs = 10;

        public readonly List<FrameSlot> Slots = new List<FrameSlot>();
        public string Mode;           // at_ms | every_ms | at_frames
        public double EveryMs, DurationMs;

        public double LastMs
        {
            get
            {
                double last = 0;
                foreach (var s in Slots) last = Math.Max(last, s.AtMs >= 0 ? s.AtMs : s.AtFrame * 1000.0 / 30.0);
                return last;
            }
        }

        // Exactly one of the three forms. `maxFrames` caps the count.
        internal static FrameSchedule Parse(string atMs, int? everyMs, int? durationMs, string atFrames, int maxFrames, string prefix, out string error)
        {
            error = null;
            int forms = (atMs != null ? 1 : 0) + (everyMs.HasValue || durationMs.HasValue ? 1 : 0) + (atFrames != null ? 1 : 0);
            if (forms != 1)
            {
                error = "Pass exactly one schedule: " + prefix + "at_ms [..], every_ms + duration_ms, or at_frames [..].";
                return null;
            }
            var schedule = new FrameSchedule();
            if (atMs != null)
            {
                schedule.Mode = "at_ms";
                if (!TryNumbers(atMs, out var values) || values.Count == 0)
                { error = prefix + "at_ms must be a non-empty array of numbers."; return null; }
                values.Sort();
                double prev = -1;
                foreach (var v in values)
                {
                    if (v < 0 || v > MaxMs) { error = prefix + "at_ms values must be 0.." + MaxMs + "."; return null; }
                    if (Math.Abs(v - prev) < 1e-9) continue;
                    prev = v;
                    schedule.Slots.Add(new FrameSlot { AtMs = v });
                }
            }
            else if (atFrames != null)
            {
                schedule.Mode = "at_frames";
                if (!TryNumbers(atFrames, out var values) || values.Count == 0)
                { error = prefix + "at_frames must be a non-empty array of integers."; return null; }
                values.Sort();
                int prev = -1;
                foreach (var v in values)
                {
                    if (v < 0 || v > MaxFrameOffset || Math.Abs(v - Math.Round(v)) > 1e-9)
                    { error = prefix + "at_frames values must be integers 0.." + MaxFrameOffset + "."; return null; }
                    int f = (int)Math.Round(v);
                    if (f == prev) continue;
                    prev = f;
                    schedule.Slots.Add(new FrameSlot { AtFrame = f });
                }
            }
            else
            {
                schedule.Mode = "every_ms";
                if (!everyMs.HasValue || !durationMs.HasValue)
                { error = "every_ms and duration_ms go together."; return null; }
                if (everyMs.Value < MinEveryMs || everyMs.Value > MaxMs)
                { error = "every_ms must be " + MinEveryMs + ".." + MaxMs + "."; return null; }
                if (durationMs.Value < 0 || durationMs.Value > MaxMs)
                { error = "duration_ms must be 0.." + MaxMs + "."; return null; }
                schedule.EveryMs = everyMs.Value;
                schedule.DurationMs = durationMs.Value;
                long count = (long)Math.Floor((double)durationMs.Value / everyMs.Value) + 1;
                if (count > maxFrames)
                { error = "every_ms " + everyMs.Value + " over duration_ms " + durationMs.Value + " is " + count + " captures; the limit is " + maxFrames + ". Raise every_ms or shorten duration_ms."; return null; }
                for (long i = 0; i < count; i++) schedule.Slots.Add(new FrameSlot { AtMs = i * (double)everyMs.Value });
            }
            if (schedule.Slots.Count > maxFrames)
            {
                error = prefix + (schedule.Mode) + " holds " + schedule.Slots.Count + " captures; the limit is " + maxFrames + ".";
                return null;
            }
            for (int i = 0; i < schedule.Slots.Count; i++) schedule.Slots[i].Index = i;
            return schedule;
        }

        internal static bool TryNumbers(string raw, out List<double> values)
        {
            values = new List<double>();
            if (raw == null) return false;
            raw = raw.Trim();
            if (!raw.StartsWith("[")) return false;
            foreach (var item in JsonBody.GetArrayRawValues(raw))
            {
                if (!WaitConditionParser.TryNumber(item, out var v)) return false;
                values.Add(v);
            }
            return true;
        }

        // Rect [x, y, w, h] in screen pixels (bottom-left origin).
        internal static bool TryRect(string raw, out RectInt rect, out string error, string name)
        {
            rect = default;
            error = null;
            if (!TryNumbers(raw, out var v) || v.Count != 4)
            {
                error = name + " must be [x, y, w, h] in screen pixels (bottom-left origin, as ui_snapshot rects).";
                return false;
            }
            int x = (int)Math.Floor(v[0]), y = (int)Math.Floor(v[1]);
            int w = (int)Math.Ceiling(v[0] + v[2]) - x, h = (int)Math.Ceiling(v[1] + v[3]) - y;
            if (v[2] <= 0 || v[3] <= 0 || x < 0 || y < 0)
            {
                error = name + " needs x, y >= 0 and a positive width and height.";
                return false;
            }
            rect = new RectInt(x, y, w, h);
            return true;
        }
    }

    internal sealed class FrameSeriesOptions
    {
        public float Scale = 1f;
        public RectInt? Region;           // screen pixels, bottom-left origin
        public bool Luminance, Diff;
        public RectInt? MetricRegion;     // screen pixels, bottom-left origin
        public bool ContactSheet;
        public string Label = "series";   // folder prefix
    }

    // One captured image (pixels until encoded).
    internal sealed class FrameImage
    {
        public byte[] Rgba;               // bottom-up rows, RGBA32
        public int Width, Height;
        public string File;
        public byte[] Thumb;              // bottom-up rows, RGBA32
        public int ThumbWidth, ThumbHeight;
    }

    internal sealed class FrameSeriesRecorder
    {
        internal const long MaxRawBytes = 512L * 1024 * 1024;
        internal const double ChangedThreshold = 10.0 / 255.0;
        internal const int ThumbMaxWidth = 320;
        internal const int MetricMaxSamples = 250_000;

        public readonly FrameSchedule Schedule;
        public readonly FrameSeriesOptions Options;
        internal string OperationId;
        private readonly List<FrameImage> images = new List<FrameImage>();
        private readonly List<string> warnings = new List<string>();
        private bool armed;
        private ConditionTick baseTick;
        private int next;                 // next slot to consider
        private long heldBytes;
        private ulong lastHash;
        private bool hasHash;
        private int encoded;
        private bool sheetDone;
        private string folder;
        private string contactSheetFile;
        private int sheetWidth, sheetHeight, sheetColumns;
        private float[] previousLuma;
        private int previousLumaW, previousLumaH;
        private int screenW, screenH, nativeW, nativeH;
        private string captureError;

        // Test seam: grab the current frame (rgba bottom-up, width, height)
        // instead of reading the play-mode view.
        internal Func<FrameSeriesRecorder, (byte[] Rgba, int Width, int Height)?> GrabOverride;
        // Test seam: write files under this folder instead of the capture folder.
        internal string FolderOverride;
        // Pixels held before encoding (test seam; MaxRawBytes in production).
        internal long RawBudgetBytes = MaxRawBytes;

        internal FrameSeriesRecorder(FrameSchedule schedule, FrameSeriesOptions options, string operationId)
        {
            Schedule = schedule;
            Options = options ?? new FrameSeriesOptions();
            OperationId = operationId;
        }

        public bool Armed => armed;
        public bool CapturesDone => armed && next >= Schedule.Slots.Count || captureError != null;
        public bool EncodingDone => sheetDone;
        public string CaptureError => captureError;
        public string Folder => folder;
        public int Taken { get { int n = 0; foreach (var s in Schedule.Slots) if (s.Taken) n++; return n; } }
        public int Pending => images.Count - encoded;

        // Start the schedule clock at `tick` (the frame offset 0 / time 0 refers to).
        internal void Arm(ConditionTick tick)
        {
            if (armed) return;
            armed = true;
            baseTick = tick;
        }

        internal ConditionTick BaseTick => baseTick;

        // Called every tick. `previous` is the frame whose image the view holds.
        internal void Observe(ConditionTick now, ConditionTick? previous, int screenWidth, int screenHeight)
        {
            if (!armed || previous == null || captureError != null) return;
            var shown = previous.Value;
            if (shown.Frame < baseTick.Frame) return;
            screenW = screenWidth;
            screenH = screenHeight;
            double offsetMs = shown.ElapsedMs - baseTick.ElapsedMs;
            int offsetFrames = shown.Frame - baseTick.Frame;
            int image = -1;
            while (next < Schedule.Slots.Count)
            {
                var slot = Schedule.Slots[next];
                bool due = slot.AtMs >= 0 ? offsetMs + 0.5 >= slot.AtMs : offsetFrames >= slot.AtFrame;
                if (!due) break;
                next++;
                if (image < 0) image = Capture(slot, shown, offsetMs);
                else ShareImage(slot, Schedule.Slots[next - 2]);
                if (captureError != null) return;
            }
        }

        private void ShareImage(FrameSlot slot, FrameSlot from)
        {
            slot.Taken = from.Taken;
            slot.Image = from.Image;
            slot.Frame = from.Frame;
            slot.PlayTime = from.PlayTime;
            slot.ElapsedMs = from.ElapsedMs;
            slot.RealtimeMs = from.RealtimeMs;
            slot.Unchanged = from.Unchanged;
            slot.Dropped = from.Dropped;
            slot.Luminance = from.Luminance;
            slot.Diff = from.Diff;
            slot.Changed = from.Changed;
        }

        private int Capture(FrameSlot slot, ConditionTick shown, double offsetMs)
        {
            slot.Frame = shown.Frame;
            slot.PlayTime = shown.PlayTime;
            slot.ElapsedMs = offsetMs;
            slot.RealtimeMs = shown.RealtimeMs - baseTick.RealtimeMs;
            (byte[] Rgba, int Width, int Height)? grabbed;
            try { grabbed = GrabOverride != null ? GrabOverride(this) : Grab(); }
            catch (Exception ex)
            {
                captureError = "Capture failed: " + ex.GetType().Name + ": " + ex.Message;
                slot.Dropped = "capture_failed";
                return -1;
            }
            if (grabbed == null)
            {
                captureError = "The play-mode view's render target is unavailable (the Game view / Simulator closed?).";
                slot.Dropped = "render_target_unavailable";
                return -1;
            }
            var g = grabbed.Value;
            long bytes = (long)g.Width * g.Height * 4;
            if (heldBytes + bytes > RawBudgetBytes)
            {
                slot.Dropped = "memory_budget";
                if (warnings.Count < 5) warnings.Add("Capture #" + slot.Index + " dropped: more than " + (RawBudgetBytes >> 20) + " MB of frames held; use scale or region.");
                return -1;
            }
            heldBytes += bytes;
            var hash = SampleHash(g.Rgba, g.Width, g.Height);
            slot.Unchanged = hasHash && hash == lastHash;
            lastHash = hash;
            hasHash = true;
            images.Add(new FrameImage { Rgba = g.Rgba, Width = g.Width, Height = g.Height });
            slot.Taken = true;
            slot.Image = images.Count - 1;
            if (Options.Luminance || Options.Diff) Measure(slot, g.Rgba, g.Width, g.Height);
            return slot.Image;
        }

        // ---- grabbing -----------------------------------------------------

        private (byte[] Rgba, int Width, int Height)? Grab()
        {
            var rt = GameViewAdapter.TargetTexture(GameViewAdapter.MainView());
            if (rt == null) return null;
            nativeW = rt.width;
            nativeH = rt.height;
            var crop = CropInTarget(rt.width, rt.height);
            var rgba = GrabRenderTarget(rt, SystemInfo.graphicsUVStartsAtTop, Options.Scale, crop, out int w, out int h);
            return (rgba, w, h);
        }

        // FNV-1a over a sparse pixel sample plus the size: equal for identical
        // frames, and cheap enough to run on every capture.
        internal static ulong SampleHash(byte[] rgba, int width, int height)
        {
            ulong hash = 14695981039346656037UL;
            void Mix(uint v) { hash ^= v; hash *= 1099511628211UL; }
            Mix((uint)width);
            Mix((uint)height);
            int pixels = width * height;
            int step = Math.Max(1, pixels / 65_536);
            for (int p = 0; p < pixels; p += step)
            {
                int i = p * 4;
                Mix((uint)(rgba[i] | rgba[i + 1] << 8 | rgba[i + 2] << 16));
            }
            return hash;
        }

        // The region (screen pixels) mapped into render-target pixels, clipped.
        private RectInt? CropInTarget(int rtW, int rtH)
        {
            if (!Options.Region.HasValue) return null;
            var r = Options.Region.Value;
            float sx = screenW > 0 ? (float)rtW / screenW : 1f, sy = screenH > 0 ? (float)rtH / screenH : 1f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(r.x * sx), 0, rtW), y0 = Mathf.Clamp(Mathf.FloorToInt(r.y * sy), 0, rtH);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((r.x + r.width) * sx), 0, rtW), y1 = Mathf.Clamp(Mathf.CeilToInt((r.y + r.height) * sy), 0, rtH);
            if (x1 - x0 < 1 || y1 - y0 < 1) throw new InvalidOperationException("region lies outside the game screen (" + rtW + "x" + rtH + ").");
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        // Blit (flip on top-origin graphics APIs, optional scale), then read the
        // crop (render-target pixels, bottom-left origin) back as RGBA32 rows,
        // bottom-up.
        internal static byte[] GrabRenderTarget(RenderTexture source, bool flip, float scale, RectInt? crop, out int width, out int height)
        {
            scale = Mathf.Clamp(scale <= 0 ? 1f : scale, 0.05f, 1f);
            int tw = Mathf.Max(1, Mathf.RoundToInt(source.width * scale)), th = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            var tmp = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var prevActive = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                if (flip) Graphics.Blit(source, tmp, new Vector2(1f, -1f), new Vector2(0f, 1f));
                else Graphics.Blit(source, tmp);
                int cx = 0, cy = 0, cw = tw, ch = th;
                if (crop.HasValue)
                {
                    var c = crop.Value;
                    cx = Mathf.Clamp(Mathf.FloorToInt(c.x * scale), 0, tw - 1);
                    cy = Mathf.Clamp(Mathf.FloorToInt(c.y * scale), 0, th - 1);
                    cw = Mathf.Clamp(Mathf.CeilToInt(c.width * scale), 1, tw - cx);
                    ch = Mathf.Clamp(Mathf.CeilToInt(c.height * scale), 1, th - cy);
                }
                tex = new Texture2D(cw, ch, TextureFormat.RGBA32, false);
                RenderTexture.active = tmp;
                tex.ReadPixels(new Rect(cx, cy, cw, ch), 0, 0, false);
                width = cw;
                height = ch;
                return tex.GetRawTextureData<byte>().ToArray();
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(tmp);
                if (tex != null) Object.DestroyImmediate(tex);
            }
        }

        // ---- metric -------------------------------------------------------

        // The metric region in captured-image pixels (bottom-left origin).
        internal RectInt MetricRect(int imageW, int imageH)
        {
            if (!Options.MetricRegion.HasValue) return new RectInt(0, 0, imageW, imageH);
            var m = Options.MetricRegion.Value;
            // Screen pixels → render-target pixels → (scale) → minus the crop origin.
            float sx = screenW > 0 && nativeW > 0 ? (float)nativeW / screenW : 1f;
            float sy = screenH > 0 && nativeH > 0 ? (float)nativeH / screenH : 1f;
            float s = Mathf.Clamp(Options.Scale <= 0 ? 1f : Options.Scale, 0.05f, 1f);
            float ox = 0, oy = 0;
            if (Options.Region.HasValue)
            {
                ox = Mathf.Floor(Options.Region.Value.x * sx * s);
                oy = Mathf.Floor(Options.Region.Value.y * sy * s);
            }
            int x0 = Mathf.Clamp(Mathf.FloorToInt(m.x * sx * s - ox), 0, imageW), y0 = Mathf.Clamp(Mathf.FloorToInt(m.y * sy * s - oy), 0, imageH);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((m.x + m.width) * sx * s - ox), 0, imageW), y1 = Mathf.Clamp(Mathf.CeilToInt((m.y + m.height) * sy * s - oy), 0, imageH);
            return new RectInt(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }

        private void Measure(FrameSlot slot, byte[] rgba, int w, int h)
        {
            var r = MetricRect(w, h);
            if (r.width < 1 || r.height < 1)
            {
                if (warnings.Count < 5) warnings.Add("metric_region lies outside the captured area; no metric for #" + slot.Index + ".");
                return;
            }
            var luma = SampleLuminance(rgba, w, r, out int lw, out int lh);
            slot.Luminance = Mean(luma);
            if (Options.Diff && previousLuma != null && previousLumaW == lw && previousLumaH == lh)
            {
                DiffStats(previousLuma, luma, out var meanAbs, out var changed);
                slot.Diff = meanAbs;
                slot.Changed = changed;
            }
            previousLuma = luma;
            previousLumaW = lw;
            previousLumaH = lh;
        }

        // Rec. 709 luminance (0..1) over `r`, sub-sampled to ≤ MetricMaxSamples.
        internal static float[] SampleLuminance(byte[] rgba, int width, RectInt r, out int sampleW, out int sampleH)
        {
            int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)r.width * r.height / MetricMaxSamples)));
            sampleW = (r.width + step - 1) / step;
            sampleH = (r.height + step - 1) / step;
            var luma = new float[sampleW * sampleH];
            int k = 0;
            for (int y = r.y; y < r.y + r.height; y += step)
                for (int x = r.x; x < r.x + r.width; x += step)
                {
                    int i = (y * width + x) * 4;
                    luma[k++] = (0.2126f * rgba[i] + 0.7152f * rgba[i + 1] + 0.0722f * rgba[i + 2]) / 255f;
                }
            return luma;
        }

        internal static double Mean(float[] v)
        {
            if (v.Length == 0) return double.NaN;
            double sum = 0;
            foreach (var x in v) sum += x;
            return sum / v.Length;
        }

        internal static void DiffStats(float[] a, float[] b, out double meanAbs, out double changedFraction)
        {
            double sum = 0;
            int changed = 0;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                double d = Math.Abs(a[i] - b[i]);
                sum += d;
                if (d > ChangedThreshold) changed++;
            }
            meanAbs = n > 0 ? sum / n : double.NaN;
            changedFraction = n > 0 ? (double)changed / n : double.NaN;
        }

        // ---- encoding -----------------------------------------------------

        // Encode pending images for up to `budgetMs` (at least one per call),
        // then the contact sheet. True when everything is written.
        internal bool EncodeStep(double budgetMs)
        {
            if (sheetDone) return true;
            var clock = Stopwatch.StartNew();
            while (encoded < images.Count)
            {
                EncodeOne(encoded);
                encoded++;
                if (clock.Elapsed.TotalMilliseconds >= budgetMs) return false;
            }
            if (Options.ContactSheet && images.Count > 0 && contactSheetFile == null) WriteContactSheet();
            sheetDone = true;
            return true;
        }

        // Encode whatever is pending now (cancel / exit play). No contact sheet.
        internal void FlushPending()
        {
            while (encoded < images.Count)
            {
                try { EncodeOne(encoded); } catch (Exception) { /* best effort */ }
                encoded++;
            }
        }

        internal void Release()
        {
            foreach (var image in images) { image.Rgba = null; image.Thumb = null; }
            heldBytes = 0;
            previousLuma = null;
        }

        private string EnsureFolder()
        {
            if (folder != null) return folder;
            var root = FolderOverride ?? ScreenshotService.OutputDir;
            var id = (OperationId ?? Guid.NewGuid().ToString()).Replace("-", "");
            folder = Path.Combine(root, Options.Label + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) +
                                         "-" + (id.Length > 8 ? id.Substring(0, 8) : id));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private void EncodeOne(int index)
        {
            var image = images[index];
            if (image.Rgba == null) return;
            var slot = FirstSlotOf(index);
            var name = "frame-" + (slot != null ? slot.Index : index).ToString("000", CultureInfo.InvariantCulture) +
                       "-f" + (slot != null ? slot.Frame : 0).ToString(CultureInfo.InvariantCulture) +
                       "-" + Math.Round(slot != null ? slot.ElapsedMs : 0).ToString("0", CultureInfo.InvariantCulture) + "ms.png";
            var png = ImageConversion.EncodeArrayToPNG(image.Rgba, GraphicsFormat.R8G8B8A8_UNorm, (uint)image.Width, (uint)image.Height);
            var path = Path.Combine(EnsureFolder(), name);
            File.WriteAllBytes(path, png);
            image.File = path;
            if (Options.ContactSheet)
            {
                image.ThumbWidth = Math.Min(ThumbMaxWidth, image.Width);
                image.ThumbHeight = Math.Max(1, (int)Math.Round((double)image.Height * image.ThumbWidth / image.Width));
                image.Thumb = Downscale(image.Rgba, image.Width, image.Height, image.ThumbWidth, image.ThumbHeight);
            }
            heldBytes -= (long)image.Width * image.Height * 4;
            image.Rgba = null;
        }

        private FrameSlot FirstSlotOf(int image)
        {
            foreach (var s in Schedule.Slots) if (s.Image == image) return s;
            return null;
        }

        // Box-filtered downscale (bottom-up RGBA32 in and out).
        internal static byte[] Downscale(byte[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new byte[dw * dh * 4];
            for (int y = 0; y < dh; y++)
            {
                int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++)
                        {
                            int i = (yy * sw + xx) * 4;
                            r += src[i]; g += src[i + 1]; b += src[i + 2]; a += src[i + 3]; n++;
                        }
                    int o = (y * dw + x) * 4;
                    dst[o] = (byte)(r / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(b / n); dst[o + 3] = (byte)(a / n);
                }
            }
            return dst;
        }

        // ---- contact sheet ------------------------------------------------

        internal const int SheetPad = 6;
        internal const int LabelHeight = 2 * BitmapFont.GlyphHeight + 6;
        internal const int MinCellWidth = 240;

        internal static string LabelFor(FrameSlot slot)
            => "#" + slot.Index.ToString(CultureInfo.InvariantCulture) +
               " F" + slot.Frame.ToString(CultureInfo.InvariantCulture) +
               " +" + Math.Round(slot.ElapsedMs).ToString("0", CultureInfo.InvariantCulture) + "MS" +
               (slot.Unchanged ? " =" : "");

        private void WriteContactSheet()
        {
            var cells = new List<FrameSlot>();
            foreach (var s in Schedule.Slots) if (s.Taken && images[s.Image].Thumb != null) cells.Add(s);
            if (cells.Count == 0) return;
            int thumbW = 0, thumbH = 0;
            foreach (var s in cells) { thumbW = Math.Max(thumbW, images[s.Image].ThumbWidth); thumbH = Math.Max(thumbH, images[s.Image].ThumbHeight); }
            int cellW = Math.Max(thumbW, MinCellWidth), cellH = thumbH + LabelHeight;
            int cols = cells.Count <= 4 ? cells.Count : Math.Min(6, (int)Math.Ceiling(Math.Sqrt(cells.Count)));
            int rows = (cells.Count + cols - 1) / cols;
            int w = cols * cellW + (cols + 1) * SheetPad, h = rows * cellH + (rows + 1) * SheetPad;
            var sheet = new byte[w * h * 4];
            for (int i = 0; i < sheet.Length; i += 4) { sheet[i] = 24; sheet[i + 1] = 24; sheet[i + 2] = 28; sheet[i + 3] = 255; }
            for (int c = 0; c < cells.Count; c++)
            {
                var slot = cells[c];
                var image = images[slot.Image];
                int col = c % cols, row = c / cols;
                int left = SheetPad + col * (cellW + SheetPad);
                int top = SheetPad + row * (cellH + SheetPad);          // from the image top
                // Label strip (top of the cell), then the thumbnail under it.
                BitmapFont.Draw(sheet, w, h, left + 4, top + 3, LabelFor(slot), 2, new Color32(240, 240, 240, 255));
                int thumbLeft = left + (cellW - image.ThumbWidth) / 2;
                int thumbTop = top + LabelHeight;
                for (int y = 0; y < image.ThumbHeight; y++)
                {
                    // Thumb rows are bottom-up: row y from the top is ThumbHeight-1-y.
                    int srcRow = image.ThumbHeight - 1 - y;
                    int dstRow = h - 1 - (thumbTop + y);
                    if (dstRow < 0 || dstRow >= h) continue;
                    Buffer.BlockCopy(image.Thumb, srcRow * image.ThumbWidth * 4, sheet, (dstRow * w + thumbLeft) * 4, image.ThumbWidth * 4);
                }
            }
            var png = ImageConversion.EncodeArrayToPNG(sheet, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h);
            contactSheetFile = Path.Combine(EnsureFolder(), "contact-sheet.png");
            File.WriteAllBytes(contactSheetFile, png);
            sheetWidth = w;
            sheetHeight = h;
            sheetColumns = cols;
        }

        // ---- result ---------------------------------------------------------

        internal void AppendJson(StringBuilder sb, string timeBase)
        {
            sb.Append("{\"schedule\":").Append(BridgeJson.EscapeString(Schedule.Mode));
            sb.Append(",\"time\":").Append(BridgeJson.EscapeString(timeBase));
            sb.Append(",\"requested\":").Append(Schedule.Slots.Count);
            sb.Append(",\"captured\":").Append(Taken);
            sb.Append(",\"base\":");
            if (armed)
                sb.Append("{\"frame\":").Append(baseTick.Frame).Append(",\"play_time\":").Append(PlaytestJson.Num(baseTick.PlayTime, 4)).Append('}');
            else sb.Append("null");
            sb.Append(",\"folder\":").Append(folder == null ? "null" : BridgeJson.EscapeString(folder));
            if (Options.Scale < 1f) sb.Append(",\"scale\":").Append(PlaytestJson.Num(Options.Scale, 3));
            if (Options.Region.HasValue) { sb.Append(",\"region\":"); AppendRect(sb, Options.Region.Value); }
            if (Options.MetricRegion.HasValue) { sb.Append(",\"metric_region\":"); AppendRect(sb, Options.MetricRegion.Value); }
            if (nativeW > 0) sb.Append(",\"native\":[").Append(nativeW).Append(',').Append(nativeH).Append(']');
            sb.Append(",\"frames\":[");
            for (int i = 0; i < Schedule.Slots.Count; i++)
            {
                var s = Schedule.Slots[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"index\":").Append(s.Index);
                if (s.AtMs >= 0) sb.Append(",\"at_ms\":").Append(PlaytestJson.Num(s.AtMs));
                else sb.Append(",\"at_frame\":").Append(s.AtFrame);
                if (s.Taken)
                {
                    var image = images[s.Image];
                    sb.Append(",\"frame\":").Append(s.Frame);
                    sb.Append(",\"play_time\":").Append(PlaytestJson.Num(s.PlayTime, 4));
                    sb.Append(",\"elapsed_ms\":").Append(PlaytestJson.Num(s.ElapsedMs));
                    sb.Append(",\"realtime_ms\":").Append(PlaytestJson.Num(s.RealtimeMs));
                    sb.Append(",\"file\":").Append(image.File == null ? "null" : BridgeJson.EscapeString(image.File));
                    sb.Append(",\"width\":").Append(image.Width).Append(",\"height\":").Append(image.Height);
                    if (s.Unchanged) sb.Append(",\"unchanged\":true");
                    if (!double.IsNaN(s.Luminance))
                    {
                        sb.Append(",\"metric\":{\"luminance\":").Append(PlaytestJson.Num(s.Luminance, 4));
                        if (!double.IsNaN(s.Diff))
                            sb.Append(",\"diff\":").Append(PlaytestJson.Num(s.Diff, 4)).Append(",\"changed\":").Append(PlaytestJson.Num(s.Changed, 4));
                        sb.Append('}');
                    }
                }
                else
                {
                    sb.Append(",\"captured\":false");
                    if (s.Dropped != null) sb.Append(",\"dropped\":").Append(BridgeJson.EscapeString(s.Dropped));
                }
                sb.Append('}');
            }
            sb.Append(']');
            if (contactSheetFile != null)
                sb.Append(",\"contact_sheet\":{\"file\":").Append(BridgeJson.EscapeString(contactSheetFile))
                  .Append(",\"width\":").Append(sheetWidth).Append(",\"height\":").Append(sheetHeight)
                  .Append(",\"columns\":").Append(sheetColumns)
                  .Append(",\"label\":\"#index F<frame> +<elapsed>MS (= : same pixels as the previous capture)\"}");
            if (captureError != null) sb.Append(",\"capture_error\":").Append(BridgeJson.EscapeString(captureError));
            if (warnings.Count > 0)
            {
                sb.Append(",\"warnings\":[");
                for (int i = 0; i < warnings.Count; i++) { if (i > 0) sb.Append(','); sb.Append(BridgeJson.EscapeString(warnings[i])); }
                sb.Append(']');
            }
            sb.Append('}');
        }

        private static void AppendRect(StringBuilder sb, RectInt r)
            => sb.Append('[').Append(r.x).Append(',').Append(r.y).Append(',').Append(r.width).Append(',').Append(r.height).Append(']');
    }

    // A 5x7 bitmap font for contact-sheet labels: digits, '#', '+', '-', '.',
    // '=', ' ' and the letters F, M, S, T (labels are upper-case).
    internal static class BitmapFont
    {
        internal const int GlyphWidth = 5;
        internal const int GlyphHeight = 7;

        private static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
        {
            ['0'] = new[] { "01110", "10001", "10011", "10101", "11001", "10001", "01110" },
            ['1'] = new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" },
            ['2'] = new[] { "01110", "10001", "00001", "00010", "00100", "01000", "11111" },
            ['3'] = new[] { "11111", "00010", "00100", "00010", "00001", "10001", "01110" },
            ['4'] = new[] { "00010", "00110", "01010", "10010", "11111", "00010", "00010" },
            ['5'] = new[] { "11111", "10000", "11110", "00001", "00001", "10001", "01110" },
            ['6'] = new[] { "00110", "01000", "10000", "11110", "10001", "10001", "01110" },
            ['7'] = new[] { "11111", "00001", "00010", "00100", "01000", "01000", "01000" },
            ['8'] = new[] { "01110", "10001", "10001", "01110", "10001", "10001", "01110" },
            ['9'] = new[] { "01110", "10001", "10001", "01111", "00001", "00010", "01100" },
            ['#'] = new[] { "01010", "01010", "11111", "01010", "11111", "01010", "01010" },
            ['+'] = new[] { "00000", "00100", "00100", "11111", "00100", "00100", "00000" },
            ['-'] = new[] { "00000", "00000", "00000", "11111", "00000", "00000", "00000" },
            ['.'] = new[] { "00000", "00000", "00000", "00000", "00000", "01100", "01100" },
            ['='] = new[] { "00000", "00000", "11111", "00000", "11111", "00000", "00000" },
            ['F'] = new[] { "11111", "10000", "10000", "11110", "10000", "10000", "10000" },
            ['M'] = new[] { "10001", "11011", "10101", "10101", "10001", "10001", "10001" },
            ['S'] = new[] { "01111", "10000", "10000", "01110", "00001", "00001", "11110" },
            ['T'] = new[] { "11111", "00100", "00100", "00100", "00100", "00100", "00100" },
        };

        internal static bool Has(char c) => c == ' ' || Glyphs.ContainsKey(char.ToUpperInvariant(c));

        internal static int Width(string text, int scale) => text.Length * (GlyphWidth + 1) * scale;

        // Draw into bottom-up RGBA32 pixels; (left, top) from the image's top-left.
        internal static void Draw(byte[] rgba, int width, int height, int left, int top, string text, int scale, Color32 color)
        {
            int pen = left;
            foreach (var ch in text)
            {
                if (Glyphs.TryGetValue(char.ToUpperInvariant(ch), out var rows))
                {
                    for (int gy = 0; gy < GlyphHeight; gy++)
                        for (int gx = 0; gx < GlyphWidth; gx++)
                        {
                            if (rows[gy][gx] != '1') continue;
                            for (int sy = 0; sy < scale; sy++)
                                for (int sx = 0; sx < scale; sx++)
                                {
                                    int x = pen + gx * scale + sx, yTop = top + gy * scale + sy;
                                    if (x < 0 || x >= width || yTop < 0 || yTop >= height) continue;
                                    int i = ((height - 1 - yTop) * width + x) * 4;
                                    rgba[i] = color.r; rgba[i + 1] = color.g; rgba[i + 2] = color.b; rgba[i + 3] = 255;
                                }
                        }
                }
                pen += (GlyphWidth + 1) * scale;
            }
        }
    }
}
