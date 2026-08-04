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
            for (int i = 0; i < 3; i++)
            {
                MainThreadDispatcher.EnqueueAsync(() => i, 60_000);
            }

            MainThreadDispatcher.Shutdown();

            Assert.That(MainThreadDispatcher.OutstandingTimerCount, Is.EqualTo(0),
                "Shutdown must dispose every outstanding Timer so its native " +
                "wait handle releases before the AppDomain unloads");
        }

        [Test]
        public static void Shutdown_IsIdempotentAndSafeWhenNoTimersAreOutstanding()
        {
            // A reload with no pending dispatch must be a no-op (no throw).
            MainThreadDispatcher.Shutdown();
            MainThreadDispatcher.Shutdown();
            Assert.That(MainThreadDispatcher.OutstandingTimerCount, Is.EqualTo(0));
        }
    }
}
