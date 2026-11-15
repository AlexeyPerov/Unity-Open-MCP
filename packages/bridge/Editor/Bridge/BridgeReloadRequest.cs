using System;
using System.Reflection;
using System.Threading;
using UnityEditor;

namespace UnityOpenMcpBridge
{
    // A tool call that asks Unity to reload the script domain
    // (EditorUtility.RequestScriptReload from an execute_csharp snippet, a
    // method invoke_method runs, …). Unity starts that reload on a later
    // editor tick, which used to race the worker thread writing the call's own
    // response: when the reload won, the response was torn down with the
    // listener and the snippet's return value was lost.
    //
    // The dispatch now notices the request (Unity reports it as requested and
    // not started) and holds domain reloads (EditorApplication.
    // LockReloadAssemblies) until its response is written; then the reload
    // runs. From the request until the reload starts the reload is published as
    // due — heartbeat state "reloading", /ping transition "reloading", and
    // mutations are refused with editor_reloading — so the next call waits for
    // it instead of being cut off by it.
    //
    // In play mode under "Script Changes While Playing: Recompile After
    // Finished Playing" (the value it had when play mode started) Unity defers
    // a requested reload until play mode ends; the reload is not published as
    // due then (calls keep running while playing) and the answer says when it
    // runs. Under "Recompile And Continue Playing" and "Stop Playing And
    // Recompile" a requested reload runs in play mode and play continues.
    [InitializeOnLoad]
    internal static class BridgeReloadRequest
    {
        // A hold whose response never reports back (the worker died) is let go
        // after this long.
        private const double MaxHoldSeconds = 10.0;
        // How long a released reload is still reported as due if Unity has not
        // started it; a started reload replaces the domain and this state.
        private const double MaxDueSeconds = 15.0;

        // EditorPrefs key of Preferences > General > Script Changes While
        // Playing: 0 recompile and continue playing, 1 recompile after finished
        // playing, 2 stop playing and recompile.
        private const string ScriptChangesWhilePlayingPref = "ScriptCompilationDuringPlay";
        private const int RecompileAfterFinishedPlaying = 1;

        internal const string TimingAfterResponse = "after_response";
        internal const string TimingAfterPlayMode = "after_play_mode";

        // Unity's own "a script reload was requested and has not run" flag.
        // Internal API, looked up once; when a Unity version drops it the
        // dispatch simply does not notice requests (the pre-hold behaviour).
        private static readonly MethodInfo IsScriptReloadRequestedMethod = FindIsRequested();

        private static int _holds;            // reload locks this class took and has not released
        private static int _releases;         // responses written whose lock is not released yet
        private static volatile bool _due;
        private static double _lastHoldAt;
        private static double _releasedAt;
        private static bool _ticking;

        static BridgeReloadRequest()
        {
            // The reload this class published as due has started: the domain
            // is about to be replaced (and every static with it). Any lock
            // still held here cannot be, since a held lock blocks the reload.
            AssemblyReloadEvents.beforeAssemblyReload += () => _due = false;
        }

        private static MethodInfo FindIsRequested()
        {
            try
            {
                return typeof(UnityEditorInternal.InternalEditorUtility).GetMethod("IsScriptReloadRequested",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            }
            catch { return null; }
        }

        /// <summary>Main thread. Whether Unity has a domain reload requested and not started; null when it cannot tell.</summary>
        internal static bool? IsRequested()
        {
            if (IsScriptReloadRequestedMethod == null) return null;
            try { return (bool)IsScriptReloadRequestedMethod.Invoke(null, null); }
            catch { return null; }
        }

        /// <summary>A requested reload is held for a response or about to start. Thread-safe.</summary>
        internal static bool Due => _due;

        /// <summary>
        /// Main thread, at the end of a dispatch that left a reload requested
        /// (it was not requested when the dispatch began). Holds the reload
        /// until <see cref="Release"/> — the caller must call it once its
        /// response is written — and returns when it will run:
        /// <see cref="TimingAfterResponse"/>, published as due, or
        /// <see cref="TimingAfterPlayMode"/> when Unity defers it to the end of
        /// play mode (not published: calls keep running while playing). The
        /// play-mode preference is read now; Unity applies the value it had
        /// when play mode started, so the hold is taken either way.
        /// </summary>
        internal static string Hold()
        {
            var deferred = EditorApplication.isPlaying
                && EditorPrefs.GetInt(ScriptChangesWhilePlayingPref, 0) == RecompileAfterFinishedPlaying;
            EditorApplication.LockReloadAssemblies();
            Interlocked.Increment(ref _holds);
            _lastHoldAt = EditorApplication.timeSinceStartup;
            if (!deferred) _due = true;
            EnsureTicking();
            return deferred ? TimingAfterPlayMode : TimingAfterResponse;
        }

        /// <summary>Any thread: the response of a call that took a hold is written; let its reload run.</summary>
        internal static void Release() => Interlocked.Increment(ref _releases);

        private static void EnsureTicking()
        {
            if (_ticking) return;
            _ticking = true;
            EditorApplication.update += Tick;
        }

        // Main thread: unlock released holds (and a hold whose response never
        // reported back), then stop reporting the reload as due once Unity had
        // the time to start it.
        private static void Tick()
        {
            var now = EditorApplication.timeSinceStartup;
            var expired = now - _lastHoldAt > MaxHoldSeconds;
            while (Volatile.Read(ref _holds) > 0 && (Volatile.Read(ref _releases) > 0 || expired))
            {
                if (Volatile.Read(ref _releases) > 0) Interlocked.Decrement(ref _releases);
                Interlocked.Decrement(ref _holds);
                try { EditorApplication.UnlockReloadAssemblies(); } catch { /* never leave the tick broken */ }
                _releasedAt = now;
            }
            if (Volatile.Read(ref _holds) > 0) return;
            Interlocked.Exchange(ref _releases, 0);
            if (_due && now - _releasedAt > MaxDueSeconds) _due = false;
            if (!_due)
            {
                EditorApplication.update -= Tick;
                _ticking = false;
            }
        }

        /// <summary>The agent-facing step for a call that requested a reload.</summary>
        internal static string NextStep(string timing)
            => timing == TimingAfterPlayMode
                ? "This call requested a script domain reload. Unity runs it when play mode ends "
                    + "(Preferences > General > Script Changes While Playing: Recompile After Finished Playing). "
                    + "After you stop play mode, wait for it with " + BridgeJson.WaitCall("idle", null) + "."
                : "This call requested a script domain reload; it starts right after this response "
                    + (EditorApplication.isPlaying ? "and play mode continues after it " : "")
                    + "(the bridge is down while it runs). Wait for it with " + BridgeJson.WaitCall("idle", null)
                    + " before the next call — it returns once the reload is done.";

        // Test seams (main thread).
        internal static int HoldsForTest => Volatile.Read(ref _holds);
        internal static void TickForTest() => Tick();

        /// <summary>Test seam (main thread): let go of every hold and stop reporting a reload as due.</summary>
        internal static void ResetForTest()
        {
            while (Volatile.Read(ref _holds) > 0)
            {
                Interlocked.Decrement(ref _holds);
                try { EditorApplication.UnlockReloadAssemblies(); } catch { }
            }
            Interlocked.Exchange(ref _releases, 0);
            _due = false;
            if (_ticking)
            {
                EditorApplication.update -= Tick;
                _ticking = false;
            }
        }
    }
}
