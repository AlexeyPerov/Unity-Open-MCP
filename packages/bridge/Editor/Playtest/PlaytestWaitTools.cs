// Playtest — wait in play mode until a condition holds (unity_open_mcp_playtest_wait).
//
// The tool parses the condition (PlaytestConditions.cs), then starts a
// PlaytestWaitOperation on the shared PlayModeScheduler and returns its id at
// once; MCP polls POST /playmode-operations to the terminal result (inline for
// short waits, as a `jobs` play-mode operation for long ones).
//
// Every tick the operation records console messages; every `poll_every_frames`
// ticks it evaluates the condition. It ends when the condition holds
// (`fired`), the wall-clock timeout passes (`timed_out`, state failed, with the
// last observed state of every leaf), `fail_on` sees an exception / error log
// (state failed, with the message and stack), or a leaf cannot be evaluated
// (`condition_error`). `time_scale` is applied on the first tick and restored
// by Cleanup — which the scheduler runs on every terminal path (success,
// timeout, cancel, exiting play mode, domain reload, Editor quit) — unless game
// code changed Time.timeScale meanwhile (then the game's value is kept and the
// result says so). One time-scaled wait at a time (exclusive resource).
using System;
using System.Diagnostics;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Playtest
{
    [BridgeToolType]
    public static class PlaytestWaitTools
    {
        internal const string ToolName = "unity_open_mcp_playtest_wait";
        internal const int DefaultTimeoutMs = 30_000;
        internal const int MaxTimeoutMs = 600_000;
        internal const int MaxPollEveryFrames = 600;
        internal const float MaxTimeScale = 100f;

        [BridgeTool(ToolName,
            Title = "Playtest: Wait Until",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = false,
            IdempotentHint = false,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "playtest")]
        [System.ComponentModel.Description(
            "Wait in play mode until a condition holds (log / ui / object / project_command / frames / " +
            "ms, or all / any of them) or a timeout, on the shared play-mode scheduler. Returns an " +
            "operation id; the MCP server polls it to the terminal result. Optional time_scale for the " +
            "wait only and fail_on exception. Play-mode only; gate-free.")]
        public static string Wait(
            string condition = null,
            int? timeout_ms = null,
            int poll_every_frames = 1,
            float? time_scale = null,
            string time = "gameplay",
            string[] fail_on = null,
            string operation_id = null)
        {
            if (!EditorApplication.isPlaying)
                return PlaytestJson.Error("play_mode_required",
                    "playtest_wait runs over real play-mode frames. Call unity_open_mcp_editor_set_state(state=\"play\") first.");

            var op = Create(condition, timeout_ms, poll_every_frames, time_scale, time, fail_on, out var refusal);
            if (op == null) return refusal;

            var snapshot = PlayModeScheduler.Start(op, operation_id, out var error);
            if (snapshot == null) return error;

            var sb = new StringBuilder(384);
            sb.Append("{\"status\":\"ok\",\"operation\":").Append(snapshot);
            sb.Append(",\"timeout_ms\":").Append(op.TimeoutMs);
            sb.Append(",\"agentNextSteps\":[").Append(BridgeJson.EscapeString(
                "The wait runs over real frames. Through MCP this call already waits for the terminal result " +
                "(or returns a jobs id for long waits); a direct bridge caller polls POST /playmode-operations " +
                "{\"action\":\"status\",\"operation_id\":\"" + op.Id + "\"}.")).Append("]}");
            return sb.ToString();
        }

        // Validate the arguments and build the operation (no scheduler, no play
        // mode needed — unit-tested). Null + `refusal` on bad input.
        internal static PlaytestWaitOperation Create(string condition, int? timeoutMs, int pollEvery, float? timeScale,
            string time, string[] failOn, out string refusal)
        {
            refusal = null;
            if (string.IsNullOrWhiteSpace(condition))
            {
                refusal = PlaytestJson.Error("invalid_arguments", "condition is required: {\"log\":…} | {\"ui\":…} | {\"object\":…} | {\"project_command\":…} | {\"frames\":N} | {\"ms\":N} | {\"all\":[…]} | {\"any\":[…]}.");
                return null;
            }
            int timeout = timeoutMs ?? DefaultTimeoutMs;
            if (timeout < 1 || timeout > MaxTimeoutMs)
            {
                refusal = PlaytestJson.Error("invalid_arguments", "timeout_ms must be 1.." + MaxTimeoutMs + ".");
                return null;
            }
            if (pollEvery < 1 || pollEvery > MaxPollEveryFrames)
            {
                refusal = PlaytestJson.Error("invalid_arguments", "poll_every_frames must be 1.." + MaxPollEveryFrames + ".");
                return null;
            }
            if (timeScale.HasValue && !(timeScale.Value > 0f && timeScale.Value <= MaxTimeScale))
            {
                refusal = PlaytestJson.Error("invalid_arguments", "time_scale must be > 0 and <= " + MaxTimeScale + " (Unity's Time.timeScale ceiling).");
                return null;
            }
            time = string.IsNullOrEmpty(time) ? "gameplay" : time;
            if (time != "gameplay" && time != "realtime")
            {
                refusal = PlaytestJson.Error("invalid_arguments", "time must be gameplay or realtime.");
                return null;
            }
            bool onException = false, onError = false;
            foreach (var f in failOn ?? Array.Empty<string>())
            {
                if (f == "exception") onException = true;
                else if (f == "error") onError = true;
                else
                {
                    refusal = PlaytestJson.Error("invalid_arguments", "fail_on accepts \"exception\" and \"error\".");
                    return null;
                }
            }
            var root = WaitConditionParser.Parse(condition, "condition", out var parseError);
            if (root == null)
            {
                var code = WaitConditionParser.SplitCode(parseError, out var message);
                refusal = PlaytestJson.Error(code, message);
                return null;
            }
            return new PlaytestWaitOperation(root, timeout, pollEvery, timeScale,
                time == "realtime" ? PlayModeTimeBase.Realtime : PlayModeTimeBase.Gameplay, onException, onError);
        }
    }

    internal sealed class PlaytestWaitOperation : PlayModeOperation
    {
        internal const string TimeScaleResource = "time_scale";

        private readonly ConditionWatcher watcher;
        private readonly float? requestedScale;
        private readonly Stopwatch wall = Stopwatch.StartNew();
        internal readonly int TimeoutMs;
        private bool timeScaleApplied;
        private float previousScale, finalScale;
        private bool restored;
        private string restoreNote;
        private string outcome;
        private int startFrame = -1, lastFrame = -1, ticks;
        private double elapsedMs, realtimeMs;
        private readonly bool gameplayTime;
        private double startPlayTime = double.NaN;
        private double startWallMs;

        // Test seam: wall-clock milliseconds since the call (null = Stopwatch).
        internal Func<double> WallClockOverride;
        // Test seam: Time.timeScale accessor (null = UnityEngine.Time).
        internal Func<float> ReadTimeScale;
        internal Action<float> WriteTimeScale;

        internal PlaytestWaitOperation(WaitCondition root, int timeoutMs, int pollEvery, float? timeScale,
            PlayModeTimeBase timeBase, bool failOnException, bool failOnError)
        {
            watcher = new ConditionWatcher(root, pollEvery) { FailOnException = failOnException, FailOnError = failOnError };
            TimeoutMs = timeoutMs;
            requestedScale = timeScale;
            // The scheduler clock is realtime so progress reads as wall time
            // against timeout_ms. For `ms` conditions, gameplay time is the
            // game's own clock (Time.time since the first tick) and realtime the
            // wall clock since the first tick (a hitch counts in full).
            TimeBase = PlayModeTimeBase.Realtime;
            gameplayTime = timeBase == PlayModeTimeBase.Gameplay;
            ExpectedDurationMs = timeoutMs;
            Phase = "waiting";
        }

        public override string Kind => PlaytestWaitTools.ToolName;
        public override string ExclusiveResource => requestedScale.HasValue ? TimeScaleResource : null;
        internal ConditionWatcher Watcher => watcher;
        internal string Outcome => outcome;
        private double WallMs => WallClockOverride?.Invoke() ?? wall.Elapsed.TotalMilliseconds;
        private float CurrentScale => ReadTimeScale?.Invoke() ?? Time.timeScale;
        private void SetScale(float v) { if (WriteTimeScale != null) WriteTimeScale(v); else Time.timeScale = v; }

        protected internal override bool Tick(PlayModeFrame frame)
        {
            if (startFrame < 0)
            {
                startFrame = frame.FrameCount;
                startPlayTime = frame.PlayTime;
                startWallMs = WallMs;
                watcher.Attach();
                if (requestedScale.HasValue)
                {
                    previousScale = CurrentScale;
                    SetScale(requestedScale.Value);
                    timeScaleApplied = true;
                }
            }
            lastFrame = frame.FrameCount;
            ticks++;
            realtimeMs = WallMs - startWallMs;
            elapsedMs = !gameplayTime ? realtimeMs
                : double.IsNaN(frame.PlayTime) || double.IsNaN(startPlayTime) ? frame.RealtimeMs * frame.TimeScale
                : (frame.PlayTime - startPlayTime) * 1000.0;

            bool met = watcher.Observe(new ConditionTick
            {
                Frame = frame.FrameCount,
                TickIndex = frame.TickIndex,
                ElapsedMs = elapsedMs,
                RealtimeMs = realtimeMs,
                PlayTime = frame.PlayTime,
            });

            if (watcher.FailLog != null)
            {
                bool exception = watcher.FailLog.Type == LogType.Exception;
                outcome = exception ? "exception" : "error_logged";
                FailureCode = exception ? "exception_thrown" : "error_logged";
                FailureMessage = (exception ? "The game threw: " : "The game logged an error: ") + PlaytestJson.Clip(watcher.FailLog.Message, 300);
                return true;
            }
            var conditionError = FirstError();
            if (conditionError != null)
            {
                outcome = "condition_error";
                FailureCode = "condition_error";
                FailureMessage = conditionError;
                return true;
            }
            if (met)
            {
                outcome = "fired";
                return true;
            }
            if (WallMs >= TimeoutMs)
            {
                outcome = "timed_out";
                FailureCode = "timed_out";
                FailureMessage = "The condition did not hold within " + TimeoutMs + " ms; see condition.last for what was observed.";
                return true;
            }
            return false;
        }

        private string FirstError()
        {
            foreach (var leaf in watcher.Root.Leaves())
                if (leaf.Error != null) return leaf.Error;
            return watcher.Root.Error;
        }

        protected internal override void Cleanup(PlayModeEndReason reason)
        {
            watcher.Detach();
            if (outcome == null) outcome = PlayModeScheduler.ReasonName(reason);
            if (!timeScaleApplied) return;
            float now;
            try { now = CurrentScale; }
            catch (Exception ex) { restoreNote = "Time.timeScale unreadable during cleanup: " + ex.Message; return; }
            if (Mathf.Approximately(now, requestedScale.Value))
            {
                SetScale(previousScale);
                restored = true;
                finalScale = previousScale;
            }
            else
            {
                // Game code changed the time scale during the wait (a pause menu,
                // its own slow-motion): its value wins over the one saved before.
                restored = false;
                finalScale = now;
                restoreNote = "Game code changed Time.timeScale to " + PlaytestJson.Num(now, 4) +
                              " during the wait; that value was kept instead of restoring " + PlaytestJson.Num(previousScale, 4) + ".";
            }
            timeScaleApplied = false;
        }

        protected internal override string BuildResult(PlayModeEndReason reason, string error)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"kind\":").Append(BridgeJson.EscapeString(Kind));
            sb.Append(",\"reason\":").Append(BridgeJson.EscapeString(PlayModeScheduler.ReasonName(reason)));
            sb.Append(",\"outcome\":").Append(BridgeJson.EscapeString(outcome ?? PlayModeScheduler.ReasonName(reason)));
            if (FailureCode != null && reason == PlayModeEndReason.Failed)
                sb.Append(",\"error_code\":").Append(BridgeJson.EscapeString(FailureCode));
            if (error != null) sb.Append(",\"error\":").Append(BridgeJson.EscapeString(error));
            sb.Append(",\"fired\":");
            if (outcome == "fired") watcher.AppendFired(sb); else sb.Append("null");
            sb.Append(",\"condition\":");
            watcher.Root.AppendJson(sb);
            sb.Append(",\"time\":").Append(BridgeJson.EscapeString(gameplayTime ? "gameplay" : "realtime"));
            sb.Append(",\"elapsed_ms\":").Append(PlaytestJson.Num(elapsedMs));
            sb.Append(",\"realtime_ms\":").Append(PlaytestJson.Num(realtimeMs));
            sb.Append(",\"wall_ms\":").Append(PlaytestJson.Num(WallMs));
            sb.Append(",\"timeout_ms\":").Append(TimeoutMs);
            sb.Append(",\"frames\":{\"start\":").Append(startFrame).Append(",\"end\":").Append(lastFrame)
              .Append(",\"ticks\":").Append(ticks).Append(",\"polls\":").Append(watcher.Polls)
              .Append(",\"poll_every_frames\":").Append(watcher.PollEvery).Append('}');
            if (requestedScale.HasValue)
            {
                sb.Append(",\"time_scale\":{\"requested\":").Append(PlaytestJson.Num(requestedScale.Value, 4));
                bool applied = startFrame >= 0;
                sb.Append(",\"applied\":").Append(applied ? "true" : "false");
                if (applied)
                {
                    sb.Append(",\"previous\":").Append(PlaytestJson.Num(previousScale, 4));
                    sb.Append(",\"restored\":").Append(restored ? "true" : "false");
                    sb.Append(",\"final\":").Append(PlaytestJson.Num(finalScale, 4));
                }
                if (restoreNote != null) sb.Append(",\"note\":").Append(BridgeJson.EscapeString(restoreNote));
                sb.Append('}');
            }
            if (watcher.FailLog != null)
            {
                sb.Append(",\"exception\":");
                ConditionWatcher.AppendLog(sb, watcher.FailLog);
            }
            watcher.AppendErrors(sb);
            if (watcher.PendingDropped > 0) sb.Append(",\"logs_dropped\":").Append(watcher.PendingDropped);
            sb.Append('}');
            return sb.ToString();
        }
    }
}
