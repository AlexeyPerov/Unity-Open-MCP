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
    // fully unit-testable. Check() touches EditorSceneManager (main-thread
    // Editor API); in a fresh EditMode session GetSceneManagerSetup() returns
    // null/empty, so Check() returns Allow — we assert that contract rather
    // than synthesizing dirty scenes (which would need a loaded test scene).
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

        // ----- Check: null/empty scene setup => Allow -----

        [Test]
        public static void Check_NoSceneSetup_Allows()
        {
            // Fresh EditMode session: GetSceneManagerSetup() returns null when
            // no scene is loaded. The guard must allow rather than block every
            // disruptive op in setups it can't introspect.
            var result = SceneDirtyGuard.Check();
            Assert.IsTrue(result.Allowed);
            Assert.IsNull(result.DirtyScenePaths);
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
