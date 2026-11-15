// Playtest — frame probes: bounded sample buffers filled across play-mode
// frames (unity_open_mcp_playtest_probe).
//
// A probe is one ProbeOperation on the shared PlayModeScheduler — the same
// player-loop tick playtest_wait and capture_series run on — so the bridge
// owns its only per-frame hook: no Application.onBeforeRender handler, no
// AppDomain data. Every terminal path of the scheduler ends it and runs its
// Cleanup exactly once (stop, until met, max_samples reached, an error,
// exiting play mode, a domain reload, Editor quit), which detaches the until
// condition's log hook and drops the source's references.
//
// Records are domain-local. A finished probe's buffer stays readable (read /
// stop / list, edit mode included) until the next start of the same id, until
// MaxRetained records push the oldest finished one out, or until a domain
// reload drops every record. Entering play mode normally reloads the domain,
// so buffers from an earlier play session are gone then.
//
// Caps: MaxRunning concurrent probes (probe_capacity), max_samples up to
// MaxSamplesCap (the buffer never grows past it: reaching it ends the probe
// `max_samples_reached`), a sample's JSON up to ProbeValue.MaxChars (larger
// values become a truncation marker). Per-sample exceptions are recorded in
// the sample; MaxConsecutiveErrors in a row end the probe `error`
// (sample_errors). Every sample is timed (evaluation + serialisation): one
// over budget_ms is reported in the sample (cost_ms), OverBudgetLimit in a
// row — or one over HardBudgetFactor × budget — end the probe `error`
// (over_budget), so a probe cannot drag the frame rate down silently.
//
// fd hygiene (packages/bridge/AGENTS.md): no OS handles; timing reads
// Stopwatch.GetTimestamp.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace UnityOpenMcpBridge.Playtest
{
    internal enum ProbeState { Running, UntilMet, MaxSamplesReached, Stopped, PlayModeExited, Error }

    internal sealed class ProbeSample
    {
        internal int Seq;
        internal int Frame;
        internal double Time;       // Time.timeAsDouble at the sample (gameplay seconds)
        internal double Realtime;   // Time.realtimeSinceStartupAsDouble at the sample
        internal string ValueJson;  // null when Error is set
        internal string Error;
        internal double CostMs;
        internal bool OverBudget;

        internal void AppendJson(StringBuilder sb)
        {
            sb.Append("{\"seq\":").Append(Seq);
            sb.Append(",\"frame\":").Append(Frame);
            sb.Append(",\"time\":").Append(PlaytestJson.Num(Time, 4));
            sb.Append(",\"realtime\":").Append(PlaytestJson.Num(Realtime, 4));
            if (Error != null) sb.Append(",\"error\":").Append(BridgeJson.EscapeString(Error));
            else sb.Append(",\"value\":").Append(ValueJson ?? "null");
            if (OverBudget) sb.Append(",\"cost_ms\":").Append(PlaytestJson.Num(CostMs, 3));
            sb.Append('}');
        }
    }

    internal sealed class ProbeRecord
    {
        internal string Id;
        internal ProbeSource Source;
        internal int EveryFrames;
        internal int MaxSamples;
        internal double BudgetMs;
        internal bool GameplayTime = true;
        internal WaitCondition Until;
        internal ConditionWatcher Watcher;

        internal ProbeState State = ProbeState.Running;
        internal string ReasonCode;
        internal string ReasonMessage;
        internal string FiredJson;
        internal bool StopRequested;
        internal string OperationId;
        internal DateTime StartedUtc = DateTime.UtcNow;
        internal DateTime EndedUtc;
        internal int StartFrame = -1, LastFrame = -1, Ticks;

        internal readonly List<ProbeSample> Samples = new List<ProbeSample>();
        internal int Errors, ConsecutiveErrors;
        internal string LastError;
        internal int OverBudget, ConsecutiveOverBudget;
        internal double CostTotalMs, CostMaxMs;
        internal int NumericCount;
        internal double NumericMin = double.MaxValue, NumericMax = double.MinValue, NumericSum;

        internal bool Running => State == ProbeState.Running;

        internal static string StateName(ProbeState state)
        {
            switch (state)
            {
                case ProbeState.Running: return "running";
                case ProbeState.UntilMet: return "until_met";
                case ProbeState.MaxSamplesReached: return "max_samples_reached";
                case ProbeState.Stopped: return "stopped";
                case ProbeState.PlayModeExited: return "play_mode_exited";
                default: return "error";
            }
        }

        // Record the end (first writer wins).
        internal void End(ProbeState state, string code, string message)
        {
            if (!Running) return;
            State = state;
            ReasonCode = code;
            ReasonMessage = message;
            EndedUtc = DateTime.UtcNow;
        }

        internal void AppendReason(StringBuilder sb)
        {
            if (ReasonCode == null) { sb.Append("null"); return; }
            sb.Append("{\"code\":").Append(BridgeJson.EscapeString(ReasonCode));
            sb.Append(",\"message\":").Append(BridgeJson.EscapeString(ReasonMessage ?? ""));
            if (FiredJson != null) sb.Append(",\"fired\":").Append(FiredJson);
            sb.Append('}');
        }

        // The probe as every action reports it.
        internal void AppendInfo(StringBuilder sb)
        {
            sb.Append("{\"id\":").Append(BridgeJson.EscapeString(Id));
            sb.Append(",\"state\":").Append(BridgeJson.EscapeString(StateName(State)));
            sb.Append(",\"reason\":"); AppendReason(sb);
            sb.Append(",\"source\":"); Source.AppendJson(sb);
            sb.Append(",\"every_frames\":").Append(EveryFrames);
            sb.Append(",\"max_samples\":").Append(MaxSamples);
            sb.Append(",\"budget_ms\":").Append(PlaytestJson.Num(BudgetMs, 3));
            sb.Append(",\"time\":").Append(GameplayTime ? "\"gameplay\"" : "\"realtime\"");
            sb.Append(",\"until\":");
            if (Until == null) sb.Append("null"); else Until.AppendJson(sb);
            sb.Append(",\"samples\":").Append(Samples.Count);
            sb.Append(",\"errors\":").Append(Errors);
            sb.Append(",\"frames\":{\"start\":").Append(StartFrame).Append(",\"last\":").Append(LastFrame).Append(",\"ticks\":").Append(Ticks).Append('}');
            sb.Append(",\"hooked\":").Append(Running ? "true" : "false");
            sb.Append(",\"operation_id\":").Append(OperationId == null ? "null" : BridgeJson.EscapeString(OperationId));
            sb.Append('}');
        }

        internal void AppendSummary(StringBuilder sb)
        {
            sb.Append("{\"samples\":").Append(Samples.Count);
            sb.Append(",\"first\":");
            if (Samples.Count > 0) Samples[0].AppendJson(sb); else sb.Append("null");
            sb.Append(",\"last\":");
            if (Samples.Count > 0) Samples[Samples.Count - 1].AppendJson(sb); else sb.Append("null");
            sb.Append(",\"numeric\":");
            if (NumericCount > 0)
            {
                sb.Append("{\"count\":").Append(NumericCount);
                sb.Append(",\"min\":").Append(PlaytestJson.Num(NumericMin, 4));
                sb.Append(",\"max\":").Append(PlaytestJson.Num(NumericMax, 4));
                sb.Append(",\"mean\":").Append(PlaytestJson.Num(NumericSum / NumericCount, 4)).Append('}');
            }
            else sb.Append("null");
            sb.Append(",\"errors\":{\"count\":").Append(Errors);
            sb.Append(",\"last\":").Append(LastError == null ? "null" : BridgeJson.EscapeString(LastError)).Append('}');
            int n = Samples.Count;
            sb.Append(",\"cost\":{\"mean_ms\":").Append(PlaytestJson.Num(n > 0 ? CostTotalMs / n : 0, 3));
            sb.Append(",\"max_ms\":").Append(PlaytestJson.Num(CostMaxMs, 3));
            sb.Append(",\"over_budget\":").Append(OverBudget);
            sb.Append(",\"budget_ms\":").Append(PlaytestJson.Num(BudgetMs, 3)).Append('}');
            sb.Append('}');
        }
    }

    // One probe on the play-mode scheduler.
    internal sealed class ProbeOperation : PlayModeOperation
    {
        // Dropped once the result is built: the scheduler keeps finished
        // operations for its retention window, the buffer belongs to the registry.
        private ProbeRecord record;
        private double startPlayTime = double.NaN;
        private double startWallMs;

        // Test seams.
        internal Func<double> RealtimeClock;     // seconds (null = Time.realtimeSinceStartupAsDouble)
        internal Func<double> WallClockMs;       // ms (null = Stopwatch)
        internal Func<double> CostOverrideMs;    // a sample's measured cost (null = Stopwatch)

        private static readonly Stopwatch wall = Stopwatch.StartNew();

        internal ProbeOperation(ProbeRecord record)
        {
            this.record = record;
            TimeBase = PlayModeTimeBase.Realtime;
            Phase = "sampling";
        }

        public override string Kind => PlaytestProbeTools.ToolName;
        // A probe waits through a paused Editor or a zero time scale.
        public override bool CanStall => false;

        private double WallMs => WallClockMs?.Invoke() ?? wall.Elapsed.TotalMilliseconds;
        private double RealtimeNow => RealtimeClock?.Invoke() ?? Time.realtimeSinceStartupAsDouble;

        protected internal override bool Tick(PlayModeFrame frame)
        {
            if (record == null || !record.Running) return true;
            if (record.StartFrame < 0)
            {
                record.StartFrame = frame.FrameCount;
                startPlayTime = frame.PlayTime;
                startWallMs = WallMs;
                record.Watcher?.Attach();
            }
            record.LastFrame = frame.FrameCount;
            record.Ticks++;

            if (frame.TickIndex % record.EveryFrames == 0) Sample(frame);
            if (!record.Running) return true;

            if (record.Watcher != null)
            {
                double realtimeMs = WallMs - startWallMs;
                double elapsedMs = !record.GameplayTime ? realtimeMs
                    : double.IsNaN(frame.PlayTime) || double.IsNaN(startPlayTime) ? frame.RealtimeMs * frame.TimeScale
                    : (frame.PlayTime - startPlayTime) * 1000.0;
                bool met = record.Watcher.Observe(new ConditionTick
                {
                    Frame = frame.FrameCount, TickIndex = frame.TickIndex, ElapsedMs = elapsedMs, RealtimeMs = realtimeMs, PlayTime = frame.PlayTime,
                });
                var conditionError = FirstError(record.Until);
                if (conditionError != null)
                {
                    record.End(ProbeState.Error, "until_error", "The until condition could not be evaluated: " + conditionError);
                    return true;
                }
                if (met)
                {
                    var fired = new StringBuilder();
                    record.Watcher.AppendFired(fired);
                    record.FiredJson = fired.ToString();
                    var node = record.Watcher.FiredNode();
                    record.End(ProbeState.UntilMet, "until_met",
                        "The until condition held (" + (node?.Path ?? "until") + ") at frame " + frame.FrameCount + ".");
                    return true;
                }
            }
            if (record.Samples.Count >= record.MaxSamples)
            {
                record.End(ProbeState.MaxSamplesReached, "max_samples_reached", "The buffer holds max_samples (" + record.MaxSamples + ") samples.");
                return true;
            }
            return false;
        }

        private static string FirstError(WaitCondition root)
        {
            if (root == null) return null;
            foreach (var leaf in root.Leaves())
                if (leaf.Error != null) return leaf.Error;
            return root.Error;
        }

        private void Sample(PlayModeFrame frame)
        {
            long t0 = Stopwatch.GetTimestamp();
            string valueJson = null, error = null;
            double? number = null;
            try
            {
                if (record.Source.TargetLost) throw new MissingReferenceException("The probed object was destroyed.");
                var value = record.Source.Read();
                valueJson = ProbeValue.ToJson(value, out number);
            }
            catch (Exception ex)
            {
                var cause = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                error = cause.GetType().Name + ": " + PlaytestJson.Clip(cause.Message, 300);
            }
            double cost = CostOverrideMs?.Invoke() ?? (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

            bool over = cost > record.BudgetMs;
            var sample = new ProbeSample
            {
                Seq = record.Samples.Count, Frame = frame.FrameCount, Time = frame.PlayTime, Realtime = RealtimeNow,
                ValueJson = valueJson, Error = error, CostMs = cost, OverBudget = over,
            };
            record.Samples.Add(sample);
            record.CostTotalMs += cost;
            if (cost > record.CostMaxMs) record.CostMaxMs = cost;
            if (over) { record.OverBudget++; record.ConsecutiveOverBudget++; }
            else record.ConsecutiveOverBudget = 0;
            if (error != null)
            {
                record.Errors++;
                record.ConsecutiveErrors++;
                record.LastError = error;
            }
            else
            {
                record.ConsecutiveErrors = 0;
                if (number.HasValue)
                {
                    record.NumericCount++;
                    record.NumericSum += number.Value;
                    if (number.Value < record.NumericMin) record.NumericMin = number.Value;
                    if (number.Value > record.NumericMax) record.NumericMax = number.Value;
                }
            }

            if (record.ConsecutiveErrors >= ProbeRegistry.MaxConsecutiveErrors)
            {
                record.End(ProbeState.Error, "sample_errors",
                    record.ConsecutiveErrors + " samples in a row failed; the probe was stopped. Last: " + error);
                return;
            }
            if (cost >= record.BudgetMs * ProbeRegistry.HardBudgetFactor || record.ConsecutiveOverBudget >= ProbeRegistry.OverBudgetLimit)
            {
                record.End(ProbeState.Error, "over_budget",
                    "A sample took " + PlaytestJson.Num(cost, 3) + " ms against budget_ms " + PlaytestJson.Num(record.BudgetMs, 3) +
                    (record.ConsecutiveOverBudget >= ProbeRegistry.OverBudgetLimit ? " (" + record.ConsecutiveOverBudget + " in a row)" : "") +
                    "; the probe was stopped so it cannot slow the game down. Sample less often (every_frames), read less, or raise budget_ms.");
            }
        }

        protected internal override double ProgressMs(double clockElapsedMs) => clockElapsedMs;

        protected internal override void AppendProgress(StringBuilder sb)
        {
            if (record == null) return;
            sb.Append(",\"probe\":").Append(BridgeJson.EscapeString(record.Id)).Append(",\"samples\":").Append(record.Samples.Count);
        }

        protected internal override void Cleanup(PlayModeEndReason reason)
        {
            if (record == null) return;
            record.Watcher?.Detach();
            switch (reason)
            {
                case PlayModeEndReason.Cancelled:
                    record.End(ProbeState.Stopped, record.StopRequested ? "stop_requested" : "cancelled",
                        record.StopRequested ? "Stopped by the stop action." : "Cancelled through the play-mode operation (jobs / POST /playmode-operations).");
                    break;
                case PlayModeEndReason.PlayModeExited:
                    record.End(ProbeState.PlayModeExited, "play_mode_exited", "Play mode ended; the probe's hook was removed. The buffer stays readable.");
                    break;
                case PlayModeEndReason.DomainReload:
                case PlayModeEndReason.EditorQuit:
                    record.End(ProbeState.Stopped, PlayModeScheduler.ReasonName(reason), "The domain is unloading; the probe was removed.");
                    break;
                case PlayModeEndReason.Failed:
                case PlayModeEndReason.Stalled:
                    record.End(ProbeState.Error, "probe_failed", "The probe's tick failed.");
                    break;
            }
            record.Source.Release();
        }

        protected internal override string BuildResult(PlayModeEndReason reason, string error)
        {
            if (record == null) return "{\"kind\":" + BridgeJson.EscapeString(Kind) + "}";
            if (error != null && record.ReasonCode == "probe_failed") record.ReasonMessage = "The probe's tick failed: " + error;
            var sb = new StringBuilder(512);
            sb.Append("{\"kind\":").Append(BridgeJson.EscapeString(Kind));
            sb.Append(",\"probe\":"); record.AppendInfo(sb);
            sb.Append(",\"summary\":"); record.AppendSummary(sb);
            sb.Append('}');
            record = null;
            return sb.ToString();
        }
    }

    internal static class ProbeRegistry
    {
        internal const int MaxRunning = 8;
        internal const int MaxRetained = 32;
        internal const int MaxSamplesCap = 5000;
        internal const int DefaultMaxSamples = 200;
        internal const int MaxEveryFrames = 600;
        internal const int MaxConsecutiveErrors = 5;
        internal const double DefaultBudgetMs = 5;
        internal const double MinBudgetMs = 0.1;
        internal const double MaxBudgetMs = 100;
        internal const int OverBudgetLimit = 3;
        internal const double HardBudgetFactor = 10;
        internal const int DefaultReadMax = 50;
        internal const int MaxReadMax = 500;

        // Start order; ids are unique.
        private static readonly List<ProbeRecord> records = new List<ProbeRecord>();

        // Test seam: applied to each new operation.
        internal static Action<ProbeOperation> ConfigureForTests;

        internal static IReadOnlyList<ProbeRecord> All => records;

        internal static ProbeRecord Get(string id)
        {
            foreach (var r in records) if (r.Id == id) return r;
            return null;
        }

        internal static int RunningCount
        {
            get { int n = 0; foreach (var r in records) if (r.Running) n++; return n; }
        }

        // Before the source is bound (compiling an expression is the slow part).
        internal static string RefuseStart(string id)
        {
            var existing = Get(id);
            if (existing != null && existing.Running)
                return PlaytestJson.Error("probe_running",
                    "Probe '" + id + "' is running. Stop it first ({\"action\":\"stop\",\"id\":\"" + id + "\"}) or start under another id.");
            if (RunningCount >= MaxRunning)
                return PlaytestJson.Error("probe_capacity",
                    MaxRunning + " probes are running; at most " + MaxRunning + " run at once. Stop one (list shows them).");
            return null;
        }

        // Register a bound probe and start its operation. Null + `error` (a
        // complete error JSON) when the scheduler refuses it.
        internal static ProbeOperation Start(ProbeRecord record, out string error)
        {
            error = RefuseStart(record.Id);
            if (error != null) return null;
            var op = new ProbeOperation(record);
            ConfigureForTests?.Invoke(op);
            var snapshot = PlayModeScheduler.Start(op, "probe-" + Guid.NewGuid().ToString("N"), out error);
            if (snapshot == null) return null;
            record.OperationId = op.Id;
            // The new start of an id replaces its finished record; the oldest
            // finished records make room for the rest.
            var previous = Get(record.Id);
            if (previous != null) records.Remove(previous);
            while (records.Count >= MaxRetained)
            {
                var oldest = records.Find(r => !r.Running);
                if (oldest == null) break;
                records.Remove(oldest);
            }
            records.Add(record);
            return op;
        }

        internal static void Stop(ProbeRecord record)
        {
            if (!record.Running) return;
            record.StopRequested = true;
            if (record.OperationId != null) PlayModeScheduler.Cancel(record.OperationId);
            if (!record.Running) return;
            // A record whose operation is gone (never expected) still ends and lets go.
            record.End(ProbeState.Stopped, "stop_requested", "Stopped by the stop action.");
            record.Watcher?.Detach();
            record.Source.Release();
        }

        internal static void ResetForTests()
        {
            records.Clear();
            ConfigureForTests = null;
        }
    }
}
