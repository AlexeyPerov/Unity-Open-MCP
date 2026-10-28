// Drives a condition tree across scheduler ticks: captures console messages
// every frame (for log conditions, `errors logged meanwhile` and `fail_on`),
// keeps a tick history as long as the longest poll interval (to time-stamp a
// log by the frame it was logged in), and evaluates the tree every `PollEvery`
// frames. Used by playtest_wait and by capture_series' `start_on`.
//
// Owns no OS handle: the only external hook is Application.logMessageReceived,
// subscribed on the first tick and removed by Detach (called from the owning
// operation's Cleanup on every terminal path).
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace UnityOpenMcpBridge.Playtest
{
    internal sealed class ConditionWatcher
    {
        // Covers every tick since the previous poll, so a log pending at a poll
        // always finds the tick it was logged in.
        internal const int HistorySize = PlaytestWaitTools.MaxPollEveryFrames + 1;
        internal const int MaxErrors = 20;
        internal const int MaxStackChars = 4000;
        internal const int MaxPendingLogs = 2000;

        public readonly WaitCondition Root;
        public readonly int PollEvery;
        public bool FailOnException, FailOnError;

        private readonly List<LoggedMessage> pending = new List<LoggedMessage>();
        private readonly ConditionTick[] history = new ConditionTick[HistorySize];
        private int historyCount, historyNext;
        private ConditionTick? previous;
        private bool subscribed;
        private bool logsWanted;

        public int Polls;
        public int LastPollFrame = -1;
        public readonly List<string> Errors = new List<string>();
        public int ErrorsDropped;
        public LoggedMessage FailLog;       // the exception / error that tripped fail_on
        public int PendingDropped;

        internal ConditionWatcher(WaitCondition root, int pollEvery)
        {
            Root = root;
            PollEvery = pollEvery < 1 ? 1 : pollEvery;
            logsWanted = root != null && root.WantsLogs;
        }

        internal void Attach()
        {
            if (subscribed) return;
            Application.logMessageReceived += OnLog;
            subscribed = true;
        }

        internal void Detach()
        {
            if (!subscribed) return;
            Application.logMessageReceived -= OnLog;
            subscribed = false;
        }

        // The condition is settled (a start_on that fired): stop buffering
        // messages for it. Errors and fail_on keep recording until Detach.
        internal void StopMatching()
        {
            logsWanted = false;
            pending.Clear();
        }

        private void OnLog(string condition, string stackTrace, LogType type)
            => Record(new LoggedMessage { Message = condition, Stack = stackTrace, Type = type, Frame = Time.frameCount, PlayTime = Time.timeAsDouble });

        // Test seam and the log hook's body.
        internal void Record(LoggedMessage log)
        {
            if (log == null) return;
            bool isError = log.Type == LogType.Error || log.Type == LogType.Exception || log.Type == LogType.Assert;
            if (isError)
            {
                if (Errors.Count < MaxErrors)
                {
                    var text = log.Type + ": " + log.Message;
                    if (log.Type == LogType.Exception && !string.IsNullOrEmpty(log.Stack))
                        text += " @ " + log.Stack.Split('\n')[0];
                    Errors.Add(PlaytestJson.Clip(text, 500));
                }
                else ErrorsDropped++;
                if (FailLog == null && ((FailOnException && log.Type == LogType.Exception) || (FailOnError && isError)))
                    FailLog = log;
            }
            if (!logsWanted) return;
            if (pending.Count >= MaxPendingLogs) { PendingDropped++; return; }
            pending.Add(log);
        }

        // Observe one tick. Returns true when the root condition holds after
        // this tick's poll (polls happen on tick 0 and every PollEvery ticks).
        internal bool Observe(ConditionTick now)
        {
            Remember(now);
            bool poll = Root != null && (now.TickIndex % PollEvery == 0);
            if (poll)
            {
                var input = new ConditionPoll { Now = now, Previous = previous, TickAt = TickAt };
                input.NewLogs.AddRange(pending);
                pending.Clear();
                Root.Evaluate(input);
                Polls++;
                LastPollFrame = now.Frame;
            }
            previous = now;
            return Root != null && Root.Met;
        }

        private void Remember(ConditionTick tick)
        {
            history[historyNext] = tick;
            historyNext = (historyNext + 1) % HistorySize;
            if (historyCount < HistorySize) historyCount++;
        }

        internal ConditionTick? TickAt(int frame)
        {
            for (int i = 0; i < historyCount; i++)
            {
                var t = history[(historyNext - 1 - i + HistorySize) % HistorySize];
                if (t.Frame == frame) return t;
                if (t.Frame < frame) break;
            }
            return null;
        }

        // The leaf (or composite) that fired: an `any` names its child.
        internal WaitCondition FiredNode()
        {
            if (Root == null || !Root.Met) return null;
            if (Root is CompositeCondition c && c.FiredChild != null) return c.FiredChild;
            return Root;
        }

        internal void AppendFired(StringBuilder sb)
        {
            var node = FiredNode();
            if (node == null) { sb.Append("null"); return; }
            sb.Append("{\"path\":").Append(BridgeJson.EscapeString(node.Path));
            sb.Append(",\"type\":").Append(BridgeJson.EscapeString(node.Type));
            var fire = node.Fire.Set ? node.Fire : Root.Fire;
            sb.Append(",\"frame\":").Append(fire.Frame);
            sb.Append(",\"observed_frame\":").Append(fire.ObservedFrame);
            sb.Append(",\"play_time\":").Append(PlaytestJson.Num(fire.PlayTime, 4));
            sb.Append(",\"elapsed_ms\":").Append(PlaytestJson.Num(fire.ElapsedMs));
            sb.Append(",\"realtime_ms\":").Append(PlaytestJson.Num(fire.RealtimeMs));
            if (fire.AtStart) sb.Append(",\"at_start\":true");
            sb.Append('}');
        }

        internal void AppendErrors(StringBuilder sb)
        {
            sb.Append(",\"errors\":[");
            for (int i = 0; i < Errors.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(Errors[i]));
            }
            sb.Append("],\"errors_dropped\":").Append(ErrorsDropped);
        }

        internal static void AppendLog(StringBuilder sb, LoggedMessage log)
        {
            sb.Append("{\"type\":").Append(BridgeJson.EscapeString(log.Type.ToString()));
            sb.Append(",\"message\":").Append(BridgeJson.EscapeString(PlaytestJson.Clip(log.Message, 1000)));
            sb.Append(",\"stack\":").Append(BridgeJson.EscapeString(PlaytestJson.Clip(log.Stack ?? "", MaxStackChars)));
            sb.Append(",\"frame\":").Append(log.Frame);
            sb.Append(",\"play_time\":").Append(PlaytestJson.Num(log.PlayTime, 4));
            sb.Append('}');
        }
    }
}
