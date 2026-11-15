// Shared play-mode scheduler.
//
// One bridge-side owner for operations that must run across real play-mode
// frames instead of inside one main-thread dispatch (a held finger, a gesture
// that lasts seconds, a wait-until, a timed capture series). A tool starts
// an operation and returns immediately; the scheduler ticks it once per player
// loop frame from a system inserted at the START of EarlyUpdate — before the
// legacy Input Manager and the Input System process input — so input injected in
// a tick is observed by the same frame's Update. Status and cancellation go
// through POST /playmode-operations (MCP polls it, either inline for short
// operations or from the generic `playmode_operation` jobs adapter).
//
// Every terminal path runs the operation's Cleanup exactly once: completion,
// failure, cancel, exiting play mode, a domain reload, and Editor quit. Cleanup
// is where held input is released, so no path leaves a stuck touch or button.
//
// Records are domain-local. A reload drops them (after Cleanup); MCP then sees
// `operation_not_found` and reports the job as lost, never restarts it.
//
// Zero progress ends an operation; slow progress never does. Every tick asks the
// operation whether it moved forward (by default: its clock advanced, so a
// gameplay-clock operation stands still while Time.timeScale is 0). Wall-clock
// time without progress accumulates in steps clamped to MaxStallStepMs (a
// main-thread hitch does not count in full), also from EditorApplication.update,
// which keeps running while the Editor is paused and the player loop is not.
// After StallWindowMs the operation ends `failed` with reason `stalled`, its own
// StalledCode and `stall.reason` (editor_paused | time_scale_zero | no_frames |
// no_progress); its cleanup keeps what it has (frames already captured).
//
// fd hygiene (packages/bridge/AGENTS.md): this scheduler owns no OS handles —
// no Timer, WaitHandle, socket or stream. The player-loop system is removed in
// beforeAssemblyReload so the native loop never calls into an unloaded domain.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.LowLevel;

namespace UnityOpenMcpBridge
{
    internal enum PlayModeTimeBase { Realtime, Gameplay }

    internal enum PlayModeEndReason { Completed, Failed, Cancelled, PlayModeExited, DomainReload, EditorQuit, Stalled }

    // One scheduler tick as seen by an operation.
    internal struct PlayModeFrame
    {
        public int FrameCount;
        public int TickIndex;       // 0 on the operation's first tick
        public double ElapsedMs;    // in the operation's time base; 0 on the first tick
        public double RealtimeMs;   // unscaled, per-frame step clamped (pauses/hitches do not skip keyframes)
        public float TimeScale;
        public int ScreenWidth;
        public int ScreenHeight;
        public double PlayTime;     // Time.time of this frame (seconds)
    }

    internal abstract class PlayModeOperation
    {
        public string Id { get; internal set; }
        // Owning tool name, reported as `kind`.
        public abstract string Kind { get; }
        // Two running operations that declare the same resource are refused
        // (e.g. two gestures driving the one legacy touch). Null = shareable.
        public virtual string ExclusiveResource => null;
        public PlayModeTimeBase TimeBase = PlayModeTimeBase.Realtime;
        public double ExpectedDurationMs;
        public string Phase = "running";
        // Set before Tick returns true to end the operation as `failed` with
        // this stable code (a wait that timed out, a game exception) while
        // BuildResult still reports the full terminal state. Null = success.
        public string FailureCode;
        public string FailureMessage;

        // Error code of a `stalled` end (no progress for StallWindowMs).
        public virtual string StalledCode => "operation_stalled";
        // What the stalled end kept / released, appended to its message.
        protected internal virtual string StallNote => "";

