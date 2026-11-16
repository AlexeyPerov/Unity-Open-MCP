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
    // Edit > Undo and editor_undo_history show. Read-only and gate-free
    // direct-response calls (editor_undo / editor_redo among them) never get
    // here.
    //
    // A call that records nothing (a refusal, an asset written to disk, an
    // in_prefab edit) leaves no step. Unity's group name is a record of its
    // own: a group holding only its name shows in the undo history, and
    // Edit > Undo drops it and reverts the step before it as well. So the
    // step is closed with IncrementCurrentGroup, which drops a group holding
    // nothing but its name.
    //
    // The name has to come first: Unity drops a name recorded after the edits
    // on the next undo, or hands it to the next group named. Like any record,
    // though, it clears the redo entries, so a call made while there is
    // something to redo is not named up front (a call that then records
    // nothing keeps the redo entries); its step reads its first record's name
    // ("MCP Create GameObject"), as it does when a Unity API opened groups of
    // its own and the first one, holding only the name, was dropped.
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
            bool redoPending = false;
            try
            {
                // Edits recorded before this call belong to the step before it.
                Undo.FlushUndoRecordObjects();
                redoPending = UndoStack.HasRedo() == true;
            }
            catch
            {
                // Best-effort: name the step up front, as without redo.
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            if (!redoPending) Undo.SetCurrentGroupName(label);
            try
            {
                return call();
            }
            finally
            {
                try
                {
                    // RecordObject edits become records here, so they join
                    // this step (an unchanged object records nothing).
                    Undo.FlushUndoRecordObjects();
                    if (Undo.GetCurrentGroup() > group) Undo.CollapseUndoOperations(group);
                    // Close the step: a group holding only its name is dropped.
                    Undo.IncrementCurrentGroup();
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
