// EditMode tests for the M16 Plan 7 typed profiler session / diagnostics
// tools (ProfilerSessionTools).
//
// Strategy: live ProfilerDriver frame data is non-deterministic in CI (no
// guarantees the Editor has captured frames), so these tests cover the
// parameter-parsing, resolver, and gate-wiring contracts that are stable:
//
// - missing/empty parameter validation for every tool that takes arguments
// - the module-bookkeeping surface (list_modules / enable_module round trip)
// - the runtime-status + script-stats reads (single-frame snapshots that
//   always work in EditMode)
// - the snapshot path resolver (save_data / load_data) refusal cases
// - the dispatch wiring contracts (KnownTools / DirectResponseTools /
//   MutatingTools membership + the lifecycle policy for the lone mutator)
//
// Live round-trips (save → load → assert content equality, set_config deep
// profile, etc.) need a real running Editor and are covered by the manual
// validation checklist, not here.
using NUnit.Framework;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    public class ProfilerSessionToolsTests
    {
        // ----------------------- Start ----------------------------------

        [Test]
        public void Start_OpenWindowFalse_DoesNotError()
        {
            // open_window:false skips the menu call but still flips the
            // enabled flag. EditMode-safe (no window is actually opened).
            var result = ProfilerSessionTools.Start("{\"open_window\":false}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"enabled\":", result.Output);
            StringAssert.Contains("\"windowOpened\":false", result.Output);
        }

        // ----------------------- Stop -----------------------------------

        [Test]
        public void Stop_DisablesProfiler_ReturnsOk()
        {
            var result = ProfilerSessionTools.Stop("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"enabled\":false", result.Output);
        }

        // ----------------------- Get status -----------------------------

        [Test]
        public void GetStatus_ReturnsRuntimeSurface()
        {
            var result = ProfilerSessionTools.GetStatus("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"enabled\":", result.Output);
            StringAssert.Contains("\"supported\":", result.Output);
            StringAssert.Contains("\"maxUsedMemoryBytes\":", result.Output);
            StringAssert.Contains("\"maxUsedMemoryMB\":", result.Output);
            StringAssert.Contains("\"activeModules\":[", result.Output);
        }

        // ----------------------- Get config -----------------------------

        [Test]
        public void GetConfig_ReturnsConfigKnobs()
        {
            var result = ProfilerSessionTools.GetConfig("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"driverEnabled\":", result.Output);
            StringAssert.Contains("\"profileEditor\":", result.Output);
            StringAssert.Contains("\"deepProfile\":", result.Output);
            StringAssert.Contains("\"allocationCallstacks\":", result.Output);
            StringAssert.Contains("\"binaryLog\":", result.Output);
            StringAssert.Contains("\"availableCategories\":[", result.Output);
            StringAssert.Contains("\"enabledCategories\":[", result.Output);
            StringAssert.Contains("\"warnings\":[", result.Output);
        }

        // ----------------------- Set config -----------------------------

        [Test]
        public void SetConfig_NoFields_ReturnsCurrentConfig()
        {
            // No requested fields — should return the current config and an
            // empty warnings list.
            var result = ProfilerSessionTools.SetConfig("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"driverEnabled\":", result.Output);
        }

        [Test]
        public void SetConfig_UnknownCategory_ReportsWarning()
        {
            // Unknown category name → surfaces in warnings but does not fail.
            var result = ProfilerSessionTools.SetConfig(
                "{\"enable_categories\":[\"__no_such_category_xyz\"]}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"warnings\":[", result.Output);
            StringAssert.Contains("Unknown profiler category", result.Output);
        }

        // ----------------------- List modules ---------------------------

        [Test]
        public void ListModules_ReturnsCanonicalModuleSet()
        {
            var result = ProfilerSessionTools.ListModules("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"modules\":[", result.Output);
            StringAssert.Contains("\"name\":\"CPU\"", result.Output);
            StringAssert.Contains("\"name\":\"Memory\"", result.Output);
            StringAssert.Contains("\"name\":\"UI\"", result.Output);
            StringAssert.Contains("\"name\":\"VirtualTexturing\"", result.Output);
            StringAssert.Contains("\"count\":14", result.Output);
        }

        // ----------------------- Enable module --------------------------

        [Test]
        public void EnableModule_MissingModule_ReturnsMissingParameter()
        {
            var result = ProfilerSessionTools.EnableModule("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'module'", result.ErrorMessage);
        }

        [Test]
        public void EnableModule_UnknownModule_ReturnsUnknownModule()
        {
            var result = ProfilerSessionTools.EnableModule(
                "{\"module\":\"__no_such_module\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("unknown_module", result.ErrorCode);
            StringAssert.Contains("Unknown profiler module", result.ErrorMessage);
            StringAssert.Contains("Available modules:", result.ErrorMessage);
        }

        [Test]
        public void EnableModule_ToggleRoundTrip_ListReflectsChange()
        {
            // "NetworkMessages" is in the canonical list but not in the
            // default-enabled set — flipping it on should be observable via
            // list_modules / get_status.
            var enable = ProfilerSessionTools.EnableModule(
                "{\"module\":\"NetworkMessages\",\"enabled\":true}");
            Assert.IsTrue(enable.Success, enable.ErrorMessage);
            StringAssert.Contains("\"enabled\":true", enable.Output);

            var status = ProfilerSessionTools.GetStatus("{}");
            Assert.IsTrue(status.Success, status.ErrorMessage);
            StringAssert.Contains("\"NetworkMessages\"", status.Output);

            // Revert to keep the test suite state-clean.
            var disable = ProfilerSessionTools.EnableModule(
                "{\"module\":\"NetworkMessages\",\"enabled\":false}");
            Assert.IsTrue(disable.Success, disable.ErrorMessage);

            var after = ProfilerSessionTools.GetStatus("{}");
            Assert.IsFalse(after.Output.Contains("\"NetworkMessages\""),
                "NetworkMessages should be off after disabling: " + after.Output);
        }

        // ----------------------- Clear data -----------------------------

        [Test]
        public void ClearData_DoesNotErrorInEditMode()
        {
            // ClearAllFrames is editor-version gated but should succeed in
            // any modern Unity EditMode session. Either an ok envelope or a
            // clear_unavailable failure on an old Unity — both are valid; the
            // call itself must not throw.
            var result = ProfilerSessionTools.ClearData("{}");
            if (result.Success)
            {
                StringAssert.Contains("\"status\":\"ok\"", result.Output);
                StringAssert.Contains("\"cleared\":true", result.Output);
            }
            else
            {
                Assert.AreEqual("clear_unavailable", result.ErrorCode);
            }
        }

        // ----------------------- Save data ------------------------------

        [Test]
        public void SaveData_MissingPath_ReturnsMissingParameter()
        {
            var result = ProfilerSessionTools.SaveData("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'file_path'", result.ErrorMessage);
        }

        [Test]
        public void SaveData_BadExtension_ReturnsInvalidPath()
        {
            var result = ProfilerSessionTools.SaveData(
                "{\"file_path\":\"Assets/Foo.txt\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_path", result.ErrorCode);
            StringAssert.Contains(".json", result.ErrorMessage);
        }

        [Test]
        public void SaveData_PathTraversal_ReturnsInvalidPath()
        {
            var result = ProfilerSessionTools.SaveData(
                "{\"file_path\":\"../escape.json\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_path", result.ErrorCode);
            StringAssert.Contains("'..'", result.ErrorMessage);
        }

        // ----------------------- Load data ------------------------------

        [Test]
        public void LoadData_MissingPath_ReturnsMissingParameter()
        {
            var result = ProfilerSessionTools.LoadData("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'file_path'", result.ErrorMessage);
        }

        [Test]
        public void LoadData_BadExtension_ReturnsInvalidPath()
        {
            var result = ProfilerSessionTools.LoadData(
                "{\"file_path\":\"Assets/Foo.raw\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_path", result.ErrorCode);
        }

        [Test]
        public void LoadData_NonExistentFile_ReturnsFileNotFound()
        {
            var result = ProfilerSessionTools.LoadData(
                "{\"file_path\":\"Assets/__MCPTest_NoSuchSnapshot.json\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("file_not_found", result.ErrorCode);
        }

        // ----------------------- Get script stats -----------------------

        [Test]
        public void GetScriptStats_ReturnsSingleFrameSnapshot()
        {
            var result = ProfilerSessionTools.GetScriptStats("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"frameTimeMs\":", result.Output);
            StringAssert.Contains("\"fixedDeltaTimeMs\":", result.Output);
            StringAssert.Contains("\"timeScale\":", result.Output);
            StringAssert.Contains("\"totalFrameCount\":", result.Output);
            StringAssert.Contains("\"realtimeSinceStartup\":", result.Output);
            StringAssert.Contains("\"monoMemoryUsageBytes\":", result.Output);
            StringAssert.Contains("\"gcMemoryUsageBytes\":", result.Output);
        }

        // ----------------------- Dispatch wiring ------------------------
        //
        // KnownTools / DirectResponseTools / MutatingTools membership is
        // the contract that lets the dispatcher route a profiler tool. We
        // assert the lifecycle policy for the lone mutator so a future edit
        // that forgets to wire a new profiler tool fails loudly here.

        [Test]
        public void Lifecycle_SaveDataIsEditorSettle_ReadsAreNone()
        {
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_profiler_save_data"));
            // All other Plan 7 tools mutate editor state / bookkeeping but
            // write no assets (gate-free direct-response) — None lifecycle.
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_profiler_start"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_profiler_stop"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_profiler_get_status"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_profiler_get_config"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_profiler_set_config"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_profiler_list_modules"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_profiler_enable_module"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_profiler_clear_data"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_profiler_load_data"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_profiler_get_script_stats"));
        }

        [Test]
        public void DirtyGuard_DoesNotPreflight_ProfilerTools()
        {
            // save_data writes a .json file (no domain reload / scene switch),
            // so the dirty guard does not preflight it. The others are read-
            // only / direct-response and never guarded either.
            Assert.IsFalse(SceneDirtyGuard.AppliesTo(
                "unity_open_mcp_profiler_save_data", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo(
                "unity_open_mcp_profiler_start", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo(
                "unity_open_mcp_profiler_set_config", "{}"));
        }
    }
}
