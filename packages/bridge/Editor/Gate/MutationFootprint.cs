using System.Collections.Generic;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge
{
    /// <summary>
    /// Where a gated tool call left unsaved changes, and what the gate can say
    /// about them. Built around one dispatch: a snapshot of the dirty
    /// containers (loaded scenes and the open prefab stage) before the call,
    /// the containers the typed tools resolved targets in during it
    /// (<see cref="TargetScope"/>), and the dirty state after it.
    /// </summary>
    internal static class MutationFootprint
    {
        internal const string StageEditSkipReason = "prefab_stage_edit";

        /// <summary>The containers dirty right now, keyed by kind + scene handle.</summary>
        internal static HashSet<TargetContainer> DirtyNow()
        {
            var dirty = new HashSet<TargetContainer>();
            foreach (var c in TargetContainers.All())
                if (c.Scene.isDirty) dirty.Add(c);
            return dirty;
        }

        /// <summary>
        /// The `dirtyScenes` of a call that ran: every scene (and the open
        /// prefab stage, by its prefab asset path) that is dirty now and either
        /// was clean before the call or held a target of it. Null when none.
        /// </summary>
        internal static string[] Dirtied(HashSet<TargetContainer> before, IReadOnlyList<TargetContainer> touched)
        {
            List<string> paths = null;
            foreach (var c in TargetContainers.All())
            {
                if (!c.Scene.isDirty) continue;
                bool became = before == null || !before.Contains(c);
                bool held = false;
                if (touched != null)
                    foreach (var t in touched)
                        if (t.Equals(c)) { held = true; break; }
                if (!became && !held) continue;
                paths ??= new List<string>();
                paths.Add(string.IsNullOrEmpty(c.AssetPath) ? SceneDirtyGuard.UntitledScene : c.AssetPath);
            }
            return paths?.ToArray();
        }

        /// <summary>The scenes in the Hierarchy a call that ran in play mode
        /// resolved or created a target in, or marked edited: what it changed
        /// there lasts until play mode exits. Null when none.</summary>
        internal static string[] PlayModeScenes(IReadOnlyList<TargetContainer> touched)
        {
            if (touched == null) return null;
            List<string> paths = null;
            foreach (var c in touched)
            {
                if (c.Kind != TargetContainerKind.Scene) continue;
                var path = string.IsNullOrEmpty(c.AssetPath) ? SceneDirtyGuard.UntitledScene : c.AssetPath;
                paths ??= new List<string>();
                if (!paths.Contains(path)) paths.Add(path);
            }
            return paths?.ToArray();
        }

        /// <summary>What the agent needs to know after a play-mode edit: it is
        /// gone when play mode exits, as an edit made in the Editor is.</summary>
        internal static string PlayModeAdvisory(string[] scenes)
        {
            var names = "'" + string.Join("', '", scenes) + "'";
            return $"The Editor is in play mode: this call's changes to scene {names} last only until play mode " +
                   "exits, as edits made in play mode in the Editor do (asset files it wrote stay). To keep them, " +
                   "stop play mode (unity_open_mcp_editor_set_state {\"state\":\"stop\"}) and repeat the call.";
        }

        /// <summary>The open prefab stage when this call edited it: a target
        /// was resolved or created in the stage and the stage has unsaved
        /// changes. <paramref name="onlyStage"/> is true when no other
        /// container held a target.</summary>
        internal static TargetContainer EditedStage(out bool onlyStage)
        {
            onlyStage = false;
            var scope = TargetScope.Current;
            if (scope == null) return null;
            var stage = TargetContainers.Stage();
            if (stage == null || !stage.Scene.isDirty) return null;
            bool inStage = false, elsewhere = false;
            foreach (var c in scope.Touched)
            {
                if (c.Equals(stage)) inStage = true;
                else elsewhere = true;
            }
            if (!inStage) return null;
            onlyStage = !elsewhere;
            return stage;
        }

        /// <summary>What the agent needs to know after editing the open stage:
        /// the edit is not in the prefab file yet (or only once Unity's
        /// auto-save writes it), and the gate validated saved files only.</summary>
        internal static string StageAdvisory(TargetContainer stage, bool validationSkipped)
        {
            var autoSave = TargetContainers.StageAutoSave(TargetContainers.OpenStage());
            var path = stage.AssetPath;
            string where;
            if (autoSave == true)
                where = $"This edit is in the open prefab stage of '{path}'. Prefab Mode auto-save is on, so Unity " +
                        "writes it to the prefab file on a later Editor update, after this call returned.";
            else if (autoSave == false)
                where = $"This edit is in the open prefab stage of '{path}' and is not on disk: Prefab Mode " +
                        "auto-save is off, so it stays in the stage until unity_open_mcp_prefab_save or " +
                        "unity_open_mcp_prefab_close {\"save\":true} (prefab_close {\"save\":false} discards it).";
            else
                where = $"This edit is in the open prefab stage of '{path}' and is not in the prefab file yet; " +
                        "it is written by Prefab Mode auto-save (when on), unity_open_mcp_prefab_save or " +
                        "unity_open_mcp_prefab_close {\"save\":true}.";
            var gate = validationSkipped
                ? " The gate validates saved files, so it did not check this edit."
                : " The gate validated the saved files in paths_hint, which do not include this edit.";
            return where + gate + $" Run unity_open_mcp_validate_edit on '{path}' once it is saved.";
        }
    }
}
