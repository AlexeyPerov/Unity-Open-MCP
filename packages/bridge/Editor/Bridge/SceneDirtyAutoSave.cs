using System;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityOpenMcpBridge
{
    // Automation helper: before a RestartThenSettle op would refuse with
    // scene_dirty, optionally save every loaded dirty scene so Unity's native
    // "unsaved changes" modal never blocks the main thread.
    //
    // Opt-in via UNITY_OPEN_MCP_AUTO_SAVE_DIRTY_SCENES=1 (Unity launch env)
    // or BridgeProjectSettings.autoSaveDirtyScenes (demo / CI projects).
    public static class SceneDirtyAutoSave
    {
        public static bool IsEnabled =>
            string.Equals(
                Environment.GetEnvironmentVariable("UNITY_OPEN_MCP_AUTO_SAVE_DIRTY_SCENES"),
                "1",
                StringComparison.Ordinal)
            || BridgeProjectSettings.Data.autoSaveDirtyScenes;

        public static bool TrySaveAllDirty(out int savedCount, out int skippedCount)
        {
            savedCount = 0;
            skippedCount = 0;

            SceneSetup[] setup;
            try
            {
                setup = EditorSceneManager.GetSceneManagerSetup();
            }
            catch
            {
                return false;
            }

            if (setup == null || setup.Length == 0) return true;

            foreach (var entry in setup)
            {
                if (entry == null) continue;

                var scene = SceneManager.GetSceneByPath(entry.path);
                if (!scene.IsValid() || !scene.isLoaded || !scene.isDirty) continue;

                if (string.IsNullOrEmpty(scene.path))
                {
                    skippedCount++;
                    continue;
                }

                try
                {
                    if (EditorSceneManager.SaveScene(scene, scene.path))
                        savedCount++;
                    else
                        skippedCount++;
                }
                catch
                {
                    skippedCount++;
                }
            }

            return skippedCount == 0;
        }
    }
}
