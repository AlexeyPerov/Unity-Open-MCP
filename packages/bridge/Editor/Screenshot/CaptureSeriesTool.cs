// Senses — frame-scheduled capture series (unity_senses_capture_series).
//
// Captures the composed game frame (the play-mode view's render target, native
// size by default — the same frame unity_senses_screenshot view "composed"
// returns in play mode) at scheduled frames inside the player loop, without a
// round trip per frame: `at_ms`, `every_ms` + `duration_ms`, or `at_frames`,
// relative to the call or to a `start_on` condition (the playtest_wait
// condition shapes). Optional `region` crop, `scale`, per-frame `metric`
// (mean luminance / diff vs the previous capture) and a `contact_sheet` (one
// tiled PNG with frame/time labels). Starts a CaptureSeriesOperation on the
// shared play-mode scheduler and returns its id; MCP polls it to the terminal
// result (inline when short, as a `jobs` play-mode operation when long).
using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.Playtest;

namespace UnityOpenMcpBridge.Screenshot
{
    [BridgeToolType]
    public static class CaptureSeriesTool
    {
        internal const string ToolName = "unity_senses_capture_series";
        internal const int DefaultStartTimeoutMs = 30_000;
        internal const int MaxStartTimeoutMs = 600_000;
        internal const double EncodeBudgetMsPerTick = 40;

        [BridgeTool(ToolName,
            Title = "Senses: Capture Series",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = true,
            IdempotentHint = false,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "playtest")]
        [System.ComponentModel.Description(
            "Capture the composed game frame at scheduled play-mode frames (at_ms, every_ms + duration_ms, " +
            "or at_frames; relative to the call or a start_on condition) inside the player loop. Native " +
            "size by default; optional region crop, scale, per-frame luminance / diff metric and a " +
            "labelled contact sheet. Returns an operation id; the MCP server polls it. Play-mode only.")]
        public static string CaptureSeries(
            string at_ms = null,
            int? every_ms = null,
            int? duration_ms = null,
            string at_frames = null,
            string start_on = null,
            int? start_timeout_ms = null,
            string time = "gameplay",
            float? scale = null,
            string region = null,
            string metric = null,
            string metric_region = null,
            bool contact_sheet = false,
            int poll_every_frames = 1,
            string operation_id = null)
        {
            if (!EditorApplication.isPlaying)
                return PlaytestJson.Error("play_mode_required",
                    "capture_series captures inside the play-mode player loop. Call unity_open_mcp_editor_set_state(state=\"play\") first; " +
                    "in edit mode use unity_senses_screenshot.");
            if (!GameViewAdapter.IsGameViewAvailable(out var code, out var message))
                return PlaytestJson.Error(code, message);
            if (GameViewAdapter.TargetTexture(GameViewAdapter.MainView()) == null)
                return PlaytestJson.Error("render_target_unavailable",
                    "The play-mode view has not rendered a frame (no Game view / Simulator open, or its tab is hidden). " +
                    "Open the Game view (or game_view set_resolution brings it to the front) and retry.");

            var screen = ScreenshotService.DescribeGameView();
            var op = Create(at_ms, every_ms, duration_ms, at_frames, start_on, start_timeout_ms, time, scale, region, metric,
                metric_region, contact_sheet, poll_every_frames, screen.ScreenWidth, screen.ScreenHeight, out var refusal);
            if (op == null) return refusal;
            op.Context = screen;

            var snapshot = PlayModeScheduler.Start(op, operation_id, out var error);
            if (snapshot == null) return error;

            var sb = new StringBuilder(384);
            sb.Append("{\"status\":\"ok\",\"operation\":").Append(snapshot);
            sb.Append(",\"requested\":").Append(op.Recorder.Schedule.Slots.Count);
            sb.Append(",\"agentNextSteps\":[").Append(BridgeJson.EscapeString(
                "The series runs over real frames. Through MCP this call already waits for the terminal result " +
                "(or returns a jobs id for long series); a direct bridge caller polls POST /playmode-operations " +
                "{\"action\":\"status\",\"operation_id\":\"" + op.Id + "\"}.")).Append("]}");
            return sb.ToString();
        }

