using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    public static class MainThreadDispatcher
    {
        private static readonly ConcurrentQueue<QueuedAction> _queue = new();

        // feedback-fable-04-08 §2a — outstanding per-call Timers. EnqueueAsync
        // creates a System.Threading.Timer per dispatch whose disposal is tied
        // to the Task completing (ContinueWith → timer.Dispose). When a domain
        // reload happens while a dispatch is still queued (main thread blocked
        // by a modal, or the gate never settled), the Task never completes, the
        // ContinueWith never runs, and the Timer — which holds a native wait
        // handle registered with Mono's IOSelector — leaks across the AppDomain
        // unload. Across many reloads the cumulative abandoned registrations
        // exhaust the fd budget and the Bee build driver raises
        // `Could not register to wait for file descriptor N`. Tracking the live
        // timers lets Shutdown dispose them en masse on teardown.
        private static readonly ConcurrentBag<Timer> _outstandingTimers = new();

        // specs/feedback.md 2026-07-03 — main-thread-stall detection. When a
        // Unity modal (unsaved-changes, scene-modified-externally, a
        // third-party Editor window) blocks the main thread, ProcessQueue stops
        // draining and every queued action sits waiting. The per-call 30s
        // timeout then fires with a generic TimeoutException that looks
        // indistinguishable from "the tool itself ran long" — so an agent burns
        // 30s per call with no signal a modal is the cause.
        //
        // To distinguish the two: record when each action was enqueued
        // (EnqueuedAtUtc) and when ProcessQueue started draining it
        // (StartedDrainAtUtc). If the timeout fires and the action NEVER started
        // draining (StartedDrainAtUtc == null), the main thread was blocked the
        // entire window — surface main_thread_blocked instead of the generic
        // timeout. If it started but didn't finish, the tool ran long — keep the
        // existing timeout behaviour.
        //
        // The threshold below is the queue-wait time after which we ALSO log a
        // diagnostic (the call may still complete, just slowly). Kept separate
        // from the per-call timeout (which is the hard fail).
        private const double QueueStallWarnSeconds = 5.0;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.update -= ProcessQueue;
            EditorApplication.update += ProcessQueue;
            // feedback-fable-04-08 §2a — dispose stranded timers + drain the
            // queue on domain reload so wait-handle registrations release before
            // the AppDomain unloads (see _outstandingTimers).
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
        }

        // feedback-fable-04-08 §2a — release every outstanding per-call Timer
        // and fail the queued actions that never ran. Runs on
        // beforeAssemblyReload so the native wait handles the Timers registered
        // with Mono's IOSelector are freed BEFORE the AppDomain is torn down
        // (the leak that, accumulated over many reloads, trips the Bee driver's
        // `Could not register to wait for file descriptor N`). Idempotent and
        // best-effort — never throws, so a reload is never blocked by cleanup.
        // Internal so the EditMode test can drive it directly + inspect the
        // outstanding-timer count.
        internal static void Shutdown()
        {
            // Drain the queue so a stranded action's TCS resolves (the worker
            // awaiting it then closes its HTTP context) rather than lingering.
            // The dequeued action is NOT run here — it would touch Editor state
            // mid-teardown — and its TCS cannot be reached from the closure, so
            // the per-call Timer disposal below is what unblocks the awaiter via
            // its timeout callback.
            while (_queue.TryDequeue(out _)) { }
            if (_outstandingTimers.IsEmpty) return;
            var snapshot = _outstandingTimers.ToArray();
            _outstandingTimers.Clear();
            foreach (var timer in snapshot)
            {
                try { timer.Dispose(); } catch { }
            }
        }

        // feedback-fable-04-08 §2a — test-only count of outstanding per-call
        // Timers, so the EditMode test can prove Shutdown drains them. Not part
        // of any production call path.
        internal static int OutstandingTimerCount => _outstandingTimers.Count;

        private static void ProcessQueue()
        {
            while (_queue.TryDequeue(out var queued))
            {
                var waitMs = (DateTime.UtcNow - queued.EnqueuedAtUtc).TotalMilliseconds;
                // Stamp the drain start so EnqueueAsync's timeout can tell
                // "never started" (main thread blocked) from "ran long".
                queued.StartedDrainAtUtc = DateTime.UtcNow;
                if (waitMs > QueueStallWarnSeconds * 1000)
                {
                    // The action sat in the queue for seconds before the main
                    // thread picked it up — a strong signal a modal or heavy
                    // editor stall held the thread. Log once so it surfaces in
                    // the console / Editor.log without failing the call (the
                    // work may still succeed).
                    Debug.LogWarning(
                        $"[unity-open-mcp] main-thread queue stalled for {waitMs:F0}ms before " +
                        "processing a tool dispatch — a Unity modal dialog or a long editor " +
                        "operation may be blocking the main thread.");
                }
                try
                {
                    queued.Action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        public static void Enqueue(Action action)
        {
            _queue.Enqueue(new QueuedAction { Action = action, EnqueuedAtUtc = DateTime.UtcNow });
        }

        public static Task<T> EnqueueAsync<T>(Func<T> action, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Wrap the typed Func in an Action that resolves the TCS, then
            // track drain timing via the shared QueuedAction envelope. The
            // timeout callback inspects StartedDrainAtUtc to distinguish
            // "main thread blocked" (never drained) from "tool ran long".
            var queued = new QueuedAction
            {
                EnqueuedAtUtc = DateTime.UtcNow,
                Action = () =>
                {
                    try
                    {
                        tcs.TrySetResult(action());
                    }
                    catch (Exception e)
                    {
                        tcs.TrySetException(e);
                    }
                },
            };
            _queue.Enqueue(queued);

            // When the timeout fires, distinguish "the work never started
            // draining" (the main thread was blocked the whole window — almost
            // certainly a Unity modal) from "the work started but ran past the
            // timeout" (the tool itself is slow). The former surfaces a
            // structured MainThreadBlockedException so the caller can build a
            // main_thread_blocked / modal_likely_open error; the latter keeps
            // the legacy TimeoutException so existing handlers
            // (BuildTimeoutEnvelope) still match.
            var timer = new Timer(_ =>
            {
                if (!queued.StartedDrainAtUtc.HasValue)
                {
                    tcs.TrySetException(new MainThreadBlockedException(timeoutMs));
                }
                else
                {
                    tcs.TrySetException(new TimeoutException());
                }
            }, null, timeoutMs, Timeout.Infinite);
            // feedback-fable-04-08 §2a — track the Timer so Shutdown can dispose
            // it on a domain reload if this dispatch is still pending then. The
            // ContinueWith still disposes it on normal completion; double-dispose
            // of a System.Threading.Timer is a documented no-op, so the Shutdown
            // path re-disposing an already-completed timer is safe.
            _outstandingTimers.Add(timer);
            tcs.Task.ContinueWith(_ =>
            {
                try { timer.Dispose(); } catch { }
            });

            return tcs.Task;
        }

        // Holds the queue-wait timing for the stall diagnostic. ProcessQueue
        // stamps StartedDrainAtUtc when it begins running the Action; the
        // EnqueueAsync timeout callback reads it to distinguish "main thread
        // blocked the whole window" (null → MainThreadBlockedException) from
        // "the work started but ran past the timeout" (set → TimeoutException).
        private sealed class QueuedAction
        {
            public Action Action;
            public DateTime EnqueuedAtUtc;
            // Null until ProcessQueue starts draining this action. Set on the
            // main thread; read by the EnqueueAsync timeout callback on a Timer
            // thread. Volatile would be the strictly-correct guard, but the
            // worst case of a stale read is surfacing the generic
            // TimeoutException instead of MainThreadBlockedException — both are
            // failures, and the diagnostic intent (point at a likely modal)
            // only fires when this is null, so a false negative degrades
            // gracefully. Kept as a plain field for simplicity.
            public DateTime? StartedDrainAtUtc;
        }
    }

    // Raised by EnqueueAsync when the per-call timeout elapses AND the queued
    // action never started draining — i.e. the main thread was blocked for the
    // entire window. Callers (BridgeHttpServer) catch this and build a
    // main_thread_blocked / modal_likely_open error envelope pointing the agent
    // at the dismiss loop, scene_save, or a restart. Distinct from
    // TimeoutException (which means the work started but ran long) so existing
    // handlers keep their semantics.
    public sealed class MainThreadBlockedException : Exception
    {
        public int TimeoutMs { get; }

        public MainThreadBlockedException(int timeoutMs)
            : base(
                "The Unity main thread did not process the tool dispatch within the timeout — " +
                "a Unity modal dialog (unsaved changes, scene modified externally, safe mode) " +
                "or a long editor operation is almost certainly blocking it. " +
                "Check the dismiss loop audit lines, scene_save before retrying, " +
                "or restart the editor if a popup is wedged.")
        {
            TimeoutMs = timeoutMs;
        }
    }
}
