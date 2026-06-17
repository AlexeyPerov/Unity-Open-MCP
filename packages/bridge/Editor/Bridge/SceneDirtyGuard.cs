using System.Collections.Generic;
using System.Text;
using UnityEditor.SceneManagement;

namespace UnityOpenMcpBridge
{
    // M13 T4.2 — Active-scene dirty guard.
    //
    // A mutating op that can disrupt the editor (play mode, recompile, scene
    // switch) can trigger Unity's native save modal mid-flow, surprising the
    // agent. Before any RestartThenSettle op we preflight the scene setup and
    // refuse if any loaded scene is dirty, surfacing the dirty scene paths so
    // the agent can save or discard first.
    //
    // The guard runs on the main thread (UnityEditor.SceneManagement is
    // main-thread-only) inside DispatchWithGate, before the mutation. The
    // request-level `ignore_scene_dirty: true` flag is the explicit opt-out —
    // the agent assumes responsibility instead of the bridge auto-saving
    // (auto-save would be a silent mutating side-effect threaded through every
    // tool, which we deliberately avoid).
    public static class SceneDirtyGuard
    {
        public struct GuardResult
        {
            public bool Allowed;
            public string[] DirtyScenePaths;
            public string RefusalMessage;

            public static GuardResult Allow() => new GuardResult { Allowed = true };

            public static GuardResult Refuse(string[] dirtyScenePaths, string message) =>
                new GuardResult
                {
                    Allowed = false,
                    DirtyScenePaths = dirtyScenePaths ?? System.Array.Empty<string>(),
                    RefusalMessage = message ?? ""
                };
        }

        // Returns true if the guard should preflight this tool. Mirrors
        // ToolLifecycle.RequiresDirtyGuard so callers don't double-decide.
        public static bool AppliesTo(string toolName, string body)
        {
            if (!ToolLifecycle.RequiresDirtyGuard(toolName)) return false;
            // Explicit opt-out: the agent takes responsibility for the dirty
            // state (the lightweight --force equivalent — no auto-save).
            if (JsonBody.GetBool(body, "ignore_scene_dirty", false)) return false;
            return true;
        }

        // Must be called on the main thread. Returns Allow when there is no
        // scene setup (e.g. an empty project / no scenes loaded) or when no
        // loaded scene is dirty; otherwise Refuse with the dirty paths.
        public static GuardResult Check()
        {
            SceneSetup[] setup;
            try
            {
                setup = EditorSceneManager.GetSceneManagerSetup();
            }
            catch
            {
                // If we can't read the setup, fall through to Allow — refusing
                // on an API failure would block every disruptive op in setups
                // we can't introspect.
                return GuardResult.Allow();
            }

            if (setup == null || setup.Length == 0) return GuardResult.Allow();

            var dirty = new List<string>();
            foreach (var scene in setup)
            {
                if (scene == null) continue;
                if (scene.isDirty)
                {
                    var path = scene.path;
                    if (string.IsNullOrEmpty(path))
                        path = "(unsaved scene)";
                    dirty.Add(path);
                }
            }

            if (dirty.Count == 0) return GuardResult.Allow();

            return GuardResult.Refuse(dirty.ToArray(), BuildMessage(dirty));
        }

        static string BuildMessage(List<string> dirty)
        {
            var sb = new StringBuilder(256);
            sb.Append("Active scene has unsaved changes (dirty): ");
            for (int i = 0; i < dirty.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(dirty[i]);
            }
            sb.Append(". A disruptive op (recompile / scene switch / play mode) could " +
                      "trigger Unity's native save modal and interrupt the flow. " +
                      "Save or discard first, or retry with ignore_scene_dirty: true " +
                      "to proceed and accept the risk.");
            return sb.ToString();
        }
    }
}