        // Validate and build (unit-tested; no scheduler). `screenW/H` is the
        // game's screen for region checks (0 = unknown, unchecked).
        internal static CaptureSeriesOperation Create(string atMs, int? everyMs, int? durationMs, string atFrames, string startOn,
            int? startTimeoutMs, string time, float? scale, string region, string metric, string metricRegion, bool contactSheet,
            int pollEvery, int screenW, int screenH, out string refusal)
        {
            refusal = null;
            var schedule = FrameSchedule.Parse(atMs, everyMs, durationMs, atFrames, FrameSchedule.MaxFrames, "", out var scheduleError);
            if (schedule == null) { refusal = PlaytestJson.Error("invalid_arguments", scheduleError); return null; }

            time = string.IsNullOrEmpty(time) ? "gameplay" : time;
            if (time != "gameplay" && time != "realtime")
            { refusal = PlaytestJson.Error("invalid_arguments", "time must be gameplay or realtime."); return null; }
            if (scale.HasValue && !(scale.Value >= 0.05f && scale.Value <= 1f))
            { refusal = PlaytestJson.Error("invalid_arguments", "scale must be 0.05..1 (1 = native size)."); return null; }
            if (pollEvery < 1 || pollEvery > PlaytestWaitTools.MaxPollEveryFrames)
            { refusal = PlaytestJson.Error("invalid_arguments", "poll_every_frames must be 1.." + PlaytestWaitTools.MaxPollEveryFrames + "."); return null; }
            int startTimeout = startTimeoutMs ?? DefaultStartTimeoutMs;
            if (startTimeout < 1 || startTimeout > MaxStartTimeoutMs)
            { refusal = PlaytestJson.Error("invalid_arguments", "start_timeout_ms must be 1.." + MaxStartTimeoutMs + "."); return null; }

            var options = new FrameSeriesOptions { Scale = scale ?? 1f, ContactSheet = contactSheet, Label = "series" };
            if (region != null)
            {
                if (!FrameSchedule.TryRect(region, out var r, out var rectError, "region")) { refusal = PlaytestJson.Error("invalid_arguments", rectError); return null; }
                if (screenW > 0 && screenH > 0 && (r.x >= screenW || r.y >= screenH))
                { refusal = PlaytestJson.Error("invalid_arguments", "region starts outside the game screen (" + screenW + "x" + screenH + ")."); return null; }
                options.Region = r;
            }
            if (metric != null)
            {
                switch (metric)
                {
                    case "luminance": options.Luminance = true; break;
                    case "diff": options.Luminance = true; options.Diff = true; break;
                    default: refusal = PlaytestJson.Error("invalid_arguments", "metric must be luminance or diff (diff also reports luminance)."); return null;
                }
            }
            if (metricRegion != null)
            {
                if (metric == null) { refusal = PlaytestJson.Error("invalid_arguments", "metric_region needs metric."); return null; }
                if (!FrameSchedule.TryRect(metricRegion, out var m, out var rectError, "metric_region")) { refusal = PlaytestJson.Error("invalid_arguments", rectError); return null; }
                if (options.Region.HasValue)
                {
                    var r = options.Region.Value;
                    if (m.x + m.width <= r.x || m.y + m.height <= r.y || m.x >= r.x + r.width || m.y >= r.y + r.height)
                    { refusal = PlaytestJson.Error("invalid_arguments", "metric_region does not overlap region."); return null; }
                }
                options.MetricRegion = m;
            }

            WaitCondition trigger = null;
            if (!string.IsNullOrWhiteSpace(startOn))
            {
                trigger = WaitConditionParser.Parse(startOn, "start_on", out var parseError);
                if (trigger == null)
                {
                    var errorCode = WaitConditionParser.SplitCode(parseError, out var parseMessage);
                    refusal = PlaytestJson.Error(errorCode, parseMessage);
                    return null;
                }
            }
            return new CaptureSeriesOperation(schedule, options, trigger, startTimeout, pollEvery,
                time == "realtime" ? PlayModeTimeBase.Realtime : PlayModeTimeBase.Gameplay);
        }
    }

    internal sealed class CaptureSeriesOperation : PlayModeOperation
    {
        internal const string Resource = "capture_series";

        internal readonly FrameSeriesRecorder Recorder;
        private readonly ConditionWatcher watcher;
        private readonly bool hasTrigger;
        private readonly int startTimeoutMs;
        private readonly System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
        internal ScreenshotService.GameViewContext Context;
        private ConditionTick? previous;
        private int startFrame = -1, lastFrame = -1, ticks;
        private string outcome;

        // Test seam: wall-clock ms since the call.
        internal Func<double> WallClockOverride;

        internal CaptureSeriesOperation(FrameSchedule schedule, FrameSeriesOptions options, WaitCondition trigger,
            int startTimeoutMs, int pollEvery, PlayModeTimeBase timeBase)
        {
            Recorder = new FrameSeriesRecorder(schedule, options, null);
            watcher = new ConditionWatcher(trigger, pollEvery);
            hasTrigger = trigger != null;
            this.startTimeoutMs = startTimeoutMs;
            TimeBase = timeBase;
            ExpectedDurationMs = schedule.LastMs;
            Phase = hasTrigger ? "waiting for start_on" : "capturing";
        }

        public override string Kind => CaptureSeriesTool.ToolName;
        public override string ExclusiveResource => Resource;
        internal ConditionWatcher Watcher => watcher;
        internal string Outcome => outcome;
        private double WallMs => WallClockOverride?.Invoke() ?? wall.Elapsed.TotalMilliseconds;

