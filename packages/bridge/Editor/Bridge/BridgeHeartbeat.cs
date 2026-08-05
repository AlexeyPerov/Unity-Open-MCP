using UnityEditor;

namespace UnityOpenMcpBridge
{
    // M13 T4.7 — Heartbeat / instance discovery (unity-cli pattern).
    //
    // unity-cli writes a per-project heartbeat JSON every 0.5s with forced
    // state transitions (compiling / reloading / entering_playmode), so
    // clients can poll readiness precisely without an HTTP round-trip. Here
    // we complement /ping with the same idea: the instance lock file at
    // ~/.unity-open-mcp/instances/<hash>.json doubles as the heartbeat file
    // (BridgeInstanceLock), rewritten every 0.5s and on every forced state
    // transition. The MCP server reads the file directly when it wants a
    // fast readiness check.
    //
    // Two write triggers:
    //   1. Forced — fired by editor callbacks the moment a transition starts
    //      (before assembly reload, playmode change). These must be instant
    //      so a reader sees "reloading" before the editor actually freezes.
    //   2. Throttled — every HeartbeatIntervalSec on EditorApplication.update,
    //      carrying whatever BridgeSession.IsCompiling / IsPlaying currently
    //      report.
    //
    // Start/Stop are idempotent. The heartbeat only writes when the bridge
    // has acquired a lock; if the lock acquire failed (no project path), the
    // heartbeat is a silent no-op so a partially-running bridge doesn't spam
    // warnings.
    public static class BridgeHeartbeat
    {
        private const double HeartbeatIntervalSec = 0.5;

        // Minimum age (seconds) a forced TRANSITIONAL state (entering_playmode)
        // must reach before the "transition abandoned" clear may fire. A normal
        // ExitingEditMode → playing transition spends a few ticks with
        // !isPlaying && !isCompiling before the domain reload / play state
        // lands; clearing on the first such tick advertised idle into a dying
        // domain. Success paths clear sooner via their own signals (isPlaying,
        // or Force(playing) from EnteredPlayMode replacing the forced state).
        private const double AbandonedTransitionGraceSec = 5.0;

        private static double _lastWriteTime;
        private static bool _registered;
        // Forced state set by callbacks; cleared after the next throttled
        // write so we don't keep emitting it forever.
        private static string _forcedState;
        private static volatile bool _forcedPending;
        // EditorApplication.timeSinceStartup at which the current forced state
        // was set — age-gates the abandoned-transition clear in WriteNow.
        private static double _forcedAtTime;

        public static bool IsRunning => _registered;

        public static void Start()
        {
            if (_registered) return;
            _registered = true;
            _lastWriteTime = EditorApplication.timeSinceStartup;

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static void Stop()
        {
            if (!_registered) return;
            _registered = false;

            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private static void OnBeforeAssemblyReload()
        {
            // Forced transition: a domain reload is about to freeze the
            // editor. Emit it immediately so readers see "reloading" before
            // /ping goes 503.
            Force(BridgeInstanceLock.StateReloading);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.ExitingEditMode:
                    Force(BridgeInstanceLock.StateEnteringPlaymode);
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    Force(BridgeInstanceLock.StatePlaying);
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    Force(BridgeInstanceLock.StateExitingPlaymode);
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    Force(BridgeInstanceLock.StateIdle);
                    break;
            }
        }

        // Queue a forced state and write immediately. The throttled Tick will
        // clear it once the editor state catches up.
        private static void Force(string state)
        {
            _forcedState = state;
            _forcedPending = true;
            _forcedAtTime = EditorApplication.timeSinceStartup;
            WriteNow();
        }

        private static void Tick()
        {
            if (!_registered) return;

            var now = EditorApplication.timeSinceStartup;
            if (now - _lastWriteTime < HeartbeatIntervalSec) return;

            _lastWriteTime = now;
            WriteNow();
        }

        // Compute the effective state and write the lock. Forced state wins
        // until the underlying flag disagrees with it; then we clear the
        // force and fall back to the derived state.
        private static void WriteNow()
        {
            if (!BridgeInstanceLock.IsAcquired) return;

            string state;
            bool isCompiling = BridgeSession.IsCompiling;
            bool isPlaying = BridgeSession.IsPlaying;

            if (_forcedPending && !string.IsNullOrEmpty(_forcedState))
            {
                state = _forcedState;
                // Clear the force when the live flags have caught up with the
                // transition's target state. compiling→idle, playmode→playing.
                if (_forcedState == BridgeInstanceLock.StateReloading && !isCompiling)
                {
                    _forcedPending = false;
                }
                else if (_forcedState == BridgeInstanceLock.StatePlaying && isPlaying)
                {
                    _forcedPending = false;
                }
                else if (_forcedState == BridgeInstanceLock.StateIdle && !isCompiling && !isPlaying)
                {
                    _forcedPending = false;
                }
                // L6 — the transitional play-mode states have no matching
                // clear branch above, so a force set in OnPlayModeStateChanged
                // could pin "entering_playmode"/"exiting_playmode" until the
                // next Force() if the transition was aborted (e.g. a compile
                // error cancels entering play mode and the editor settles back
                // to idle). Clear them when the editor has reached a state the
                // transition targets or visibly abandoned it:
                //   - entering_playmode → resolved once playing (reached it),
                //     or once idle without compiling for at least the
                //     abandoned-transition grace period (gave up). The age gate
                //     matters: during a NORMAL transition the first heartbeat
                //     tick after ExitingEditMode still reads !isPlaying &&
                //     !isCompiling — the old ungated check was a tautology
                //     there and cleared the force immediately, briefly
                //     advertising idle into a domain about to reload for play
                //     mode.
                //   - exiting_playmode  → resolved once back in edit mode
                //     (!isPlaying) and not mid-reload.
                else if (_forcedState == BridgeInstanceLock.StateEnteringPlaymode
                    && (isPlaying
                        || (!isCompiling
                            && EditorApplication.timeSinceStartup - _forcedAtTime
                                >= AbandonedTransitionGraceSec)))
                {
                    _forcedPending = false;
                }
                else if (_forcedState == BridgeInstanceLock.StateExitingPlaymode
                    && !isPlaying && !isCompiling)
                {
                    _forcedPending = false;
                }
            }
            else if (isCompiling)
            {
                state = BridgeInstanceLock.StateCompiling;
            }
            else if (isPlaying)
            {
                state = BridgeInstanceLock.StatePlaying;
            }
            else
            {
                state = BridgeInstanceLock.StateIdle;
            }

            BridgeInstanceLock.UpdateState(state, isPlaying, isCompiling);
        }
    }
}