        // Called on every player-loop frame while running. Return true when done.
        protected internal abstract bool Tick(PlayModeFrame frame);
        // Whether the tick that just ran moved the operation forward;
        // `clockStepMs` is this tick's step of the operation's own clock.
        // Default: the clock advanced (a gameplay clock does not at timeScale 0).
        protected internal virtual bool Progressed(PlayModeFrame frame, double clockStepMs) => clockStepMs > 0;
        // progress.elapsedMs of a status snapshot, in the unit of
        // ExpectedDurationMs (default: the scheduler clock since the first tick).
        protected internal virtual double ProgressMs(double clockElapsedMs) => clockElapsedMs;
        // Extra members inside the snapshot's `progress` object, each with a leading comma.
        protected internal virtual void AppendProgress(StringBuilder sb) { }
        // Called exactly once on every terminal path. Must release held input and
        // restore any temporarily changed Editor/runtime state.
        protected internal abstract void Cleanup(PlayModeEndReason reason);
        // Terminal result as a JSON object. Called after Cleanup.
        protected internal abstract string BuildResult(PlayModeEndReason reason, string error);
    }

    [InitializeOnLoad]
    internal static class PlayModeScheduler
    {
        internal const int MaxRecords = 64;
        internal static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);
        internal const double MaxFrameStepMs = 250;
        // Wall-clock time without progress that ends an operation as stalled.
        internal const double StallWindowMs = 15_000;
        // Largest step one stall check adds (a hitch between two checks is not a stall).
        internal const double MaxStallStepMs = 1_000;
        // No tick for this long while not paused: the player loop is not running.
        internal const double NoFramesAfterMs = 2_000;

        private sealed class Entry
        {
            internal PlayModeOperation Op;
            internal string State = "running";
            internal string Reason;
            internal string Error;
            internal string Result;
            internal DateTime Started = DateTime.UtcNow;
            internal DateTime Finished;
            internal bool FirstTick = true;
            internal int Ticks;
            internal int StartFrame = -1;
            internal int LastFrame = -1;
            internal double ElapsedMs;
            internal double RealtimeMs;
            // Stall accounting on the scheduler clock (NowMs).
            internal double StalledMs;
            internal double LastCheckMs = double.NaN;
            internal double LastTickMs = double.NaN;
            internal float LastTimeScale = 1f;
            internal string StallReason;
            internal bool Terminal => State != "running" && State != "cancel_requested";
        }

        // Marker type for the inserted PlayerLoopSystem.
        private struct PlayModeSchedulerTick { }

        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private static bool installed;

        // Test seams. Production leaves them null/false.
        internal static Func<bool> IsPlayingOverride;
        internal static Func<bool> IsPausedOverride;
        // Scheduler wall clock in ms (stall accounting).
        internal static Func<double> ClockOverride;
        internal static bool SuppressPlayerLoop;