        protected internal override bool Tick(PlayModeFrame frame)
        {
            if (startFrame < 0)
            {
                startFrame = frame.FrameCount;
                Recorder.OperationId = Id;
                watcher.Attach();
            }
            lastFrame = frame.FrameCount;
            ticks++;
            var now = new ConditionTick
            {
                Frame = frame.FrameCount,
                TickIndex = frame.TickIndex,
                ElapsedMs = frame.ElapsedMs,
                RealtimeMs = frame.RealtimeMs,
                PlayTime = frame.PlayTime,
            };

            if (!Recorder.Armed)
            {
                if (!hasTrigger) Recorder.Arm(now);
                else
                {
                    bool met = watcher.Observe(now);
                    foreach (var leaf in watcher.Root.Leaves())
                        if (leaf.Error != null)
                        {
                            outcome = "condition_error";
                            FailureCode = "condition_error";
                            FailureMessage = leaf.Error;
                            return true;
                        }
                    if (met)
                    {
                        // t = 0 is the frame the trigger names (the frame that made it
                        // true); its image is still in the view when that frame just ran.
                        var fire = watcher.FiredNode()?.Fire ?? watcher.Root.Fire;
                        var at = watcher.TickAt(fire.Frame);
                        Recorder.Arm(at ?? new ConditionTick { Frame = fire.Frame, ElapsedMs = fire.ElapsedMs, RealtimeMs = fire.RealtimeMs, PlayTime = fire.PlayTime });
                        Phase = "capturing";
                    }
                    else if (WallMs >= startTimeoutMs)
                    {
                        outcome = "start_on_timed_out";
                        FailureCode = "timed_out";
                        FailureMessage = "start_on did not hold within " + startTimeoutMs + " ms; nothing was captured. See start_on.condition.last.";
                        return true;
                    }
                }
            }
            else if (hasTrigger) watcher.Observe(now);   // keep recording errors / history

            if (Recorder.Armed && !Recorder.CapturesDone)
                Recorder.Observe(now, previous, frame.ScreenWidth, frame.ScreenHeight);
            previous = now;

            if (Recorder.CaptureError != null && Recorder.Taken == 0)
            {
                outcome = "capture_failed";
                FailureCode = "capture_failed";
                FailureMessage = Recorder.CaptureError;
                return true;
            }
            if (!Recorder.CapturesDone) return false;
            Phase = "encoding";
            bool done = Recorder.EncodeStep(CaptureSeriesTool.EncodeBudgetMsPerTick);
            if (done) outcome = Recorder.CaptureError != null ? "partial" : "completed";
            return done;
        }

        protected internal override void Cleanup(PlayModeEndReason reason)
        {
            watcher.Detach();
            if (outcome == null) outcome = PlayModeScheduler.ReasonName(reason);
            // Keep the evidence captured so far when the series was cut short
            // in a live Editor; a reload / quit drops it (no slow teardown).
            if (reason == PlayModeEndReason.Cancelled || reason == PlayModeEndReason.PlayModeExited || reason == PlayModeEndReason.Failed)
            {
                try { Recorder.FlushPending(); } catch (Exception) { /* best effort */ }
            }
            Recorder.Release();
        }

        protected internal override string BuildResult(PlayModeEndReason reason, string error)
        {
            var sb = new StringBuilder(2048);
            sb.Append("{\"kind\":").Append(BridgeJson.EscapeString(Kind));
            sb.Append(",\"reason\":").Append(BridgeJson.EscapeString(PlayModeScheduler.ReasonName(reason)));
            sb.Append(",\"outcome\":").Append(BridgeJson.EscapeString(outcome ?? PlayModeScheduler.ReasonName(reason)));
            if (FailureCode != null && reason == PlayModeEndReason.Failed)
                sb.Append(",\"error_code\":").Append(BridgeJson.EscapeString(FailureCode));
            if (error != null) sb.Append(",\"error\":").Append(BridgeJson.EscapeString(error));
            sb.Append(",\"series\":");
            Recorder.AppendJson(sb, TimeBase == PlayModeTimeBase.Gameplay ? "gameplay" : "realtime");
            if (hasTrigger)
            {
                sb.Append(",\"start_on\":{\"fired\":");
                if (Recorder.Armed) watcher.AppendFired(sb); else sb.Append("null");
                sb.Append(",\"condition\":");
                watcher.Root.AppendJson(sb);
                sb.Append('}');
            }
            sb.Append(",\"frames\":{\"start\":").Append(startFrame).Append(",\"end\":").Append(lastFrame).Append(",\"ticks\":").Append(ticks).Append('}');
            if (Context != null)
            {
                sb.Append(",\"capturedAt\":");
                ScreenshotService.AppendContext(sb, Context);
            }
            watcher.AppendErrors(sb);
            sb.Append('}');
            return sb.ToString();
        }
    }
}
