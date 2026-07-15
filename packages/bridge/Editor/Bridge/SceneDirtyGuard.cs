using System.Collections.Generic;
using System.Text;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

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
            return !JsonBody.GetBool(body, "ignore_scene_dirty");
        }

        // Must be called on the main thread. Returns Allow when there is no
        // scene setup (e.g. an empty project / no scenes loaded) or when no
        // loaded scene is dirty; otherwise Refuse with the dirty paths.
        public static GuardResult Check()
        {
            return Check(() => EditorSceneManager.GetSceneManagerSetup());
        }

        // Overload that accepts the scene-setup provider so the fail-open path
        // is unit-testable without synthesizing a live scene that throws. The
        // production Check() supplies the real EditorSceneManager call.
        //
        // Fail-open policy is unchanged: if the provider throws, the guard
        // returns Allow (refusing on an API failure would block every
        // disruptive op in setups we can't introspect). The exception is now
        // logged once as a warning so a real failure — e.g. a corrupted scene
        // setup — is observable instead of silently disabling the guard.
        public static GuardResult Check(System.Func<SceneSetup[]> getSetup)
        {
            SceneSetup[] setup;
            try
            {
                setup = getSetup();
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogWarning(
                    "[unity-open-mcp] SceneDirtyGuard could not read the scene " +
                    "setup and fell back to Allow (fail-open). A disruptive op " +
                    "may proceed without the dirty-scene preflight. Exception: "
                    + e.Message);
                return GuardResult.Allow();
            }

            return Check(setup);
        }

        // Pure decision over a scene setup snapshot. Split out from Check() so
        // the dirty-path collection is unit-testable without synthesizing live
        // scenes: a synthetic SceneSetup[] fed through here resolves each entry
        // via EditorSceneManager.GetSceneByPath, which returns an invalid Scene
        // when no real scene matches (the case in a fresh EditMode test), so
        // the dirty list comes back empty.
        //
        // SceneSetup has only isActive/isLoaded/path — no isDirty. The dirty
        // flag lives on UnityEngine.SceneManagement.Scene; we resolve each
        // setup entry to its Scene by path, then read Scene.isDirty. An entry
        // we can't resolve (unsaved scene with empty path, or a setup whose
        // scene isn't currently loaded) is skipped — refusing on a scene we
        // can't introspect would block every disruptive op in such setups.
        public static GuardResult Check(SceneSetup[] setup)
        {
            if (setup == null || setup.Length == 0) return GuardResult.Allow();

            var dirty = CollectDirtyPaths(setup);
            if (dirty.Count == 0) return GuardResult.Allow();
            return GuardResult.Refuse(dirty.ToArray(), BuildMessage(dirty));
        }

        private static List<string> CollectDirtyPaths(SceneSetup[] setup)
        {
            var dirty = new List<string>();
            foreach (var entry in setup)
            {
                if (entry == null) continue;

                var scene = SceneManager.GetSceneByPath(entry.path);
                if (!scene.IsValid()) continue;

                if (scene.isDirty)
                {
                    var path = entry.path;
                    if (string.IsNullOrEmpty(path))
                        path = "(unsaved scene)";
                    dirty.Add(path);
                }
            }
            return dirty;
        }

        private static string BuildMessage(List<string> dirty)
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
