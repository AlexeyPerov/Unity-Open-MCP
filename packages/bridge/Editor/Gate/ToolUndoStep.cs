using System;
using UnityEditor;

namespace UnityOpenMcpBridge
{
    // One mutating bridge call is one Editor undo step, the way one user
    // action is. Unity opens a new undo group per input event; bridge calls
    // run on Editor updates with no input event between them, so without a
    // group of their own every call since the last click would share Unity's
    // current group and one Undo would revert all of them.
    //
    // The top-level dispatch wraps the mutation of every gated call (a
    // batch_execute wraps the whole batch, so its steps are one step). The
    // group is named after the tool ("MCP gameobject_create"), which is what
    // Edit > Undo and editor_undo_history show. A call that records nothing
    // leaves no entry: Unity keeps no empty group. Read-only and gate-free
    // direct-response calls (editor_undo / editor_redo among them) never get
    // here.
    internal static class ToolUndoStep
    {
        internal const string LabelPrefix = "MCP ";

        /// <summary>The undo-group name of a call to <paramref name="toolName"/>.</summary>
        internal static string Label(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return LabelPrefix.Trim();
            var name = toolName;
            if (name.StartsWith("unity_open_mcp_", StringComparison.Ordinal)) name = name.Substring("unity_open_mcp_".Length);
            else if (name.StartsWith("unity_senses_", StringComparison.Ordinal)) name = name.Substring("unity_senses_".Length);
            return LabelPrefix + name;
        }

        /// <summary>Runs <paramref name="call"/> in a new undo group and folds
        /// whatever it recorded (a tool or Unity may open further groups) into
        /// that one step, also when the call fails or throws: a partial edit is
        /// still undone as a unit.</summary>
        internal static T Run<T>(string toolName, Func<T> call)
        {
            var label = Label(toolName);
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(label);
            try
            {
                return call();
            }
            finally
            {
                try
                {
                    if (Undo.GetCurrentGroup() > group) Undo.CollapseUndoOperations(group);
                    Undo.SetCurrentGroupName(label);
                }
                catch
                {
                    // Best-effort: never let the grouping mask the call's
                    // own result or fault.
                }
            }
        }
    }
}
