using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.FrameDebugger
{
    // M20 Plan 1 / T20.1.3 — reflection wrapper around Unity's internal
    // Frame Debugger API.
    //
    // The Frame Debugger has no public scripting API. Its entry point
    // (UnityEditorInternal.FrameDebuggerUtility) moved into a child namespace
    // on Unity 6 (FrameDebuggerInternal.FrameDebuggerUtility), and the event-
    // data retrieval method changed shape across versions. Wrapping all of that
    // here keeps the version drift in one place; the tool above (and any future
    // consumer) talks to a stable surface.
    //
    // Implementation notes: no Newtonsoft, no dynamic — hand-rolled field
    // extraction returning a plain Dictionary<string,object>.
    internal static class FrameDebuggerApi
    {
        private static readonly Type UtilType;
        private static readonly PropertyInfo EventCountProp;
        private static readonly MethodInfo EnableMethod;
        private static readonly MethodInfo GetFrameEventsMethod;
        private static readonly MethodInfo GetEventDataMethod;
        private static readonly MethodInfo GetEventInfoNameMethod;
        private static readonly Type EventDataType;
        private static readonly Type FrameEventType;
        private static readonly bool _available;

        // Lazy type lookup. The ReflectionException is swallowed (Available=false)
        // so the tool degrades to a clear `frame_debugger_unavailable` error
        // rather than crashing the dispatch on a Unity build without the API.
        static FrameDebuggerApi()
        {
            try
            {
                // Unity 6+: FrameDebuggerUtility moved to a child namespace.
                UtilType = Type.GetType(
                    "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility, UnityEditor");
                // Unity 2021–2022: original location.
                UtilType ??= Type.GetType(
                    "UnityEditorInternal.FrameDebuggerUtility, UnityEditor");

                if (UtilType == null) return;

                EventCountProp = UtilType.GetProperty("count", BindingFlags.Public | BindingFlags.Static)
                              ?? UtilType.GetProperty("eventsCount", BindingFlags.Public | BindingFlags.Static)
                              ?? UtilType.GetProperty("eventCount", BindingFlags.Public | BindingFlags.Static);

                EnableMethod = UtilType.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static,
                                   null, new[] { typeof(bool), typeof(int) }, null)
                            ?? UtilType.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static,
                                   null, new[] { typeof(bool) }, null)
                            ?? UtilType.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static);

                GetFrameEventsMethod = UtilType.GetMethod("GetFrameEvents", BindingFlags.Public | BindingFlags.Static);
                GetEventInfoNameMethod = UtilType.GetMethod("GetFrameEventInfoName", BindingFlags.Public | BindingFlags.Static);

                // Unity 6: GetFrameEventData(int, FrameDebuggerEventData) — 2 params, returns bool.
                // Older: GetFrameEventData(int) — 1 param, returns the event data object.
                EventDataType = Type.GetType(
                        "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData, UnityEditor")
                             ?? Type.GetType(
                        "UnityEditorInternal.FrameDebuggerEventData, UnityEditor");

                if (EventDataType != null)
                {
                    GetEventDataMethod = UtilType.GetMethod("GetFrameEventData", BindingFlags.Public | BindingFlags.Static,
                                             null, new[] { typeof(int), EventDataType }, null);
                }
                GetEventDataMethod ??= UtilType.GetMethod("GetFrameEventData", BindingFlags.Public | BindingFlags.Static,
                                         null, new[] { typeof(int) }, null)
                                     ?? UtilType.GetMethod("GetFrameEventData", BindingFlags.Public | BindingFlags.Static);

                // UnityEditorInternal.FrameDebuggerEvent — the descriptor array
                // returned by GetFrameEvents(). Carries type / gameObject id.
                FrameEventType = Type.GetType(
                        "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEvent, UnityEditor")
                             ?? Type.GetType(
                        "UnityEditorInternal.FrameDebuggerEvent, UnityEditor");

                _available = EventCountProp != null && EnableMethod != null;
            }
            catch
            {
                _available = false;
            }
        }

        public static bool Available => _available;

        // Opens the Frame Debugger window via the Window/Analysis/Frame Debugger
        // menu (the same menu the user clicks). Returns true when the window
        // had to be opened (was not already present) so callers can report it.
        public static bool OpenWindowIfNeeded()
        {
            bool wasOpen = false;
            try
            {
                // EditorWindow.GetWindow with makeVisible:false does not steal
                // focus but guarantees the window exists. We probe first to
                // detect whether it was already open.
                var existing = Resources.FindObjectsOfTypeAll<EditorWindow>();
                foreach (var w in existing)
                {
                    if (w.GetType().Name.IndexOf("FrameDebugger", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        wasOpen = true;
                        break;
                    }
                }
            }
            catch { /* probe failure is non-fatal */ }

            // ExecuteMenuItem both opens the window and reflects the user path
            // (Frame Debugger has no public GetWindow type). Returns true when
            // the menu resolved; when it does not, we still report the
            // open-state change so the response reflects what happened.
            try
            {
                EditorApplication.ExecuteMenuItem("Window/Analysis/Frame Debugger");
            }
            catch { /* menu path missing on this layout — non-fatal */ }
            return !wasOpen;
        }

        public static void SetEnabled(bool enabled)
        {
            if (EnableMethod == null)
                throw new InvalidOperationException("SetEnabled method not resolved.");

            int paramCount = EnableMethod.GetParameters().Length;
            if (paramCount == 2)
                EnableMethod.Invoke(null, new object[] { enabled, 0 });
            else if (paramCount == 1)
                EnableMethod.Invoke(null, new object[] { enabled });
            else
                throw new InvalidOperationException(
                    $"SetEnabled has unexpected {paramCount} parameters.");
        }

        public static int GetEventCount()
        {
            if (EventCountProp == null) return 0;
            try { return (int)EventCountProp.GetValue(null); }
            catch { return 0; }
        }

        // Returns one draw-call entry as a Dictionary<string,object> with the
        // fields the tool surfaces (index, name, type, shader, pass, render
        // target, vertex/index/instance counts, mesh). Fields absent on the
        // current Unity build are simply omitted — the response never carries
        // null placeholders for unknown keys.
        public static Dictionary<string, object> GetEvent(int index)
        {
            var entry = new Dictionary<string, object> { ["index"] = index };

            // Event name (the human label the Frame Debugger window shows).
            if (GetEventInfoNameMethod != null)
            {
                try
                {
                    var name = GetEventInfoNameMethod.Invoke(null, new object[] { index }) as string;
                    if (!string.IsNullOrEmpty(name)) entry["name"] = name;
                }
                catch { /* skip */ }
            }

            // Descriptor array (type / gameObjectInstanceID per event).
            object[] descriptors = GetDescriptorArray();
            if (descriptors != null && index < descriptors.Length)
            {
                var desc = descriptors[index];
                if (desc != null)
                {
                    var descType = desc.GetType();
                    TryAddField(descType, desc, "type", entry, "eventType");
                    TryAddField(descType, desc, "gameObjectInstanceID", entry);
                }
            }

            // Detailed event data (shader / pass / render target / counts).
            var eventData = GetEventData(index);
            if (eventData != null)
            {
                var edType = eventData.GetType();
                TryAddField(edType, eventData, "shaderName", entry, "shader");
                TryAddField(edType, eventData, "shaderKeywordNames", entry, "shaderKeywords");
                TryAddField(edType, eventData, "passName", entry, "pass");
                TryAddField(edType, eventData, "passIndex", entry);
                TryAddField(edType, eventData, "rtName", entry, "renderTarget");
                TryAddField(edType, eventData, "rtWidth", entry, "renderTargetWidth");
                TryAddField(edType, eventData, "rtHeight", entry, "renderTargetHeight");
                TryAddField(edType, eventData, "vertexCount", entry);
                TryAddField(edType, eventData, "indexCount", entry);
                TryAddField(edType, eventData, "instanceCount", entry);
                TryAddField(edType, eventData, "meshName", entry, "mesh");
            }

            return entry;
        }

        private static object[] GetDescriptorArray()
        {
            if (GetFrameEventsMethod == null || FrameEventType == null) return null;
            try
            {
                var raw = GetFrameEventsMethod.Invoke(null, null);
                if (raw is Array arr)
                {
                    var boxed = new object[arr.Length];
                    arr.CopyTo(boxed, 0);
                    return boxed;
                }
            }
            catch { /* fall through */ }
            return null;
        }

        private static object GetEventData(int index)
        {
            if (GetEventDataMethod == null) return null;
            try
            {
                var paramInfos = GetEventDataMethod.GetParameters();

                // Unity 6: bool GetFrameEventData(int, FrameDebuggerEventData).
                if (paramInfos.Length == 2 && EventDataType != null)
                {
                    var instance = Activator.CreateInstance(EventDataType);
                    var args = new object[] { index, instance };
                    var ok = GetEventDataMethod.Invoke(null, args);
                    return ok is true ? args[1] : null;
                }

                // Older: FrameDebuggerEventData GetFrameEventData(int).
                return GetEventDataMethod.Invoke(null, new object[] { index });
            }
            catch { /* skip event data for this index */ }
            return null;
        }

        private static void TryAddField(
            Type type, object obj, string fieldName,
            Dictionary<string, object> dict, string outputKey = null)
        {
            try
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance)
                         ?? type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                var prop = type.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance)
                        ?? type.GetProperty(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);

                object val = field != null ? field.GetValue(obj)
                           : prop != null ? prop.GetValue(obj)
                           : null;
                if (val == null) return;

                // Enums render as their string name; arrays of strings join
                // into a comma-separated value so the JSON stays flat.
                if (val.GetType().IsEnum)
                {
                    dict[outputKey ?? fieldName] = val.ToString();
                    return;
                }
                if (val is string[] arr)
                {
                    dict[outputKey ?? fieldName] = string.Join(", ", arr);
                    return;
                }

                dict[outputKey ?? fieldName] = val;
            }
            catch { /* skip unavailable field */ }
        }
    }
}
