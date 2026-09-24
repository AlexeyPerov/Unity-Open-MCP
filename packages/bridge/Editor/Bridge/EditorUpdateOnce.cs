using System;
using UnityEditor;

namespace UnityOpenMcpBridge
{
    // One-shot EditorApplication.update callback.
    //
    // Preferred over EditorApplication.delayCall for work that must run in an
    // Editor nobody is looking at: delayCall is flushed alongside inspector
    // repaints and can stay pending indefinitely in an unfocused or headless
    // session, while update ticks regardless. Every deferred-start path in the
    // bridge (test runs, project-command jobs, UPM re-pins, cache warm-ups)
    // goes through here so the guarantee is made in one place.
    internal static class EditorUpdateOnce
    {
        internal static void Schedule(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            EditorApplication.CallbackFunction callback = null;
            callback = () =>
            {
                EditorApplication.update -= callback;
                action();
            };
            EditorApplication.update += callback;
        }
    }
}
