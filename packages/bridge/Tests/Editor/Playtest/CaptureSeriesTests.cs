// capture_series / gesture capture_at_ms: schedule parsing and limits, the
// recorder's frame accounting on synthetic ticks (a capture taken at tick F is
// labelled with frame F-1, the frame the view holds), shared captures for slots
// due on one frame, the unchanged-pixels flag (every pixel compared), luminance /
// diff metrics inside a metric region, the memory budget, the cleanup flush
// budget, PNG + contact-sheet output (upright, labelled with frame and time),
// argument validation, a start_on trigger arming the schedule at the frame that
// made it true (and reported as it fired), the wall-clock bound of a gameplay
// schedule that stops advancing, registration and the batch refusal.
// Grabbing the real play-mode view is covered by the manual checklist.
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.Extensions.InputSimulation;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpBridge.Playtest;
using UnityOpenMcpBridge.Screenshot;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Tests.Playtest
{
    public class CaptureSeriesTests
    {
        private string folder;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "open-mcp-series-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            PlayModeScheduler.ResetForTests();
            PlayModeScheduler.IsPlayingOverride = () => true;
            PlayModeScheduler.SuppressPlayerLoop = true;
        }

        [TearDown]
        public void TearDown()
        {
            PlayModeScheduler.EndAll(PlayModeEndReason.Cancelled);
            PlayModeScheduler.ResetForTests();
            PlayModeScheduler.IsPlayingOverride = null;
            PlayModeScheduler.SuppressPlayerLoop = false;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        private static ConditionTick T(int frame, double ms) => new ConditionTick { Frame = frame, ElapsedMs = ms, RealtimeMs = ms, PlayTime = ms / 1000.0, TickIndex = frame };

        // A w×h frame filled with one grey level (bottom-up RGBA32).
        private static byte[] Solid(int w, int h, byte grey)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = grey; px[i + 1] = grey; px[i + 2] = grey; px[i + 3] = 255; }
            return px;
        }

        private FrameSeriesRecorder Recorder(FrameSchedule schedule, FrameSeriesOptions options, Func<int, byte[]> frameAt, int w = 8, int h = 4)
        {
            int current = 0;
            var recorder = new FrameSeriesRecorder(schedule, options ?? new FrameSeriesOptions(), "test-series-0001") { FolderOverride = folder };
            recorder.GrabOverride = _ => (frameAt(current), w, h);
            frameAtCurrent = f => current = f;
            return recorder;
        }

        private Action<int> frameAtCurrent;

        // Feed ticks frame..frame+n; the grab sees the image of the previous frame.
        private void Run(FrameSeriesRecorder recorder, int from, int to, double msPerFrame = 100)
        {
            ConditionTick? previous = null;
            for (int f = from; f <= to; f++)
            {
                var now = T(f, (f - from) * msPerFrame);
                if (!recorder.Armed) recorder.Arm(now);
                frameAtCurrent(f - 1);
                recorder.Observe(now, previous, 8, 4);
                previous = now;
            }
        }

        // ---- schedule --------------------------------------------------------

        [Test]
        public void Schedule_ParsesEachFormAndNormalises()
        {
            var atMs = FrameSchedule.Parse("[300, 0, 100, 100, 200]", null, null, null, 120, "", out var error);
            Assert.IsNotNull(atMs, error);
            CollectionAssert.AreEqual(new[] { 0.0, 100, 200, 300 }, atMs.Slots.ConvertAll(s => s.AtMs));

            var every = FrameSchedule.Parse(null, 500, 2000, null, 120, "", out error);
            Assert.AreEqual(5, every.Slots.Count, error);
            Assert.AreEqual(2000, every.Slots[4].AtMs);

            var frames = FrameSchedule.Parse(null, null, null, "[0, 2, 1]", 120, "", out error);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, frames.Slots.ConvertAll(s => s.AtFrame));
        }

        [TestCase("[0]", 100, 1000, null, "exactly one schedule")]
        [TestCase(null, null, null, null, "exactly one schedule")]
        [TestCase(null, 100, null, null, "go together")]
        [TestCase(null, 5, 1000, null, "every_ms must be")]
        [TestCase(null, 10, 5000, null, "the limit is 120")]
        [TestCase("[]", null, null, null, "non-empty")]
        [TestCase("[-1]", null, null, null, "0..")]
        [TestCase(null, null, null, "[1.5]", "integers")]
        [TestCase("\"0,100\"", null, null, null, "array of numbers")]
        public void Schedule_RefusesBadForms(string atMs, int? every, int? duration, string atFrames, string message)
        {
            Assert.IsNull(FrameSchedule.Parse(atMs, every, duration, atFrames, 120, "", out var error));
            StringAssert.Contains(message, error);
        }

        [Test]
        public void Schedule_CountIsBounded()
        {
            var many = "[" + string.Join(",", System.Linq.Enumerable.Range(0, 121)) + "]";
            Assert.IsNull(FrameSchedule.Parse(many, null, null, null, 120, "", out var error));
            StringAssert.Contains("limit is 120", error);
            Assert.IsNull(GestureTools.ParseCaptures("[" + string.Join(",", System.Linq.Enumerable.Range(0, 31)) + "]", out error));
            StringAssert.Contains("limit is 30", error);
            Assert.IsNotNull(GestureTools.ParseCaptures("[0, 100, 300]", out error), error);
        }

        // ---- recorder --------------------------------------------------------

        [Test]
        public void Capture_IsLabelledWithTheFrameTheViewHolds()
        {
            var schedule = FrameSchedule.Parse("[0, 250]", null, null, null, 120, "", out _);
            var rec = Recorder(schedule, null, f => Solid(8, 4, (byte)f));
            Run(rec, 10, 14);   // base frame 10; 100 ms per frame
            Assert.IsTrue(rec.CapturesDone);
            var first = schedule.Slots[0];
            Assert.AreEqual(10, first.Frame, "offset 0 = the base frame, captured at the next tick");
            Assert.AreEqual(0, first.ElapsedMs);
            var second = schedule.Slots[1];
            Assert.AreEqual(13, second.Frame, "first frame at or after 250 ms (frame 13 = 300 ms)");
            Assert.AreEqual(300, second.ElapsedMs, 0.001);
        }

        [Test]
        public void SlotsDueOnOneFrame_ShareTheCapture()
        {
            var schedule = FrameSchedule.Parse("[0, 10, 20]", null, null, null, 120, "", out _);
            int grabs = 0;
            var rec = Recorder(schedule, null, f => { grabs++; return Solid(8, 4, 10); });
            Run(rec, 0, 2);
            Assert.AreEqual(2, grabs, "slot 0 at frame 0; slots 1+2 both due at frame 1 → one grab");
            Assert.AreEqual(schedule.Slots[1].Image, schedule.Slots[2].Image);
            Assert.AreEqual(1, schedule.Slots[2].Frame);
        }

        [Test]
        public void AtFrames_CountFrameOffsets()
        {
            var schedule = FrameSchedule.Parse(null, null, null, "[0, 3]", 120, "", out _);
            var rec = Recorder(schedule, null, f => Solid(8, 4, (byte)f));
            Run(rec, 50, 54);
            Assert.AreEqual(50, schedule.Slots[0].Frame);
            Assert.AreEqual(53, schedule.Slots[1].Frame);
        }

        [Test]
        public void UnchangedPixels_AreFlagged()
        {
            var schedule = FrameSchedule.Parse(null, null, null, "[0, 1, 2]", 120, "", out _);
            var rec = Recorder(schedule, null, f => Solid(8, 4, f < 51 ? (byte)20 : (byte)200));
            Run(rec, 50, 53);
            Assert.IsFalse(schedule.Slots[0].Unchanged);
            Assert.IsFalse(schedule.Slots[1].Unchanged, "frame 51 differs from 50");
            Assert.IsTrue(schedule.Slots[2].Unchanged, "frame 52 equals 51");
        }

        [Test]
        public void Unchanged_ComparesEveryPixel()
        {
            const int w = 512, h = 512;
            var schedule = FrameSchedule.Parse(null, null, null, "[0, 1, 2]", 120, "", out _);
            var rec = Recorder(schedule, null, f =>
            {
                var px = Solid(w, h, 40);
                if (f >= 51) px[1 * 4] = 41;   // one channel of the second pixel
                return px;
            }, w, h);
            Run(rec, 50, 53);
            Assert.IsFalse(schedule.Slots[1].Unchanged, "one pixel differs from frame 50");
            Assert.IsTrue(schedule.Slots[2].Unchanged, "frame 52 equals 51 byte for byte");
        }

        [Test]
        public void FlushPending_StopsAtItsBudgetAndReportsUnwrittenFrames()
        {
            var schedule = FrameSchedule.Parse(null, null, null, "[0, 1, 2]", 120, "", out _);
            var rec = Recorder(schedule, null, f => Solid(8, 4, (byte)f));
            Run(rec, 0, 3);
            Assert.AreEqual(3, rec.Taken);
            rec.FlushPending(0);   // the first frame is always written
            Assert.IsNull(schedule.Slots[0].Dropped);
            Assert.AreEqual("flush_budget", schedule.Slots[1].Dropped);
            Assert.AreEqual("flush_budget", schedule.Slots[2].Dropped);
            var sb = new System.Text.StringBuilder();
            rec.AppendJson(sb, "gameplay");
            var json = sb.ToString();
            StringAssert.Contains("frame-000-f0-0ms.png", json);
            StringAssert.Contains("\"file\":null,\"width\":8,\"height\":4,\"dropped\":\"flush_budget\"", json);
            StringAssert.Contains("were not written", json);
        }

        [Test]
        public void Metric_LuminanceAndDiffInsideTheRegion()
        {
            var schedule = FrameSchedule.Parse(null, null, null, "[0, 1]", 120, "", out _);
            var options = new FrameSeriesOptions { Luminance = true, Diff = true, MetricRegion = new RectInt(0, 0, 4, 4) };
            // Left half (x < 4) is the metric region; frame 1 = black there, frame 2 = white there.
            var rec = Recorder(schedule, options, f =>
            {
                var px = Solid(8, 4, 128);
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int i = (y * 8 + x) * 4;
                        byte v = f <= 1 ? (byte)0 : (byte)255;
                        px[i] = v; px[i + 1] = v; px[i + 2] = v;
                    }
                return px;
            });
            Run(rec, 1, 3);
            Assert.AreEqual(0, schedule.Slots[0].Luminance, 1e-6);
            Assert.IsTrue(double.IsNaN(schedule.Slots[0].Diff), "no previous capture");
            Assert.AreEqual(1, schedule.Slots[1].Luminance, 1e-6);
            Assert.AreEqual(1, schedule.Slots[1].Diff, 1e-6);
            Assert.AreEqual(1, schedule.Slots[1].Changed, 1e-6);
        }

        [Test]
        public void MemoryBudget_DropsCapturesWithAWarning()
        {
            var schedule = FrameSchedule.Parse(null, null, null, "[0, 1, 2]", 120, "", out _);
            var rec = Recorder(schedule, null, f => Solid(8, 4, (byte)f));
            rec.RawBudgetBytes = 8 * 4 * 4 * 2;   // two frames
            Run(rec, 0, 3);
            Assert.IsTrue(schedule.Slots[1].Taken);
            Assert.IsFalse(schedule.Slots[2].Taken);
            Assert.AreEqual("memory_budget", schedule.Slots[2].Dropped);
            var sb = new System.Text.StringBuilder();
            rec.AppendJson(sb, "gameplay");
            StringAssert.Contains("\"dropped\":\"memory_budget\"", sb.ToString());
            StringAssert.Contains("MB of frames held", sb.ToString());
        }

        [Test]
        public void Encode_WritesUprightPngsAndALabelledContactSheet()
        {
            var schedule = FrameSchedule.Parse("[0, 100]", null, null, null, 120, "", out _);
            var options = new FrameSeriesOptions { ContactSheet = true };
            // Bottom row red, the rest blue: an upright PNG has red at the bottom.
            var rec = Recorder(schedule, options, f =>
            {
                var px = Solid(8, 4, 0);
                for (int i = 0; i < px.Length; i += 4) { px[i + 2] = 255; }
                for (int x = 0; x < 8; x++) { int i = x * 4; px[i] = 255; px[i + 2] = 0; }
                return px;
            });
            Run(rec, 7, 9);
            Assert.IsTrue(rec.CapturesDone);
            Assert.IsTrue(rec.EncodeStep(1000));
            var sb = new System.Text.StringBuilder();
            rec.AppendJson(sb, "gameplay");
            var json = sb.ToString();
            StringAssert.Contains("frame-000-f7-0ms.png", json);
            StringAssert.Contains("frame-001-f8-100ms.png", json);
            StringAssert.Contains("\"contact_sheet\":{\"file\":", json);

            var png = File.ReadAllBytes(Path.Combine(rec.Folder, "frame-000-f7-0ms.png"));
            var tex = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(tex.LoadImage(png));
                Assert.AreEqual(8, tex.width);
                Assert.AreEqual(4, tex.height);
                Assert.Greater(tex.GetPixel(0, 0).r, 0.9f, "texture row 0 (bottom) is red");
                Assert.Greater(tex.GetPixel(0, 3).b, 0.9f, "top row is blue");

                Assert.IsTrue(tex.LoadImage(File.ReadAllBytes(Path.Combine(rec.Folder, "contact-sheet.png"))));
                int cell = FrameSeriesRecorder.MinCellWidth;
                Assert.AreEqual(2 * cell + 3 * FrameSeriesRecorder.SheetPad, tex.width);
                // The label strip carries light glyph pixels.
                int lit = 0;
                for (int x = 0; x < cell; x++)
                    for (int y = tex.height - FrameSeriesRecorder.LabelHeight; y < tex.height; y++)
                        if (tex.GetPixel(x, y).r > 0.8f) lit++;
                Assert.Greater(lit, 20);
            }
            finally { Object.DestroyImmediate(tex); }
        }

        [Test]
        public void Labels_UseTheBitmapFontGlyphs()
        {
            var slot = new FrameSlot { Index = 3, Frame = 1234, ElapsedMs = 199.6, Unchanged = true };
            var label = FrameSeriesRecorder.LabelFor(slot);
            Assert.AreEqual("#3 F1234 +200MS =", label);
            foreach (var c in label) Assert.IsTrue(BitmapFont.Has(c), "glyph for '" + c + "'");
            var px = new byte[20 * 10 * 4];
            BitmapFont.Draw(px, 20, 10, 0, 0, "1", 1, new Color32(255, 255, 255, 255));
            // '1' row 0 = 00100: lit at x=2 of the top row (top row = bottom-up row 9).
            Assert.AreEqual(255, px[(9 * 20 + 2) * 4]);
            Assert.AreEqual(0, px[(9 * 20 + 0) * 4]);
        }

        // ---- tool arguments / operation ----------------------------------------

        [Test]
        public void Create_ValidatesArguments()
        {
            CaptureSeriesOperation Make(string atMs = "[0]", string region = null, string metric = null, string metricRegion = null,
                float? scale = null, string startOn = null, string time = null, int poll = 1)
                => CaptureSeriesTool.Create(atMs, null, null, null, startOn, null, time, scale, region, metric, metricRegion, false, poll, 1000, 800, out _);
            Assert.IsNotNull(Make());
            Assert.IsNotNull(Make(region: "[10, 10, 100, 50]", metric: "diff", metricRegion: "[20, 20, 10, 10]"));
            void Refused(string why, CaptureSeriesOperation op) => Assert.IsNull(op, why);
            Refused("no schedule", Make(atMs: null));
            Refused("region shape", Make(region: "[1,2,3]"));
            Refused("region outside the screen", Make(region: "[2000, 10, 10, 10]"));
            Refused("metric_region without metric", Make(metricRegion: "[0,0,10,10]"));
            Refused("metric_region outside region", Make(region: "[0,0,10,10]", metric: "luminance", metricRegion: "[50,50,10,10]"));
            Refused("unknown metric", Make(metric: "contrast"));
            Refused("scale", Make(scale: 2f));
            Refused("time", Make(time: "wall"));
            Refused("poll", Make(poll: 0));
            Refused("start_on", Make(startOn: "{\"sleep\":1}"));
        }

        [Test]
        public void StartOn_ArmsTheScheduleAtTheFrameThatMadeItTrue()
        {
            var op = CaptureSeriesTool.Create("[0, 200]", null, null, null, "{\"frames\":3}", 5000, "gameplay", null, null, null, null,
                false, 1, 0, 0, out var refusal);
            Assert.IsNotNull(op, refusal);
            int frameShown = 0;
            op.Recorder.GrabOverride = _ => (Solid(4, 4, (byte)frameShown), 4, 4);
            op.Recorder.FolderOverride = folder;
            Assert.IsNotNull(PlayModeScheduler.Start(op, "series-0000-0001", out var error), error);
            for (int f = 100; f <= 112; f++)
            {
                frameShown = f - 1;
                PlayModeScheduler.TickAll(f, 0.1f, 0.1f, 1f, 4, 4, f * 0.1);
            }
            var status = PlayModeScheduler.Status("series-0000-0001");
            StringAssert.Contains("\"state\":\"succeeded\"", status);
            StringAssert.Contains("\"start_on\":{\"fired\":{\"path\":\"start_on\",\"type\":\"frames\",\"frame\":103", status);
            StringAssert.Contains("\"base\":{\"frame\":103", status);
            StringAssert.Contains("\"index\":0,\"at_ms\":0,\"frame\":103", status);
            StringAssert.Contains("\"index\":1,\"at_ms\":200,\"frame\":105", status);
        }

        [Test]
        public void StartOn_FiredIsFrozenWhenTheScheduleStarts()
        {
            var go = new GameObject("SeriesTest_Popup_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try
            {
                go.SetActive(false);
                var op = CaptureSeriesTool.Create("[0, 300]", null, null, null,
                    "{\"object\":{\"path\":\"" + go.name + "\",\"state\":\"active\"}}", 5000, "gameplay", null, null, null, null,
                    false, 1, 0, 0, out var refusal);
                Assert.IsNotNull(op, refusal);
                op.Recorder.GrabOverride = _ => (Solid(4, 4, 1), 4, 4);
                op.Recorder.FolderOverride = folder;
                Assert.IsNotNull(PlayModeScheduler.Start(op, "series-0000-0003", out var error), error);
                PlayModeScheduler.TickAll(1, 0.1f, 0.1f, 1f, 4, 4, 0.1);
                go.SetActive(true);    // during frame 1
                PlayModeScheduler.TickAll(2, 0.1f, 0.1f, 1f, 4, 4, 0.2);
                go.SetActive(false);   // the popup closes while the series runs
                for (int f = 3; f <= 8; f++) PlayModeScheduler.TickAll(f, 0.1f, 0.1f, 1f, 4, 4, f * 0.1);
                var status = PlayModeScheduler.Status("series-0000-0003");
                StringAssert.Contains("\"state\":\"succeeded\"", status);
                StringAssert.Contains("\"start_on\":{\"fired\":{\"path\":\"start_on\",\"type\":\"object\",\"frame\":1,", status);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void GameplaySchedule_ThatStopsAdvancing_TimesOutKeepingItsFrames()
        {
            var op = CaptureSeriesTool.Create("[0, 1000]", null, null, null, null, null, "gameplay", null, null, null, null,
                false, 1, 0, 0, out var refusal);
            Assert.IsNotNull(op, refusal);
            double wall = 0;
            op.WallClockOverride = () => wall;
            op.Recorder.GrabOverride = _ => (Solid(4, 4, 7), 4, 4);
            op.Recorder.FolderOverride = folder;
            Assert.AreEqual(CaptureSeriesTool.CaptureWallLimitMs(1000), op.CaptureLimitMs);
            Assert.IsNotNull(PlayModeScheduler.Start(op, "series-0000-0004", out var error), error);
            // Time.timeScale 0: game time never reaches the 1000 ms slot.
            for (int f = 1; f <= 3; f++) PlayModeScheduler.TickAll(f, 0f, 0.1f, 0f, 4, 4, 0);
            StringAssert.Contains("\"state\":\"running\"", PlayModeScheduler.Status("series-0000-0004"));
            wall = op.CaptureLimitMs + 1;
            PlayModeScheduler.TickAll(4, 0f, 0.1f, 0f, 4, 4, 0);
            var status = PlayModeScheduler.Status("series-0000-0004");
            StringAssert.Contains("\"state\":\"failed\"", status);
            StringAssert.Contains("\"outcome\":\"capture_timed_out\",\"error_code\":\"timed_out\"", status);
            StringAssert.Contains("\"captured\":1", status);
            StringAssert.Contains("\"index\":1,\"at_ms\":1000,\"captured\":false,\"dropped\":\"timed_out\"", status);
            Assert.IsNull(PlayModeScheduler.RunningWithResource(CaptureSeriesOperation.Resource), "the series lock is released");
        }

        [Test]
        public void StartOn_TimesOutWithNothingCaptured()
        {
            var op = CaptureSeriesTool.Create("[0]", null, null, null, "{\"frames\":1000}", 100, "gameplay", null, null, null, null,
                false, 1, 0, 0, out _);
            double wall = 0;
            op.WallClockOverride = () => wall;
            PlayModeScheduler.Start(op, "series-0000-0002", out _);
            PlayModeScheduler.TickAll(1, 0.1f, 0.1f, 1f, 4, 4, 0.1);
            wall = 150;
            PlayModeScheduler.TickAll(2, 0.1f, 0.1f, 1f, 4, 4, 0.2);
            var status = PlayModeScheduler.Status("series-0000-0002");
            StringAssert.Contains("\"state\":\"failed\"", status);
            StringAssert.Contains("\"outcome\":\"start_on_timed_out\",\"error_code\":\"timed_out\"", status);
            StringAssert.Contains("\"captured\":0", status);
        }

        [Test]
        public void Registry_CaptureSeriesIsReadOnlyPlaytest()
        {
            if (!BridgeToolRegistry.Contains("unity_open_mcp_inputsim_touch"))
                Assert.Ignore("Tool registry not scanned in this Editor session (batch -runTests).");
            Assert.IsTrue(BridgeToolRegistry.TryGet(CaptureSeriesTool.ToolName, out var info));
            Assert.IsFalse(info.IsMutating);
            Assert.AreEqual(GateMode.Off, info.Gate);
            Assert.AreEqual("playtest", info.Group);
        }

        [Test]
        public void Batch_CaptureSeriesIsRefusedAsServerPolled()
        {
            var result = BatchExecuteTool.Execute("{\"commands\":[{\"tool\":\"unity_senses_capture_series\",\"params\":{\"at_ms\":[0]}}]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("batch_step_requires_server_poll", result.ErrorCode);
        }

        [Test]
        public void EditMode_RefusesWithPlayModeRequired()
        {
            if (UnityEditor.EditorApplication.isPlaying) Assert.Ignore("Runs in edit mode.");
            StringAssert.Contains("play_mode_required", CaptureSeriesTool.CaptureSeries(at_ms: "[0]"));
        }
    }
}
