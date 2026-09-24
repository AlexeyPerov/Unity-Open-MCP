using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M13 T4.2 — active-scene dirty guard.
    //
    // The guard's AppliesTo() is a pure decision over (toolName, body) and is
    // fully unit-testable. Check() enumerates the scenes SceneManager has
    // loaded (not GetSceneManagerSetup(), which omits unsaved additive scenes)
    // and refuses when any of them is dirty — including an untitled one whose
    // path is empty. The live-session tests below derive their expectation
    // from the same SceneManager state instead of assuming a clean session:
    // earlier fixtures legitimately dirty the scratch scene.
    public static class SceneDirtyGuardTests
    {
        // ----- AppliesTo: which tools are guarded -----

        [TestCase("unity_open_mcp_execute_csharp", ExpectedResult = true)]
        [TestCase("unity_open_mcp_invoke_method", ExpectedResult = true)]
        [TestCase("unity_open_mcp_execute_menu", ExpectedResult = true)]
        public static bool AppliesTo_DisruptiveTools_Guarded(string tool)
        {
            return SceneDirtyGuard.AppliesTo(tool, "{}");
        }

        [TestCase("unity_open_mcp_apply_fix", ExpectedResult = false)]
        [TestCase("unity_open_mcp_reserialize", ExpectedResult = false)]
        [TestCase("unity_open_mcp_find_members", ExpectedResult = false)]
        [TestCase("unity_senses_run_tests", ExpectedResult = false)]
        [TestCase("unity_open_mcp_validate_edit", ExpectedResult = false)]
        public static bool AppliesTo_NonDisruptiveTools_NotGuarded(string tool)
        {
            return SceneDirtyGuard.AppliesTo(tool, "{}");
        }

        [Test]
        public static void AppliesTo_UnknownTool_NotGuarded()
        {
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_brand_new", "{}"));
        }

        // ----- AppliesTo: ignore_scene_dirty opt-out -----

        [Test]
        public static void AppliesTo_IgnoreSceneDirtyTrue_SkipsGuard()
        {
            // The explicit opt-out is the lightweight --force equivalent: the
            // agent takes responsibility instead of the bridge auto-saving.
            Assert.IsFalse(
                SceneDirtyGuard.AppliesTo("unity_open_mcp_execute_csharp",
                    "{\"ignore_scene_dirty\":true}"));
        }

        [Test]
        public static void AppliesTo_IgnoreSceneDirtyFalse_KeepsGuard()
        {
            Assert.IsTrue(
                SceneDirtyGuard.AppliesTo("unity_open_mcp_execute_csharp",
                    "{\"ignore_scene_dirty\":false}"));
        }

        [Test]
        public static void AppliesTo_IgnoreSceneDirtyOmitted_KeepsGuard()
        {
            Assert.IsTrue(
                SceneDirtyGuard.AppliesTo("unity_open_mcp_execute_csharp", "{}"));
        }

        // ----- B-N8: additive scene_create / scene_open bypass the guard -----
        //
        // Additive mode keeps currently-open scenes open, so a dirty scene can
        // neither be lost nor trigger the native save modal. The guard would
        // only add friction on the most common (interactive) path. Only the
        // default Single mode — which closes every open scene without saving —
        // is preflighted. This matches the shipped schema's promise that the
        // dirty guard has "No effect for 'additive' mode".

        [TestCase("unity_open_mcp_scene_create", ExpectedResult = false)]
        [TestCase("unity_open_mcp_scene_open", ExpectedResult = false)]
        public static bool AppliesTo_AdditiveSceneOp_SkipsGuard(string tool)
        {
            return SceneDirtyGuard.AppliesTo(tool, "{\"mode\":\"additive\"}");
        }

        [TestCase("unity_open_mcp_scene_create", ExpectedResult = true)]
        [TestCase("unity_open_mcp_scene_open", ExpectedResult = true)]
        public static bool AppliesTo_SingleSceneOp_Guarded(string tool)
        {
            // The default Single mode closes open scenes — must stay guarded.
            return SceneDirtyGuard.AppliesTo(tool, "{\"mode\":\"single\"}");
        }

        [TestCase("unity_open_mcp_scene_create", ExpectedResult = true)]
        [TestCase("unity_open_mcp_scene_open", ExpectedResult = true)]
        public static bool AppliesTo_SceneOp_DefaultMode_Guarded(string tool)
        {
            // Missing mode ⇒ Unity's default (Single) ⇒ guarded.
            return SceneDirtyGuard.AppliesTo(tool, "{}");
        }

        [Test]
        public static void AppliesTo_AdditiveSceneOp_CaseInsensitive()
        {
            // A case variant must still bypass (mirrors the bool-quoting fix
            // class — agent-authored bodies are not always lower-case).
            Assert.IsFalse(
                SceneDirtyGuard.AppliesTo("unity_open_mcp_scene_create",
                    "{\"mode\":\"Additive\"}"));
        }

        [Test]
        public static void AppliesTo_AdditiveSceneOp_IgnoreStillRespected()
        {
            // Additive bypasses regardless; the assertion documents that the
            // two opt-outs are independent and additive already implies no risk.
            Assert.IsFalse(
                SceneDirtyGuard.AppliesTo("unity_open_mcp_scene_create",
                    "{\"mode\":\"additive\",\"ignore_scene_dirty\":false}"));
        }

        // ----- Check(): the live SceneManager contract -----

        [Test]
        public static void Check_LoadedScenes_RefusesExactlyWhenOneIsDirty()
        {
            // Allowed iff no loaded scene is dirty; a refused result names every
            // dirty scene, with "(unsaved scene)" standing in for an empty path.
            var expected = new System.Collections.Generic.List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                    expected.Add(string.IsNullOrEmpty(scene.path) ? "(unsaved scene)" : scene.path);
            }

            var result = SceneDirtyGuard.Check();

            Assert.AreEqual(expected.Count == 0, result.Allowed);
            if (expected.Count == 0)
                Assert.IsNull(result.DirtyScenePaths);
            else
                CollectionAssert.AreEqual(expected, result.DirtyScenePaths);
        }

        [Test]
        public static void Check_DirtyUnsavedAdditiveScene_Refuses()
        {
            // The reason Check() enumerates SceneManager: an unsaved scene has
            // no path and GetSceneManagerSetup() would not list it, yet a
            // Single-mode scene switch discards it without a prompt. Unity
            // refuses to add a scene next to an unsaved untitled one, so in
            // that session (the usual headless case) the untitled scratch
            // scene itself is the subject; otherwise an additive scratch scene
            // is created so the test never replaces the session's own scene.
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool reuse = string.IsNullOrEmpty(active.path);
            bool wasDirty = active.isDirty;
            var scratch = reuse ? active : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            GameObject marker = null;
            try
            {
                // Put the marker INSIDE the scratch scene: NewScene(Additive) does
                // not activate the new scene, so a bare `new GameObject` would land
                // in — and permanently dirty — the session's own active scene.
                marker = new GameObject("__MCPTest_SceneDirtyGuard");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(marker, scratch);
                EditorSceneManager.MarkSceneDirty(scratch);
                Assert.IsTrue(scratch.isDirty, "precondition: additive scratch scene is dirty");

                var result = SceneDirtyGuard.Check();

                Assert.IsFalse(result.Allowed);
                CollectionAssert.Contains(result.DirtyScenePaths, "(unsaved scene)");
                StringAssert.Contains("ignore_scene_dirty", result.RefusalMessage);
            }
            finally
            {
                if (marker != null) UnityEngine.Object.DestroyImmediate(marker);
                if (!reuse) EditorSceneManager.CloseScene(scratch, true);
                else if (!wasDirty) ClearSceneDirtiness(scratch);
            }
        }

        // The session's untitled scene is reused when Unity refuses an additive
        // scene beside it; hand it back in the state we found it.
        private static void ClearSceneDirtiness(UnityEngine.SceneManagement.Scene scene)
        {
            typeof(EditorSceneManager)
                .GetMethod("ClearSceneDirtiness",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.Invoke(null, new object[] { scene });
        }

        // ----- Check(SceneSetup[]): the dirty-branch seam -----
        //
        // Regression for the CS1061 that shipped because no test reached the
        // per-scene dirty loop: SceneSetup has no isDirty, and the original
        // code read it off the setup directly. The fix resolves each setup to
        // its Scene via EditorSceneManager.GetSceneByPath and reads Scene.isDirty.
        // In a fresh EditMode session no real scene matches a synthetic setup's
        // path, so GetSceneByPath returns an invalid Scene and the entry is
        // skipped — the guard must Allow rather than throw.

        [Test]
        public static void Check_SyntheticSetup_NoMatchingScene_Allows()
        {
            // A setup pointing at a scene that isn't actually loaded: GetSceneByPath
            // returns an invalid Scene, the entry is skipped, and no dirty path is
            // collected. Critically this must NOT throw CS1061 (the shipped bug).
            var setup = new[]
            {
                new SceneSetup { path = "Assets/DoesNotExist.unity", isLoaded = true },
            };
            var result = SceneDirtyGuard.Check(setup);
            Assert.IsTrue(result.Allowed);
        }

        [Test]
        public static void Check_NullSetup_Allows()
        {
            Assert.IsTrue(SceneDirtyGuard.Check((SceneSetup[])null).Allowed);
        }

        [Test]
        public static void Check_EmptySetup_Allows()
        {
            Assert.IsTrue(SceneDirtyGuard.Check(new SceneSetup[0]).Allowed);
        }

        // ----- A3: unsaved ("untitled") scene must not be skipped -----
        //
        // A never-saved scene has path == "", so GetSceneByPath("") is invalid
        // and the old code skipped the entry — a user who built a hierarchy in
        // a fresh untitled scene lost it to a single scene_create in Single
        // mode. The fix resolves an empty-path entry by index via GetSceneAt(i)
        // instead. The Check(setup, resolveScene) overload lets the test inject
        // a synthetic resolver so the index-based fallback is covered without a
        // live SceneManager.
        //
        // Scene is a struct with no public setter for isDirty, so a test cannot
        // construct a "dirty valid Scene" to observe a Refuse. Instead we prove
        // the fix by asserting the resolver is *called* for an empty-path entry
        // (the old code skipped it before any lookup): a stub that records the
        // invocation proves the index branch was reached.

        [Test]
        public static void Check_UnsavedScene_EmptyPath_ResolvedByIndex()
        {
            // The empty-path entry must reach the resolver (by index), not be
            // skipped. The stub records the call; if the guard skipped empty-
            // path entries (the A3 bug), the assertion below would fail.
            int calls = 0;
            int seenIndex = -2;
            string seenPath = "untouched";
            var setup = new[]
            {
                new SceneSetup { path = "", isLoaded = true },
            };
            var result = SceneDirtyGuard.Check(setup, (p, i) =>
            {
                calls++;
                seenPath = p;
                seenIndex = i;
                return default; // invalid Scene ⇒ skipped ⇒ Allow (no live scene)
            });
            Assert.AreEqual(1, calls, "Empty-path entry must be resolved, not skipped.");
            Assert.AreEqual("", seenPath, "Empty path must be passed to the resolver.");
            Assert.AreEqual(0, seenIndex, "Unsaved scene must be resolved by its setup index.");
            Assert.IsTrue(result.Allowed, "Invalid resolved Scene ⇒ skipped ⇒ Allow.");
        }

        [Test]
        public static void Check_NamedDirtyScene_ResolvedByPath()
        {
            // Sanity: a non-empty path is resolved by path (the resolver
            // receives the path verbatim). Here it returns an invalid Scene so
            // the entry is skipped and the guard Allows.
            var setup = new[]
            {
                new SceneSetup { path = "Assets/Scenes/Main.unity", isLoaded = true },
            };
            var result = SceneDirtyGuard.Check(setup, (p, i) =>
            {
                Assert.AreEqual("Assets/Scenes/Main.unity", p,
                    "Non-empty path must be passed to the resolver verbatim.");
                return default;
            });
            Assert.IsTrue(result.Allowed);
        }

        // ----- Check(Func): fail-open + observability -----
        //
        // Fail-open policy is intentional (refusing on an API failure would
        // block every disruptive op), but the swallowed exception must be
        // observable so a real failure (e.g. corrupted scene setup) is not
        // silent. The Check(getSetup) overload lets the test inject a throwing
        // provider and assert: (1) Allow is returned and (2) a warning is
        // logged (not swallowed silently).

        [Test]
        public static void Check_ThrowingSetupProvider_AllowsAndLogsWarning()
        {
            // Expect the fail-open warning; LogAssert.Expect prevents the
            // logged warning from failing the test run and asserts it fired.
            LogAssert.Expect(LogType.Warning,
                new Regex(@"SceneDirtyGuard could not read the scene setup.*corrupted"));

            var result = SceneDirtyGuard.Check(() => throw new Exception("corrupted scene setup"));

            Assert.IsTrue(result.Allowed, "Fail-open policy must still return Allow.");
        }

        [Test]
        public static void Check_ThrowingSetupProvider_SwallowsWithoutThrowing()
        {
            // The guard must absorb the exception entirely — no propagation to
            // the caller (BridgeHttpServer preflight). Verify via the overload
            // that no exception escapes, regardless of the log channel.
            LogAssert.Expect(LogType.Warning, new Regex(@"SceneDirtyGuard"));
            Assert.DoesNotThrow(() =>
                SceneDirtyGuard.Check(() => throw new Exception("boom")));
        }

        // ----- GuardResult factories -----

        [Test]
        public static void GuardResult_Allow_HasNoDirtyPaths()
        {
            var r = SceneDirtyGuard.GuardResult.Allow();
            Assert.IsTrue(r.Allowed);
        }

        [Test]
        public static void GuardResult_Refuse_CarriesDirtyPathsAndMessage()
        {
            var r = SceneDirtyGuard.GuardResult.Refuse(
                new[] { "Assets/Scenes/Main.unity" }, "dirty");
            Assert.IsFalse(r.Allowed);
            Assert.AreEqual(new[] { "Assets/Scenes/Main.unity" }, r.DirtyScenePaths);
            Assert.AreEqual("dirty", r.RefusalMessage);
        }

        [Test]
        public static void GuardResult_Refuse_NullPaths_BecomesEmptyArray()
        {
            var r = SceneDirtyGuard.GuardResult.Refuse(null, null);
            Assert.IsFalse(r.Allowed);
            Assert.IsNotNull(r.DirtyScenePaths);
            Assert.AreEqual(0, r.DirtyScenePaths.Length);
            Assert.AreEqual("", r.RefusalMessage ?? "");
        }
    }
}
