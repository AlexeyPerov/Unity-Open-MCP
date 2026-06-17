using NUnit.Framework;
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
        [TestCase("unity_agent_run_tests", ExpectedResult = false)]
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
