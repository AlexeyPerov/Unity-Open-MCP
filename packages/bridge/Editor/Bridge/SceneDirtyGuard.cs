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
            // Additive scene_create / scene_open do not close any open scene,
            // so a dirty scene cannot be lost and the native save modal cannot
            // fire — the guard would only add friction. The shipped schema tells
            // the agent exactly this ("No effect for 'additive' mode"). Only the
            // default Single mode (which closes every open scene without saving)
            // is preflighted. (B-N8.)
            if (IsAdditiveSceneOp(toolName, body)) return false;
            // Explicit opt-out: the agent takes responsibility for the dirty
            // state (the lightweight --force equivalent — no auto-save).
            return !JsonBody.GetBool(body, "ignore_scene_dirty");
        }

        // scene_create and scene_open accept a `mode` parameter whose "additive"
        // value keeps currently-open scenes open. The body's mode string is
        // matched case-insensitively against "additive"; any other value
        // (including the default "single" and a missing key) is treated as a
        // scene-closing op that the guard must preflight.
        private static bool IsAdditiveSceneOp(string toolName, string body)
        {
            if (toolName != "unity_open_mcp_scene_create"
                && toolName != "unity_open_mcp_scene_open") return false;
            var mode = JsonBody.GetString(body, "mode");
            if (string.IsNullOrEmpty(mode)) return false;
            return mode == "additive" ||
                   System.String.Equals(mode, "additive", System.StringComparison.OrdinalIgnoreCase);
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
        // via the scene-resolver provider, which returns an invalid Scene when
        // no real scene matches (the case in a fresh EditMode test), so the
        // dirty list comes back empty.
        //
        // SceneSetup has only isActive/isLoaded/path — no isDirty. The dirty
        // flag lives on UnityEngine.SceneManagement.Scene; we resolve each
        // setup entry to its Scene by path, then read Scene.isDirty.
        //
        // A3 — a brand-new untitled scene has path == "", so GetSceneByPath("")
        // returns an invalid Scene. Previously such an entry was skipped, so a
        // user who built a hierarchy in a fresh unsaved scene lost it to a
        // single scene_create in Single mode (the guard never refused). Now an
        // empty-path entry is resolved by index via GetSceneAt(i) instead —
        // SceneSetup entries are in load order, which matches SceneManager's
        // loaded-scene order — and its isDirty is honored. An entry we still
        // can't resolve (e.g. a setup whose scene isn't currently loaded) is
        // skipped; refusing on a scene we can't introspect would block every
        // disruptive op in such setups.
        public static GuardResult Check(SceneSetup[] setup)
        {
            return Check(setup, ResolveSceneDefault);
        }

        // Overload that accepts a scene resolver so the index-based unsaved-
        // scene fallback is unit-testable without a live SceneManager. The
        // resolver takes (path, indexInSetup) and returns the matching Scene.
        public static GuardResult Check(
            SceneSetup[] setup, System.Func<string, int, Scene> resolveScene)
        {
            if (setup == null || setup.Length == 0) return GuardResult.Allow();
            if (resolveScene == null) resolveScene = ResolveSceneDefault;

            var dirty = CollectDirtyPaths(setup, resolveScene);
            if (dirty.Count == 0) return GuardResult.Allow();
            return GuardResult.Refuse(dirty.ToArray(), BuildMessage(dirty));
        }

        // Production resolver: path-based lookup, with an index-based fallback
        // for unsaved scenes whose path is empty. Must run on the main thread.
        private static Scene ResolveSceneDefault(string path, int indexInSetup)
        {
            if (!string.IsNullOrEmpty(path))
                return SceneManager.GetSceneByPath(path);
            if (indexInSetup >= 0 && indexInSetup < SceneManager.sceneCount)
                return SceneManager.GetSceneAt(indexInSetup);
            return default;
        }

        private static List<string> CollectDirtyPaths(
            SceneSetup[] setup, System.Func<string, int, Scene> resolveScene)
        {
            var dirty = new List<string>();
            for (int i = 0; i < setup.Length; i++)
            {
                var entry = setup[i];
                if (entry == null) continue;

                var scene = resolveScene(entry.path, i);
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
