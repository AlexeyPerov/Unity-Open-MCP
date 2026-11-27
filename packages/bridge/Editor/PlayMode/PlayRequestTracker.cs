using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;

namespace UnityOpenMcpBridge
{
    // What became of the last play-mode request editor_set_state made.
    //
    // `EditorApplication.isPlaying = true` only requests play mode; Unity starts
    // the transition on a later update, and a project's playModeStateChanged
    // handler (or a compile error) can cancel it there. The call's response is
    // long written by then, so without this record an agent waiting for play
    // mode only learns that the Editor stayed in Edit Mode, never why.
    //
    // The record (SessionState: it survives the play-mode domain reload) is
    // `requested` until play mode is entered (then cleared) or the Editor stops
    // changing play mode without entering it (`cancelled`, with the reason and
    // the EditorApplication.delayCall callbacks still pending — a handler that
    // defers its work to delayCall stalls in an unfocused Editor, where Unity
    // does not run them). /ping publishes it (`playRequest`) for the server's
    // `wait_until: "playing"` wait. Main thread throughout; /ping reads the
    // volatile JSON copy.
    [InitializeOnLoad]
    internal static class PlayRequestTracker
    {
        private const string Prefix = "UnityOpenMcp.PlayRequest.";
        private const string StatusKey = Prefix + "status";
        private const string RequestedAtKey = Prefix + "requestedAt";
        private const string RequestedTickKey = Prefix + "requestedTick";
        private const string SawExitingKey = Prefix + "sawExiting";
        private const string RecordKey = Prefix + "record";
        private const int MaxListedTargets = 5;
        // A request Unity has not acted on within this long is not pending.
        private const double StartGraceSeconds = 1.0;

        private static volatile string _pingJson;

        static PlayRequestTracker()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            _pingJson = SessionState.GetString(RecordKey, "");
            if (_pingJson.Length == 0) _pingJson = null;
            if (SessionState.GetString(StatusKey, "") == "requested") Watch();
        }

        /// <summary>The /ping member (`,"playRequest":{…}`), or "" without a record. Any thread.</summary>
        internal static string PingMember()
        {
            var json = _pingJson;
            return string.IsNullOrEmpty(json) ? "" : ",\"playRequest\":" + json;
        }

        /// <summary>editor_set_state is about to request play mode (main thread).</summary>
        internal static void NoteRequested()
        {
            SessionState.SetString(StatusKey, "requested");
            SessionState.SetString(RequestedAtKey, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            SessionState.SetFloat(RequestedTickKey, (float)EditorApplication.timeSinceStartup);
            SessionState.SetBool(SawExitingKey, false);
            Publish("{\"status\":\"requested\",\"requestedAt\":" + BridgeJson.EscapeString(SessionState.GetString(RequestedAtKey, "")) + "}");
            Watch();
        }

        private static void Watch()
        {
            EditorApplication.update -= Check;
            EditorApplication.update += Check;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            var status = SessionState.GetString(StatusKey, "");
            if (status == "requested")
            {
                if (change == PlayModeStateChange.ExitingEditMode) SessionState.SetBool(SawExitingKey, true);
                else if (change == PlayModeStateChange.EnteredPlayMode) Clear();
            }
            // A play attempt made some other way (the toolbar, a snippet) makes
            // a cancelled record stale: it must not answer for that attempt.
            else if (status == "cancelled"
                && (change == PlayModeStateChange.ExitingEditMode || change == PlayModeStateChange.EnteredPlayMode))
                Clear();
        }

        private static void Check()
        {
            if (SessionState.GetString(StatusKey, "") != "requested") { EditorApplication.update -= Check; return; }
            if (EditorApplication.isPlaying) { Clear(); return; }
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var sawExiting = SessionState.GetBool(SawExitingKey, false);
            var elapsed = EditorApplication.timeSinceStartup - SessionState.GetFloat(RequestedTickKey, 0f);
            if (!sawExiting && elapsed >= 0 && elapsed < StartGraceSeconds) return;
            MarkCancelled(sawExiting);
            EditorApplication.update -= Check;
        }

        private static void Clear()
        {
            SessionState.EraseString(StatusKey);
            SessionState.EraseString(RecordKey);
            _pingJson = null;
            EditorApplication.update -= Check;
        }

        private static void Publish(string json)
        {
            SessionState.SetString(RecordKey, json);
            _pingJson = json;
        }

        private static void MarkCancelled(bool sawExiting)
        {
            SessionState.SetString(StatusKey, "cancelled");
            var reason = EditorUtility.scriptCompilationFailed ? "compile_errors"
                : sawExiting ? "cancelled_in_transition" : "not_started";
            var pending = PendingDelayCalls();
            var sb = new StringBuilder(256);
            sb.Append("{\"status\":\"cancelled\",\"reason\":").Append(BridgeJson.EscapeString(reason));
            sb.Append(",\"requestedAt\":").Append(BridgeJson.EscapeString(SessionState.GetString(RequestedAtKey, "")));
            sb.Append(",\"cancelledAt\":").Append(BridgeJson.EscapeString(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
            sb.Append(",\"pendingDelayCalls\":").Append(pending.Length);
            sb.Append(",\"delayCallTargets\":[");
            for (int i = 0; i < pending.Length && i < MaxListedTargets; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(BridgeJson.EscapeString(pending[i]));
            }
            sb.Append("]}");
            Publish(sb.ToString());
        }

        /// <summary>The pending EditorApplication.delayCall callbacks as `Type.Method` names (main thread).</summary>
        internal static string[] PendingDelayCalls()
        {
            try
            {
                var list = EditorApplication.delayCall?.GetInvocationList();
                if (list == null) return Array.Empty<string>();
                return list.Select(d => d.Method == null ? "?"
                    : (d.Method.DeclaringType != null ? d.Method.DeclaringType.FullName + "." : "") + d.Method.Name).ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>
        /// Run the pending EditorApplication.delayCall callbacks once (main
        /// thread), as Unity's own update does. They are project and package
        /// code: only on an explicit request. Returns how many were pending, or
        /// -1 when this Unity has no Internal_CallDelayFunctions.
        /// </summary>
        internal static int FlushDelayCalls(out string error)
        {
            error = null;
            var count = PendingDelayCalls().Length;
            var method = typeof(EditorApplication).GetMethod("Internal_CallDelayFunctions",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) return -1;
            try { method.Invoke(null, null); }
            catch (TargetInvocationException e) { error = (e.InnerException ?? e).Message; }
            catch (Exception e) { error = e.Message; }
            return count;
        }
    }
}
