using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // feedback-fable-04-08 §2a — MainThreadDispatcher per-call Timer lifecycle.
    //
    // EnqueueAsync creates a System.Threading.Timer per dispatch whose disposal
    // is tied to the Task completing (ContinueWith → timer.Dispose). When a
    // domain reload happens while a dispatch is still queued (main thread
    // blocked by a modal, or the gate never settled), the Task never completes
    // and the Timer — which holds a native wait handle registered with Mono's
    // IOSelector — leaks across the AppDomain unload. Accumulated over many
    // reloads the abandoned registrations exhaust the fd budget and the Bee
    // build driver raises `Could not register to wait for file descriptor N`.
    //
    // Shutdown() (hooked to beforeAssemblyReload) disposes every outstanding
    // timer en masse so the wait handles release BEFORE the AppDomain is torn
    // down. These tests pin that contract without depending on a real reload:
    //   - after Shutdown, the outstanding-timer count is always 0 (whether the
    //     dispatch completed on its own or is still pending), and
    //   - Shutdown is idempotent / safe when nothing is outstanding.
    public static class MainThreadDispatcherTests
    {
        [SetUp]
        public static void SetUp()
        {
            // Start each test from a clean dispatcher state.
            MainThreadDispatcher.Shutdown();
        }

        [TearDown]
        public static void TearDown()
        {
            MainThreadDispatcher.Shutdown();
        }

        [Test]
        public static void Shutdown_DrainsOutstandingTimersFromPendingDispatches()
        {
            // Enqueue several dispatches with long timeouts. The action returns
            // immediately (it must NOT block the main thread if
            // EditorApplication.update ticks and drains the queue mid-test).
            // The per-call Timer is created in EnqueueAsync; whether the queue
            // drained (timer retired by its ContinueWith) or is still pending
            // (timer outstanding), Shutdown must leave the count at 0.
            var tasks = new System.Collections.Generic.List<Task<int>>();
            for (int i = 0; i < 3; i++)
            {
                tasks.Add(MainThreadDispatcher.EnqueueAsync(() => i, 60_000));
            }

            MainThreadDispatcher.Shutdown();

            Assert.That(MainThreadDispatcher.OutstandingTimerCount, Is.EqualTo(0),
                "Shutdown must dispose every outstanding Timer so its native " +
                "wait handle releases before the AppDomain unloads");

            // Shutdown now FAULTS still-pending dispatches (so unbounded
            // .Result callers can't hang) — observe those faults so they never
            // surface as UnobservedTaskExceptions.
            foreach (var t in tasks)
            {
                try { t.Wait(1_000); } catch (System.AggregateException) { }
            }
        }

        [Test]
        public static void Shutdown_IsIdempotentAndSafeWhenNoTimersAreOutstanding()
        {
            // A reload with no pending dispatch must be a no-op (no throw).
            MainThreadDispatcher.Shutdown();
            MainThreadDispatcher.Shutdown();
            Assert.That(MainThreadDispatcher.OutstandingTimerCount, Is.EqualTo(0));
        }

        // Shutdown must COMPLETE (fault) the awaiter of a still-queued
        // dispatch, not just dispose its Timer: Timer.Dispose CANCELS the
        // pending timeout callback, so before the fix the TCS never resolved
        // and any unbounded .Result caller hung across the reload.
        [Test]
        public static void Shutdown_FailsPendingAwaitersInsteadOfStrandingThem()
        {
            // The queue is drained by EditorApplication.update, which cannot
            // tick while this test body holds the main thread — the dispatch
            // is deterministically still queued when Shutdown runs.
            var task = MainThreadDispatcher.EnqueueAsync(() => 1, 60_000);

            MainThreadDispatcher.Shutdown();

            try
            {
                bool completed = task.Wait(5_000);
                Assert.Fail(completed
                    ? "Task must fault (the action never ran), not complete successfully."
                    : "Task never completed — Shutdown stranded the awaiter.");
            }
            catch (System.AggregateException ae)
            {
                Assert.IsInstanceOf<System.OperationCanceledException>(ae.InnerException,
                    "Shutdown must fail the pending dispatch with OperationCanceledException.");
            }
        }

        // A dispatch that completes on its own must retire its pending entry —
        // the old ConcurrentBag only ever grew, leaking one dead Timer +
        // closure per dispatch until the next domain reload.
        [Test]
        public static void CompletedDispatch_RetiresItsOutstandingEntry()
        {
            // Short timeout: the Timer faults the task (the queue is not
            // drained while this test holds the main thread), and the task's
            // ContinueWith must then remove the entry.
            var task = MainThreadDispatcher.EnqueueAsync(() => 42, 100);
            Assert.That(MainThreadDispatcher.OutstandingTimerCount, Is.EqualTo(1));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (MainThreadDispatcher.OutstandingTimerCount > 0 && sw.ElapsedMilliseconds < 5_000)
            {
                Thread.Sleep(20);
            }

            Assert.That(MainThreadDispatcher.OutstandingTimerCount, Is.EqualTo(0),
                "a completed dispatch must retire its Timer entry instead of " +
                "accumulating until the next domain reload");
            // Observe the expected fault so it never surfaces as an
            // UnobservedTaskException.
            try { task.Wait(1_000); }
            catch (System.AggregateException) { }
        }
    }
}
