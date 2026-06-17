using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M13 T4.1 — lifecycle policy classification.
    //
    // Pins the centralized ToolLifecycle.Map so a tool's settle/retry behaviour
    // can't silently drift when a new tool is added or a registry attribute is
    // edited. The map is the single source of truth consumed by the dispatcher
    // (settle-wait) and the dirty guard (preflight).
    public static class ToolLifecycleTests
    {
        // ----- None: read-only, no settle -----

        [TestCase("unity_open_mcp_find_members", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("unity_open_mcp_validate_edit", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("unity_open_mcp_checkpoint_create", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("unity_open_mcp_delta", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("unity_open_mcp_find_references", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("unity_open_mcp_scan_paths", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("unity_open_mcp_read_asset", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("unity_open_mcp_search_assets", ExpectedResult = LifecyclePolicy.None)]
        public static LifecyclePolicy Resolve_ReadOnlyHardcodedTools_None(string tool)
        {
            return ToolLifecycle.Resolve(tool);
        }

        // Registry-discovered read-only tools carry their policy on the attribute;
        // Resolve() must surface it, not fall through to the default.
        [TestCase("unity_open_mcp_editor_status", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("unity_agent_screenshot", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("unity_agent_read_console", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("unity_agent_profiler_capture", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("unity_agent_profiler_memory", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("unity_agent_profiler_rendering", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("unity_agent_spatial_query", ExpectedResult = LifecyclePolicy.None)]
        public static LifecyclePolicy Resolve_ReadOnlyRegistryTools_None(string tool)
        {
            return ToolLifecycle.Resolve(tool);
        }

        // ----- EditorSettle: mutating, asset-refresh settle -----

        [TestCase("unity_open_mcp_apply_fix", ExpectedResult = LifecyclePolicy.EditorSettle)]
        [TestCase("unity_open_mcp_reserialize", ExpectedResult = LifecyclePolicy.EditorSettle)]
        public static LifecyclePolicy Resolve_AssetMutationTools_EditorSettle(string tool)
        {
            return ToolLifecycle.Resolve(tool);
        }

        // ----- RestartThenSettle: may trigger a domain reload -----

        [TestCase("unity_open_mcp_execute_csharp", ExpectedResult = LifecyclePolicy.RestartThenSettle)]
        [TestCase("unity_open_mcp_invoke_method", ExpectedResult = LifecyclePolicy.RestartThenSettle)]
        [TestCase("unity_open_mcp_execute_menu", ExpectedResult = LifecyclePolicy.RestartThenSettle)]
        public static LifecyclePolicy Resolve_DisruptiveTools_RestartThenSettle(string tool)
        {
            return ToolLifecycle.Resolve(tool);
        }

        // ----- CustomConfirmation: external completion signal -----

        [Test]
        public static void Resolve_RunTests_CustomConfirmation()
        {
            Assert.AreEqual(LifecyclePolicy.CustomConfirmation,
                ToolLifecycle.Resolve("unity_agent_run_tests"));
        }

        // ----- defaults -----

        [Test]
        public static void Resolve_UnknownTool_DefaultsToNone()
        {
            // A tool added without an explicit policy must never silently
            // trigger a settle-wait or a dirty-scene refusal. None is the
            // safe read-only default.
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_brand_new_unclassified"));
        }

        [Test]
        public static void Resolve_NullOrEmpty_DefaultsToNone()
        {
            Assert.AreEqual(LifecyclePolicy.None, ToolLifecycle.Resolve(null));
            Assert.AreEqual(LifecyclePolicy.None, ToolLifecycle.Resolve(""));
        }

        // ----- RequiresSettleWait -----

        [Test]
        public static void RequiresSettleWait_TrueForEditorAndRestart()
        {
            Assert.IsTrue(ToolLifecycle.RequiresSettleWait(LifecyclePolicy.EditorSettle));
            Assert.IsTrue(ToolLifecycle.RequiresSettleWait(LifecyclePolicy.RestartThenSettle));
        }

        [Test]
        public static void RequiresSettleWait_FalseForNoneAndCustom()
        {
            Assert.IsFalse(ToolLifecycle.RequiresSettleWait(LifecyclePolicy.None));
            Assert.IsFalse(ToolLifecycle.RequiresSettleWait(LifecyclePolicy.CustomConfirmation));
        }

        // ----- RequiresDirtyGuard -----

        [Test]
        public static void RequiresDirtyGuard_OnlyRestartThenSettle()
        {
            // Only ops that can disrupt the editor are guarded — apply_fix /
            // reserialize never trigger the native save modal, so guarding them
            // would be pure friction.
            Assert.IsTrue(ToolLifecycle.RequiresDirtyGuard("unity_open_mcp_execute_csharp"));
            Assert.IsTrue(ToolLifecycle.RequiresDirtyGuard("unity_open_mcp_invoke_method"));
            Assert.IsTrue(ToolLifecycle.RequiresDirtyGuard("unity_open_mcp_execute_menu"));

            Assert.IsFalse(ToolLifecycle.RequiresDirtyGuard("unity_open_mcp_apply_fix"));
            Assert.IsFalse(ToolLifecycle.RequiresDirtyGuard("unity_open_mcp_reserialize"));
            Assert.IsFalse(ToolLifecycle.RequiresDirtyGuard("unity_open_mcp_find_members"));
            Assert.IsFalse(ToolLifecycle.RequiresDirtyGuard("unity_agent_run_tests"));
        }

        // ----- wire string round-trip -----

        [TestCase(LifecyclePolicy.None, ExpectedResult = "none")]
        [TestCase(LifecyclePolicy.EditorSettle, ExpectedResult = "editor_settle")]
        [TestCase(LifecyclePolicy.RestartThenSettle, ExpectedResult = "restart_then_settle")]
        [TestCase(LifecyclePolicy.CustomConfirmation, ExpectedResult = "custom_confirmation")]
        public static string ToWireString_MatchesSnakeCaseContract(LifecyclePolicy policy)
        {
            return policy.ToWireString();
        }

        [Test]
        public static void ParseWireString_RoundTripsToWireString()
        {
            foreach (LifecyclePolicy policy in System.Enum.GetValues(typeof(LifecyclePolicy)))
            {
                Assert.AreEqual(policy,
                    LifecyclePolicyExtensions.ParseWireString(policy.ToWireString()));
            }
        }

        [TestCase("unknown", ExpectedResult = LifecyclePolicy.None)]
        [TestCase("", ExpectedResult = LifecyclePolicy.None)]
        public static LifecyclePolicy ParseWireString_Unknown_DefaultsToNone(string value)
        {
            return LifecyclePolicyExtensions.ParseWireString(value);
        }
    }
}
