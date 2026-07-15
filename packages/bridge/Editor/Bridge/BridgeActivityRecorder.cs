using System;
using System.Net;

namespace UnityOpenMcpBridge
{
    // Per-request activity bookkeeping. A BridgeActivityEvent is constructed at
    // the start of HandleRequest, annotated through the dispatch path, and
    // recorded to the activity ring buffer (BridgeActivityLog) when the request
    // finishes. The current record is held thread-static so each ThreadPool
    // worker handling a request sees its own (requests never hop threads).

    internal static class BridgeActivityRecorder
    {
        // Per-request activity record. Set on the listener worker thread at the start
        // of HandleRequest and read by nested handlers (e.g. HandleToolDispatch) before
        // FinishActivity records it to the ring buffer. Thread-static because each
        // request runs on a ThreadPool worker.
        [ThreadStatic] private static BridgeActivityEvent _currentActivity;

        internal static BridgeActivityEvent CurrentActivity
        {
            get => _currentActivity;
            set => _currentActivity = value;
        }

        internal static BridgeActivityEvent BeginActivity(HttpListenerContext context)
        {
            var evt = new BridgeActivityEvent
            {
                Timestamp = DateTime.Now,
                Kind = BridgeActivityKind.UnknownPath,
                ToolName = null,
                GateMode = null,
                Outcome = BridgeActivityOutcome.Unknown,
                DurationMs = 0,
                HttpStatus = 0,
                RequestBodyLength = SafeContentLength(context?.Request),
                ErrorCode = null,
                ErrorMessage = null
            };
            return evt;
        }

        internal static int SafeContentLength(HttpListenerRequest request)
        {
            if (request == null) return 0;
            try
            {
                var cl = request.ContentLength64;
                if (cl > 0 && cl < int.MaxValue) return (int)cl;
                return 0;
            }
            catch { return 0; }
        }

        internal static void FinishActivity(HttpListenerContext context, BridgeActivityEvent activity)
        {
            if (activity == null) return;
            try
            {
                activity.HttpStatus = context?.Response?.StatusCode ?? 0;
                if (activity.Outcome == BridgeActivityOutcome.Unknown)
                {
                    if (activity.HttpStatus >= 500) activity.Outcome = BridgeActivityOutcome.Failed;
                    else if (activity.HttpStatus >= 400) activity.Outcome = BridgeActivityOutcome.Failed;
                    else if (activity.HttpStatus > 0) activity.Outcome = BridgeActivityOutcome.Success;
                }
            }
            catch { }
            try { BridgeActivityLog.Record(activity); } catch { }
        }

        internal static void ApplyToolResultToActivity(BridgeActivityEvent activity, GateDispatchResult result, long durationMs)
        {
            if (activity == null) return;
            activity.DurationMs = durationMs;
            activity.Outcome = result.Outcome switch
            {
                GateOutcome.Passed => BridgeActivityOutcome.Success,
                GateOutcome.Warned => BridgeActivityOutcome.Success,
                GateOutcome.Skipped => BridgeActivityOutcome.Skipped,
                GateOutcome.Failed => result.Mutation != null && !result.Mutation.Success
                    ? BridgeActivityOutcome.Failed
                    : BridgeActivityOutcome.Failed,
                // T5.3 — the mutation committed but the validate scan could not
                // run. Surface as Failed so the operator notices something needs
                // a manual check (the gate did not pass cleanly).
                GateOutcome.ValidateScanFailed => BridgeActivityOutcome.Failed,
                _ => BridgeActivityOutcome.Unknown
            };
            if (result.Mutation != null && !result.Mutation.Success && !string.IsNullOrEmpty(result.Mutation.ErrorCode))
            {
                activity.ErrorCode = result.Mutation.ErrorCode;
                activity.ErrorMessage = TruncateMessage(result.Mutation.ErrorMessage);
            }
        }

        internal static void ApplyToolFailureToActivity(BridgeActivityEvent activity, string code, string message, long durationMs)
        {
            if (activity == null) return;
            activity.DurationMs = durationMs;
            activity.Outcome = code == "timeout" ? BridgeActivityOutcome.Timeout : BridgeActivityOutcome.Failed;
            activity.ErrorCode = code;
            activity.ErrorMessage = TruncateMessage(message);
        }

        internal static string TruncateMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            const int max = 200;
            return message.Length <= max ? message : message.Substring(0, max) + "…";
        }
    }
}
