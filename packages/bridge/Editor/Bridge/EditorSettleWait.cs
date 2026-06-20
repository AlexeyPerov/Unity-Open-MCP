using System.Threading;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    // M13 T4.1 — Compile-settle wait helper.
    //
    // After an EditorSettle / RestartThenSettle mutation the dispatcher blocks
    // until the editor finishes compiling before building the response, so the
    // caller never observes a half-compiled state. The wait happens on the
    // worker (listener) thread — the main thread is the one doing the compiling,
    // so sleeping here does not stall the compile.
    //
    // Cap differs by policy:
    //   - EditorSettle:        short cap (asset refresh / import settle).
    //   - RestartThenSettle:   long cap (a real domain reload may occur).
    //
    // The HTTP listener thread and the TaskCompletionSource bridging the
    // dispatched work both survive a domain reload, so re-/ping after reload is
    // automatic — the caller's next /ping observes the post-reload state with
    // no extra round-trip.
    //
    // Thread-safety: we poll BridgeSession.IsCompiling (a volatile flag cached
    // on the main-thread EditorApplication.update tick) rather than
    // EditorApplication.isCompiling directly — the latter is a main-thread API
    // and the worker thread must not touch it. The main thread keeps ticking
    // update (and thus refreshing the flag) while this loop sleeps.
    public static class EditorSettleWait
    {
        private const int TickMs = 100;
        private const int EditorSettleCapMs = 5000;
        private const int RestartSettleCapMs = 60000;

        // Blocks the calling (worker) thread until isCompiling flips false or
        // the policy cap elapses. Returns the elapsed wait in milliseconds.
        public static long Wait(LifecyclePolicy policy)
        {
            if (!ToolLifecycle.RequiresSettleWait(policy)) return 0;

            int capMs = policy == LifecyclePolicy.RestartThenSettle
                ? RestartSettleCapMs
                : EditorSettleCapMs;

            long elapsed = 0;
            while (elapsed < capMs)
            {
                if (!BridgeSession.IsCompiling) break;

                Thread.Sleep(TickMs);
                elapsed += TickMs;
            }

            if (elapsed >= capMs)
            {
                Debug.LogWarning(
                    $"[EditorSettleWait] Settle wait hit the {capMs}ms cap for policy {policy}; " +
                    "the editor may still be compiling. The response is returned anyway.");
            }

            return elapsed;
        }
    }
}