        // A Stopwatch owns no OS handle (fd hygiene).
        private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        static PlayModeScheduler()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;
            // Runs while the Editor is paused (the player loop does not).
            EditorApplication.update += CheckStalls;
        }

        private static bool IsPlaying => IsPlayingOverride?.Invoke() ?? EditorApplication.isPlaying;
        private static bool IsPaused => IsPausedOverride?.Invoke() ?? EditorApplication.isPaused;
        internal static double NowMs => ClockOverride?.Invoke() ?? clock.Elapsed.TotalMilliseconds;

        internal static bool AnyRunning => entries.Values.Any(e => !e.Terminal);

        // The id of the running operation holding `resource`, or null.
        internal static string RunningWithResource(string resource)
        {
            if (string.IsNullOrEmpty(resource)) return null;
            foreach (var entry in entries.Values)
                if (!entry.Terminal && entry.Op.ExclusiveResource == resource) return entry.Op.Id;
            return null;
        }

        // Start an operation. Returns the status snapshot JSON, or null with
        // `error` set to a complete error object JSON.
        internal static string Start(PlayModeOperation op, string requestedId, out string error)
        {
            error = null;
            if (op == null) throw new ArgumentNullException(nameof(op));
            if (!IsPlaying)
            {
                error = ErrorJson("play_mode_required", "Play-mode operations require play mode.");
                return null;
            }
            var id = string.IsNullOrEmpty(requestedId) ? Guid.NewGuid().ToString() : requestedId;
            if (!IsValidId(id))
            {
                error = ErrorJson("invalid_arguments", "operation_id must be 8-64 characters of [A-Za-z0-9-].");
                return null;
            }
            Prune();
            if (entries.TryGetValue(id, out var existing))
            {
                // A retried start of the same operation answers with its record;
                // a different tool reusing the id is a conflict.
                if (existing.Op.Kind != op.Kind)
                {
                    error = ErrorJson("idempotency_conflict", "operation_id already names a different operation.");
                    return null;
                }
                return Snapshot(existing);
            }
            var holder = RunningWithResource(op.ExclusiveResource);
            if (holder != null)
            {
                error = ErrorJson("playmode_operation_busy",
                    "Play-mode operation " + holder + " is still driving " + op.ExclusiveResource +
                    ". Wait for it (or cancel it) before starting another.", "\"operationId\":" + BridgeJson.EscapeString(holder));
                return null;
            }
            if (entries.Count >= MaxRecords)
            {
                error = ErrorJson("playmode_operation_capacity", "Too many retained play-mode operations; retry after older records expire.");
                return null;
            }
            op.Id = id;
            var entry = new Entry { Op = op, LastCheckMs = NowMs };
            entries[id] = entry;
            Install();
            return Snapshot(entry);
        }

        internal static string Status(string id)
        {
            Prune();
            return entries.TryGetValue(id ?? "", out var entry)
                ? Snapshot(entry)
                : ErrorJson("operation_not_found", "Unknown or expired play-mode operation. A domain reload or Editor restart drops records; the operation's input was released by that cleanup. Do not restart blindly.");
        }

        internal static string Cancel(string id)
        {
            if (!entries.TryGetValue(id ?? "", out var entry))
                return Status(id);
            if (!entry.Terminal) Finish(entry, PlayModeEndReason.Cancelled, null);
            return Snapshot(entry);
        }

        // POST /playmode-operations body: {"action":"status"|"cancel","operation_id":"..."}
        internal static string Handle(string body)
        {
            if (!BridgeJson.IsValidJsonObject(body)) return ErrorJson("invalid_arguments", "JSON object required.");
            var keys = JsonBody.GetObjectKeys(body) ?? new List<string>();
            if (keys.Any(k => k != "action" && k != "operation_id") || keys.Distinct().Count() != keys.Count)
                return ErrorJson("invalid_arguments", "Use only action and operation_id.");
            var action = JsonBody.GetString(body, "action");
            var id = JsonBody.GetString(body, "operation_id");
            if (string.IsNullOrEmpty(id)) return ErrorJson("invalid_arguments", "operation_id is required.");
            switch (action)
            {
                case "status": return Status(id);
                case "cancel": return Cancel(id);
                default: return ErrorJson("invalid_arguments", "action must be status or cancel.");
            }
        }

        internal static void EndAll(PlayModeEndReason reason)
        {
            foreach (var entry in entries.Values.ToArray())
                if (!entry.Terminal) Finish(entry, reason, null);
            Uninstall();
        }

        // ------------------------------------------------------------------
        // Ticking
        // ------------------------------------------------------------------

        private static void TickAll()
        {
            if (!IsPlaying) return;
            TickAll(Time.frameCount, Time.deltaTime, Time.unscaledDeltaTime, Time.timeScale, Screen.width, Screen.height, Time.timeAsDouble);
        }

        internal static void TickAll(int frameCount, float deltaTime, float unscaledDeltaTime, float timeScale, int screenWidth, int screenHeight,
            double playTime = double.NaN)
        {
            foreach (var entry in entries.Values.ToArray())
            {
                if (entry.Terminal) continue;
                var op = entry.Op;
                bool first = entry.FirstTick;
                double realStep = first ? 0 : Math.Min(Math.Max(unscaledDeltaTime, 0f) * 1000.0, MaxFrameStepMs);
                double gameStep = first ? 0 : Math.Min(Math.Max(deltaTime, 0f) * 1000.0, MaxFrameStepMs * Math.Max(1f, timeScale));
                double clockStep = op.TimeBase == PlayModeTimeBase.Gameplay ? gameStep : realStep;
                entry.RealtimeMs += realStep;
                entry.ElapsedMs += clockStep;
                if (first) entry.StartFrame = frameCount;
                entry.FirstTick = false;
                entry.LastFrame = frameCount;
                var frame = new PlayModeFrame
                {
                    FrameCount = frameCount,
                    TickIndex = entry.Ticks++,
                    ElapsedMs = entry.ElapsedMs,
                    RealtimeMs = entry.RealtimeMs,
                    TimeScale = timeScale,
                    ScreenWidth = screenWidth,
                    ScreenHeight = screenHeight,
                    PlayTime = playTime,
                };
                bool done;
                try { done = op.Tick(frame); }
                catch (Exception ex)
                {
                    Finish(entry, PlayModeEndReason.Failed, ex.GetType().Name + ": " + ex.Message);
                    continue;
                }
                if (!done)
                {
                    var now = NowMs;
                    entry.LastTickMs = now;
                    entry.LastTimeScale = timeScale;
                    bool progressed;
                    try { progressed = first || op.Progressed(frame, clockStep); }
                    catch (Exception) { progressed = true; }
                    Account(entry, progressed, now);
                    if (entry.StalledMs >= StallWindowMs) Stall(entry, now);
                    continue;
                }
                if (op.FailureCode == null) Finish(entry, PlayModeEndReason.Completed, null);
                else Finish(entry, PlayModeEndReason.Failed, op.FailureMessage ?? op.FailureCode);
            }
            if (!AnyRunning && installed)
            {
                // Never rewrite the player loop while it is executing.
                EditorUpdateOnce.Schedule(() => { if (!AnyRunning) Uninstall(); });
            }
        }

        // ------------------------------------------------------------------
        // Stalls
        // ------------------------------------------------------------------

        // Add the wall-clock time since the entry's last check (clamped) to its
        // time without progress, or reset it when the operation progressed.
        private static void Account(Entry entry, bool progressed, double now)
        {
            double step = double.IsNaN(entry.LastCheckMs) ? 0 : Math.Min(Math.Max(now - entry.LastCheckMs, 0), MaxStallStepMs);
            entry.LastCheckMs = now;
            entry.StalledMs = progressed ? 0 : entry.StalledMs + step;
        }

        // EditorApplication.update: time passes for running operations even when
        // no player-loop frame runs (a paused Editor). Internal for tests.
        internal static void CheckStalls()
        {
            if (entries.Count == 0 || !IsPlaying) return;
            var now = NowMs;
            bool ended = false;
            foreach (var entry in entries.Values)
            {
                if (entry.Terminal) continue;
                Account(entry, false, now);
                if (entry.StalledMs < StallWindowMs) continue;
                Stall(entry, now);
                ended = true;
            }
            if (ended && !AnyRunning && installed) Uninstall();
        }

        // Why an operation stood still (unit-tested).
        internal static string ClassifyStall(bool paused, double sinceLastTickMs, float timeScale)
        {
            if (paused) return "editor_paused";
            if (double.IsNaN(sinceLastTickMs) || sinceLastTickMs > NoFramesAfterMs) return "no_frames";
            return timeScale <= 0f ? "time_scale_zero" : "no_progress";
        }

        internal static string StallMessage(string reason, double stalledMs)
        {
            var ms = Num(Math.Round(stalledMs));
            switch (reason)
            {
                case "editor_paused": return "The Editor is paused: no play-mode frame ran for " + ms + " ms.";
                case "time_scale_zero": return "Time.timeScale is 0: gameplay time did not advance for " + ms + " ms while frames ran.";
                case "no_frames": return "No play-mode frame ran for " + ms + " ms (the player loop is not running).";
                default: return "The operation made no progress for " + ms + " ms while frames ran.";
            }
        }

        private static void Stall(Entry entry, double now)
        {
            var reason = ClassifyStall(IsPaused, now - entry.LastTickMs, entry.LastTimeScale);
            entry.StallReason = reason;
            var op = entry.Op;
            op.FailureCode = op.StalledCode;
            var note = op.StallNote;
            op.FailureMessage = StallMessage(reason, entry.StalledMs) + (string.IsNullOrEmpty(note) ? "" : " " + note);
            Finish(entry, PlayModeEndReason.Stalled, op.FailureMessage);
        }

        // Cut short in a live Editor: cleanup keeps the evidence gathered so far.
        internal static bool IsCutShort(PlayModeEndReason reason) =>
            reason == PlayModeEndReason.Cancelled || reason == PlayModeEndReason.PlayModeExited ||
            reason == PlayModeEndReason.Failed || reason == PlayModeEndReason.Stalled;

        // Ended on its own terms without success: the result carries error_code.
        internal static bool CarriesFailureCode(PlayModeEndReason reason) =>
            reason == PlayModeEndReason.Failed || reason == PlayModeEndReason.Stalled;

        private static void Finish(Entry entry, PlayModeEndReason reason, string error)
        {
            if (entry.Terminal) return;
            entry.Reason = ReasonName(reason);
            entry.Error = error;
            entry.State = reason == PlayModeEndReason.Completed ? "succeeded"
                : reason == PlayModeEndReason.Failed || reason == PlayModeEndReason.Stalled ? "failed"
                : reason == PlayModeEndReason.DomainReload || reason == PlayModeEndReason.EditorQuit ? "orphaned"
                : "cancelled";
            try { entry.Op.Cleanup(reason); }
            catch (Exception ex) { entry.Error = (entry.Error == null ? "" : entry.Error + "; ") + "cleanup: " + ex.Message; }
            try { entry.Result = entry.Op.BuildResult(reason, entry.Error); }
            catch (Exception ex) { entry.Result = "{\"resultError\":" + BridgeJson.EscapeString(ex.Message) + "}"; }
            entry.Finished = DateTime.UtcNow;
        }

        internal static string ReasonName(PlayModeEndReason reason)
        {
            switch (reason)
            {
                case PlayModeEndReason.Completed: return "completed";
                case PlayModeEndReason.Failed: return "failed";
                case PlayModeEndReason.Cancelled: return "cancelled";
                case PlayModeEndReason.PlayModeExited: return "play_mode_exited";
                case PlayModeEndReason.DomainReload: return "domain_reload";
                case PlayModeEndReason.Stalled: return "stalled";
                default: return "editor_quit";
            }
        }

        // ------------------------------------------------------------------
        // Player loop install / uninstall
        // ------------------------------------------------------------------

        private static void Install()
        {
            if (SuppressPlayerLoop) return;
            // Re-read the live loop every time: project code may have replaced it
            // (and dropped the tick) since the last install.
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (Contains(loop)) { installed = true; return; }
            var systems = loop.subSystemList;
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i].type != typeof(UnityEngine.PlayerLoop.EarlyUpdate)) continue;
                var children = new List<PlayerLoopSystem>(systems[i].subSystemList ?? Array.Empty<PlayerLoopSystem>());
                children.Insert(0, new PlayerLoopSystem { type = typeof(PlayModeSchedulerTick), updateDelegate = TickAll });
                systems[i].subSystemList = children.ToArray();
                loop.subSystemList = systems;
                PlayerLoop.SetPlayerLoop(loop);
                installed = true;
                return;
            }
            throw new InvalidOperationException("Player loop has no EarlyUpdate phase.");
        }

        private static void Uninstall()
        {
            if (SuppressPlayerLoop) { installed = false; return; }
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (!Contains(loop)) { installed = false; return; }
            var systems = loop.subSystemList;
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i].subSystemList == null) continue;
                var children = new List<PlayerLoopSystem>(systems[i].subSystemList);
                if (children.RemoveAll(s => s.type == typeof(PlayModeSchedulerTick)) > 0)
                    systems[i].subSystemList = children.ToArray();
            }
            loop.subSystemList = systems;
            PlayerLoop.SetPlayerLoop(loop);
            installed = false;
        }

        private static bool Contains(PlayerLoopSystem loop)
        {
            if (loop.subSystemList == null) return false;
            foreach (var system in loop.subSystemList)
            {
                if (system.subSystemList == null) continue;
                foreach (var child in system.subSystemList)
                    if (child.type == typeof(PlayModeSchedulerTick)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Lifecycle hooks
        // ------------------------------------------------------------------

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode) EndAll(PlayModeEndReason.PlayModeExited);
        }

        private static void OnBeforeAssemblyReload() => EndAll(PlayModeEndReason.DomainReload);

        private static void OnQuitting() => EndAll(PlayModeEndReason.EditorQuit);

        // ------------------------------------------------------------------
        // Records + JSON
        // ------------------------------------------------------------------

        private static void Prune()
        {
            var now = DateTime.UtcNow;
            foreach (var key in entries.Where(p => p.Value.Terminal && now - p.Value.Finished > Retention).Select(p => p.Key).ToArray())
                entries.Remove(key);
            while (entries.Count >= MaxRecords)
            {
                var oldest = entries.Values.Where(e => e.Terminal).OrderBy(e => e.Finished).FirstOrDefault();
                if (oldest == null) break;
                entries.Remove(oldest.Op.Id);
            }
        }

        private static bool IsValidId(string id)
        {
            if (id.Length < 8 || id.Length > 64) return false;
            foreach (var c in id)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '-') return false;
            return true;
        }

        private static string Snapshot(Entry entry)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"operation_id\":").Append(BridgeJson.EscapeString(entry.Op.Id));
            sb.Append(",\"kind\":").Append(BridgeJson.EscapeString(entry.Op.Kind));
            sb.Append(",\"state\":").Append(BridgeJson.EscapeString(entry.State));
            sb.Append(",\"phase\":").Append(BridgeJson.EscapeString(entry.Terminal ? entry.State : entry.Op.Phase));
            sb.Append(",\"reason\":").Append(entry.Reason == null ? "null" : BridgeJson.EscapeString(entry.Reason));
            if (entry.StallReason != null)
                sb.Append(",\"stall\":{\"reason\":").Append(BridgeJson.EscapeString(entry.StallReason))
                  .Append(",\"stalledMs\":").Append(Num(entry.StalledMs)).Append('}');
            // The scheduler clock belongs to progress.elapsedMs, so it is named
            // there (progress.clock). A top-level `time` here clashed with the
            // tool result's own `time` — playtest_wait reported "realtime" (its
            // wall-clock timeout) and "gameplay" (its ms conditions) side by side.
            sb.Append(",\"progress\":{\"clock\":").Append(BridgeJson.EscapeString(entry.Op.TimeBase == PlayModeTimeBase.Gameplay ? "gameplay" : "realtime"));
            sb.Append(",\"elapsedMs\":").Append(Num(SafeProgressMs(entry)));
            sb.Append(",\"expectedMs\":").Append(Num(entry.Op.ExpectedDurationMs));
            sb.Append(",\"realtimeMs\":").Append(Num(entry.RealtimeMs));
            sb.Append(",\"stalledMs\":").Append(Num(entry.StalledMs));
            sb.Append(",\"ticks\":").Append(entry.Ticks);
            sb.Append(",\"startFrame\":").Append(entry.StartFrame);
            sb.Append(",\"lastFrame\":").Append(entry.LastFrame);
            // Operation detail while it runs (a terminal result carries its own).
            if (!entry.Terminal)
            {
                var detail = new StringBuilder();
                try { entry.Op.AppendProgress(detail); sb.Append(detail); }
                catch (Exception) { /* progress detail is best effort; never a broken snapshot */ }
            }
            sb.Append('}');
            sb.Append(",\"result\":").Append(entry.Result ?? "null");
            sb.Append('}');
            return sb.ToString();
        }

        private static double SafeProgressMs(Entry entry)
        {
            try { return entry.Op.ProgressMs(entry.ElapsedMs); }
            catch (Exception) { return entry.ElapsedMs; }
        }

        internal static string Num(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

        internal static string ErrorJson(string code, string message, string extraFields = null)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"error\":{\"code\":").Append(BridgeJson.EscapeString(code));
            sb.Append(",\"message\":").Append(BridgeJson.EscapeString(message));
            if (!string.IsNullOrEmpty(extraFields)) sb.Append(',').Append(extraFields);
            sb.Append("}}");
            return sb.ToString();
        }

        // Test-only: drop every record without running Cleanup.
        internal static void ResetForTests()
        {
            entries.Clear();
            installed = false;
        }
    }
}
