using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace UnityOpenMcpBridge
{
    // Unity's undo stack as the Editor shows it. Unity has no public API for
    // it; the Editor's Undo History window (Edit > Undo History) reads it
    // through the internal Undo.GetUndoList, which lists one entry per undo
    // step (a group: what one Edit > Undo reverts), undo and redo together,
    // oldest first, with the index of the last step that can be undone.
    // Undo.GetRecords lists undo records instead: a step is one or more
    // records, so it is the fallback only, reported as records.
    internal static class UndoStack
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static bool s_Resolved;
        private static MethodInfo s_GetUndoList;
        private static MethodInfo s_GetRecords;
        private static MethodInfo s_HasRedo;

        private static void Resolve()
        {
            if (s_Resolved) return;
            s_Resolved = true;
            foreach (var m in typeof(Undo).GetMethods(Static))
            {
                var ps = m.GetParameters();
                // GetUndoList(List<string>, out int) on 2022.3;
                // GetUndoList(List<string>, out int, bool serialized) on 6000.x.
                if (m.Name == "GetUndoList" && ps.Length >= 2
                    && ps[0].ParameterType == typeof(List<string>)
                    && ps[1].ParameterType == typeof(int).MakeByRefType()
                    && (ps.Length == 2 || (ps.Length == 3 && ps[2].ParameterType == typeof(bool))))
                    s_GetUndoList = m;
                // GetRecords(List<string> undoRecords, List<string> redoRecords).
                else if (m.Name == "GetRecords" && ps.Length == 2
                    && ps[0].ParameterType == typeof(List<string>)
                    && ps[1].ParameterType == typeof(List<string>))
                    s_GetRecords = m;
                else if (m.Name == "HasRedo" && ps.Length == 0 && m.ReturnType == typeof(bool))
                    s_HasRedo = m;
            }
        }

        /// <summary>The undo steps by name, oldest first, undo and redo
        /// together; <paramref name="cursor"/> is the index of the most recent
        /// step that can be undone (-1: nothing to undo). The entries after it
        /// can be redone, the next one first. False when this Editor exposes
        /// no step list.</summary>
        internal static bool TryReadSteps(List<string> steps, out int cursor)
        {
            cursor = -1;
            Resolve();
            if (s_GetUndoList == null) return false;
            try
            {
                // serialized: false, as the Undo History window reads it.
                var args = s_GetUndoList.GetParameters().Length == 3
                    ? new object[] { steps, 0, false }
                    : new object[] { steps, 0 };
                s_GetUndoList.Invoke(null, args);
                cursor = (int)args[1];
                if (cursor >= steps.Count) cursor = steps.Count - 1;
                if (cursor < -1) cursor = -1;
                return true;
            }
            catch
            {
                steps.Clear();
                cursor = -1;
                return false;
            }
        }

        /// <summary>The undo and redo records by name, oldest first. A step is
        /// one or more records (and its name is often a record of its own), so
        /// these are not undo steps.</summary>
        internal static bool TryReadRecords(List<string> undo, List<string> redo)
        {
            Resolve();
            if (s_GetRecords == null) return false;
            try
            {
                s_GetRecords.Invoke(null, new object[] { undo, redo });
                return true;
            }
            catch
            {
                undo.Clear();
                redo.Clear();
                return false;
            }
        }

        /// <summary>Whether there is something to redo; null when this Editor
        /// does not say.</summary>
        internal static bool? HasRedo()
        {
            Resolve();
            if (s_HasRedo != null)
            {
                try { return (bool)s_HasRedo.Invoke(null, null); }
                catch { /* fall through to the step list */ }
            }
            var steps = new List<string>();
            if (!TryReadSteps(steps, out var cursor)) return null;
            return cursor < steps.Count - 1;
        }
    }
}
