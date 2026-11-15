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

        // The lock's first write in a domain (main thread). It carries the
        // Editor's state as it is now and the compile block SessionState kept
        // across the reload: a fresh domain's flags still hold their defaults
        // (not playing, no compile block) until the first update tick, and
        // after a reload in play mode that tick comes seconds later — a lock
        // written from those defaults read `idle` / `isPlaying: false` /
        // `compile: null` in the middle of a play session.
        internal static void AcquireLock(string projectPath, int port)
        {
            Snapshot(out var state, out var isPlaying, out var isCompiling, out var compileJson);
            BridgeInstanceLock.Acquire(projectPath, port, state, isPlaying, isCompiling, compileJson);
        }

        // The Editor state the lock reports when no forced transition is
        // pending. Pure: a domain reload that comes back inside play mode, or
        // in the middle of entering it, is playing / entering_playmode, never
        // idle.
        internal static string DeriveState(bool reloadDue, bool isCompiling, bool isPlaying, bool willChangePlaymode)
        {
            // A call requested a domain reload that has not started yet:
            // readers must wait for it, not dispatch into it.
            if (reloadDue) return BridgeInstanceLock.StateReloading;
            if (isCompiling) return BridgeInstanceLock.StateCompiling;
            if (willChangePlaymode && !isPlaying) return BridgeInstanceLock.StateEnteringPlaymode;
            if (!willChangePlaymode && isPlaying) return BridgeInstanceLock.StateExitingPlaymode;
            return isPlaying ? BridgeInstanceLock.StatePlaying : BridgeInstanceLock.StateIdle;
        }

        // Main thread: the flags read from the Editor now (not the values the
        // last update tick cached — the heartbeat's own update callback can run
        // before BridgeSession's in a tick, and a play-mode callback before its
        // refresh) plus the compile block.
        private static void Snapshot(out string state, out bool isPlaying, out bool isCompiling, out string compileJson)
        {
            BridgeSession.RefreshVolatileState();
            isCompiling = BridgeSession.IsCompiling;
            isPlaying = BridgeSession.IsPlaying;
            state = DeriveState(BridgeReloadRequest.Due, isCompiling, isPlaying, BridgeSession.WillChangePlaymode);
            compileJson = null;
            try { compileJson = BridgeCompileState.LockJson(); }
            catch { /* the compile block is advisory; the heartbeat must still beat */ }
        }

        // Compute the effective state and write the lock. Forced state wins
        // until the underlying flag disagrees with it; then we clear the
        // force and fall back to the derived state.
        private static void WriteNow()
        {
            if (!BridgeInstanceLock.IsAcquired) return;

            Snapshot(out var state, out var isPlaying, out var isCompiling, out var compileJson);

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

            BridgeInstanceLock.UpdateState(state, isPlaying, isCompiling, compileJson);
        }

        // Write the lock now, outside the 0.5 s cadence (main thread). A tool
        // that just requested a compile publishes it before its response leaves,
        // so a reader that polls the lock next already sees the request.
        internal static void WriteNowIfRunning()
        {
            if (_registered) WriteNow();
        }
    }
}
