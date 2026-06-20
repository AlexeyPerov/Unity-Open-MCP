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

        private static double _lastWriteTime;
        private static bool _registered;
        // Forced state set by callbacks; cleared after the next throttled
        // write so we don't keep emitting it forever.
        private static string _forcedState;
        private static volatile bool _forcedPending;

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
