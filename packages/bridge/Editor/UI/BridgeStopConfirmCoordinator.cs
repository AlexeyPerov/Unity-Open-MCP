using System;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    // M29 Plan 2 — shared two-click Stop-confirm coordinator.
    //
    // The Status tab has always required a two-click / timed confirm before
    // stopping the listener (a Stop drops MCP connectivity for any active
    // agent). The toolbar MCP toggle, however, called BridgeHttpServer.Stop()
    // directly — same destructive action, no confirm. This coordinator makes
    // the confirm policy shared so both entry points honor it.
    //
    // Policy:
    //   - Start is always one-click (idempotent, non-destructive).
    //   - Stop is gated by a transient confirm. The first Stop click arms
    //     the confirm (5s window); the second click within the window
    //     performs the stop. If the window is open, the Status tab renders
    //     the armed state as its "Confirm Stop" button. If the window is
    //     NOT open, the toolbar click opens the window and arms the confirm,
    //     so the operator always sees the second-click affordance.
    //
    // The coordinator is a single static instance because the toolbar and
    // the window are separate surfaces that must observe the same transient
    // (a confirm armed from one surface must be visible from the other).

    internal static class BridgeStopConfirmCoordinator
    {
        /// <summary>Seconds the second click has to arrive after the first.</summary>
        public const double ConfirmWindowSeconds = 5.0;

        private static bool _armed;
        private static double _deadline;

        // Time source seam. Defaults to EditorApplication.timeSinceStartup so
        // production behavior is unchanged; tests override it via SetTimeSource
        // to fast-forward the deadline and exercise the auto-expire branch in
        // Tick() without a real sleep.
        private static Func<double> _timeSource = () => EditorApplication.timeSinceStartup;

        /// <summary>
        /// Read-only view of the transient used by both surfaces.
        /// <see cref="BridgeToolbarToggle"/> and the Status tab read this to
        /// render their "Confirm Stop" affordance + countdown.
        /// </summary>
        public static bool IsArmed => _armed;

        /// <summary>The editor time (seconds since startup) at which the armed confirm expires.</summary>
        public static double Deadline => _deadline;

        /// <summary>Remaining seconds for a countdown label, clamped at 0.</summary>
        public static double RemainingSeconds
        {
            get
            {
                if (!_armed) return 0.0;
                return Math.Max(0.0, _deadline - _timeSource());
            }
        }

        // Test-only seam: replace the time source so Tick()/expiry can be
        // exercised deterministically. Restored to the editor clock by passing
        // null. Production code never calls this.
        internal static void SetTimeSource(Func<double> timeSource)
        {
            _timeSource = timeSource ?? (() => EditorApplication.timeSinceStartup);
        }

        /// <summary>
        /// Arm the confirm transient. Idempotent — re-arming refreshes the
        /// deadline so repeated first-clicks don't shorten the window.
        /// </summary>
        public static void Arm()
        {
            _armed = true;
            _deadline = _timeSource() + ConfirmWindowSeconds;
        }

        /// <summary>
        /// Clear the transient without stopping. Called when the confirm
        /// expires or the operator backs out.
        /// </summary>
        public static void Disarm()
        {
            _armed = false;
        }

        /// <summary>
        /// Pump the transient: if the deadline passed, clear it. Both
        /// surfaces' EditorApplication.update ticks call this so the
        /// auto-expire works regardless of which surface is open.
        /// </summary>
        public static void Tick()
        {
            if (_armed && _timeSource() >= _deadline)
                _armed = false;
        }

        /// <summary>
        /// The single Stop entry point for both surfaces. Returns true when
        /// the stop actually ran (second click), false when the call only
        /// armed the transient (first click). The caller repaints either way.
        /// </summary>
        public static bool RequestStop(Action performStop)
        {
            if (_armed)
            {
                try
                {
                    performStop?.Invoke();
                }
                finally
                {
                    _armed = false;
                }
                return true;
            }

            Arm();
            return false;
        }
    }
}
