using NUnit.Framework;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    public class BuildSettingsToolsTests
    {
        // ----------------------- GetTargets -----------------------------

        [Test]
        public void GetTargets_ReturnsOkEnvelope()
        {
            var result = BuildSettingsTools.GetTargets("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"active\":", result.Output);
            StringAssert.Contains("\"activeGroup\":", result.Output);
            StringAssert.Contains("\"targets\":[", result.Output);
            StringAssert.Contains("\"count\":", result.Output);
            StringAssert.Contains("\"truncated\":", result.Output);
            // Each entry must carry the required keys.
            StringAssert.Contains("\"name\":", result.Output);
            StringAssert.Contains("\"group\":", result.Output);
            StringAssert.Contains("\"installed\":", result.Output);
            StringAssert.Contains("\"isActive\":", result.Output);
        }

        // ----------------------- GetActiveTarget ------------------------

        [Test]
        public void GetActiveTarget_ReturnsOkEnvelope()
        {
            var result = BuildSettingsTools.GetActiveTarget("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"target\":", result.Output);
            StringAssert.Contains("\"group\":", result.Output);
        }

        // ----------------------- GetScenes ------------------------------

        [Test]
        public void GetScenes_ReturnsOkEnvelope()
        {
            var result = BuildSettingsTools.GetScenes("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"count\":", result.Output);
            StringAssert.Contains("\"scenes\":[", result.Output);
        }

        // ----------------------- GetDefines -----------------------------

        [Test]
        public void GetDefines_ReturnsOkEnvelope()
        {
            var result = BuildSettingsTools.GetDefines("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"group\":", result.Output);
            StringAssert.Contains("\"namedTarget\":", result.Output);
            StringAssert.Contains("\"defines\":", result.Output);
            StringAssert.Contains("\"list\":[", result.Output);
        }

        // ----------------------- SettingsGetPlayer ----------------------

        [Test]
        public void SettingsGetPlayer_ReturnsOkEnvelope()
        {
            var result = BuildSettingsTools.SettingsGetPlayer("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"companyName\":", result.Output);
            StringAssert.Contains("\"productName\":", result.Output);
            StringAssert.Contains("\"colorSpace\":", result.Output);
            StringAssert.Contains("\"activeInputHandler\":", result.Output);
        }

        // ----------------------- SettingsGetQuality ---------------------

        [Test]
        public void SettingsGetQuality_ReturnsOkEnvelope()
        {
            var result = BuildSettingsTools.SettingsGetQuality("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"currentLevel\":", result.Output);
            StringAssert.Contains("\"levels\":[", result.Output);
            StringAssert.Contains("\"shadowDistance\":", result.Output);
            StringAssert.Contains("\"antiAliasing\":", result.Output);
        }

        // ----------------------- SettingsGetPhysics ---------------------

        [Test]
        public void SettingsGetPhysics_ReturnsOkEnvelope()
        {
            var result = BuildSettingsTools.SettingsGetPhysics("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"gravity\":[", result.Output);
            StringAssert.Contains("\"simulationMode\":", result.Output);
            StringAssert.Contains("\"physics2DGravity\":[", result.Output);
        }

        // ----------------------- SettingsGetLighting --------------------

        [Test]
        public void SettingsGetLighting_ReturnsOkEnvelope()
        {
            var result = BuildSettingsTools.SettingsGetLighting("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"ambientMode\":", result.Output);
            StringAssert.Contains("\"fog\":", result.Output);
            StringAssert.Contains("\"skybox\":", result.Output);
        }

        // ----------------------- SetTarget ------------------------------

        [Test]
        public void SetTarget_MissingTarget_ReturnsMissingParameter()
        {
            var result = BuildSettingsTools.SetTarget("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'target'", result.ErrorMessage);
        }

        [Test]
        public void SetTarget_UnknownTarget_ReturnsUnknownTarget()
        {
            var result = BuildSettingsTools.SetTarget(
                "{\"target\":\"__Nope_Standalone_42\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("unknown_target", result.ErrorCode);
            StringAssert.Contains("build_get_targets", result.ErrorMessage);
        }

        // ----------------------- SetScenes ------------------------------

        [Test]
        public void SetScenes_MissingScenes_ReturnsMissingParameter()
        {
            var result = BuildSettingsTools.SetScenes("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'scenes'", result.ErrorMessage);
        }

        [Test]
        public void SetScenes_EmptyScenesArray_ReturnsMissingParameter()
        {
            var result = BuildSettingsTools.SetScenes("{\"scenes\":[]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        // ----------------------- SetDefines -----------------------------

        [Test]
        public void SetDefines_MissingDefines_ReturnsMissingParameter()
        {
            var result = BuildSettingsTools.SetDefines("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'defines'", result.ErrorMessage);
        }

        // ----------------------- StartBuild (deny) ----------------------

        [Test]
        public void StartBuild_WithoutBypass_ReturnsBuildConfirmationRequired()
        {
            // The default deny contract: BuildPipeline.BuildPlayer is blocked
            // unless gate: "off" AND confirm_bypass: true are both set.
            var result = BuildSettingsTools.StartBuild(
                "{\"output_path\":\"Builds/Test\",\"paths_hint\":[\"Builds/Test\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("build_confirmation_required", result.ErrorCode);
            StringAssert.Contains("BuildPipeline.BuildPlayer", result.ErrorMessage);
            StringAssert.Contains("confirm_bypass", result.ErrorMessage);
            StringAssert.Contains("gate", result.ErrorMessage);
        }

        [Test]
        public void StartBuild_PartialBypass_OnlyConfirmFlag_StillsRefused()
        {
            // confirm_bypass alone is NOT enough — gate must be off too.
            var result = BuildSettingsTools.StartBuild(
                "{\"confirm_bypass\":true,\"paths_hint\":[\"Builds/Test\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("build_confirmation_required", result.ErrorCode);
        }

        [Test]
        public void StartBuild_PartialBypass_OnlyGateOff_StillsRefused()
        {
            // gate off alone is NOT enough — confirm_bypass must also be true.
            var result = BuildSettingsTools.StartBuild(
                "{\"gate\":\"off\",\"paths_hint\":[\"Builds/Test\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("build_confirmation_required", result.ErrorCode);
        }

        // ----------------------- SettingsSet* --------------------------

        [Test]
        public void SettingsSetPlayer_MissingFields_ReturnsMissingParameter()
        {
            var result = BuildSettingsTools.SettingsSetPlayer("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'fields'", result.ErrorMessage);
        }

        [Test]
        public void SettingsSetPlayer_EmptyFieldsArray_ReturnsMissingParameter()
        {
            var result = BuildSettingsTools.SettingsSetPlayer("{\"fields\":[]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void SettingsSetPlayer_NoApplicableKeys_ReturnsNoApplicableKeys()
        {
            // An unknown key + a missing-key entry → nothing applied.
            var result = BuildSettingsTools.SettingsSetPlayer(
                "{\"fields\":[{\"key\":\"__unknown_key\",\"value\":1},{\"value\":2}]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("no_applicable_keys", result.ErrorCode);
        }

        [Test]
        public void SettingsSetQuality_MissingFields_ReturnsMissingParameter()
        {
            var result = BuildSettingsTools.SettingsSetQuality("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void SettingsSetPhysics_MissingFields_ReturnsMissingParameter()
        {
            var result = BuildSettingsTools.SettingsSetPhysics("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void SettingsSetLighting_MissingFields_ReturnsMissingParameter()
        {
            var result = BuildSettingsTools.SettingsSetLighting("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        // ----------------------- Dispatch wiring ------------------------
        //
        // KnownTools / DirectResponseTools / MutatingTools membership is the
        // contract that lets the dispatcher route a build/settings tool. We
        // assert the lifecycle + dirty-guard contracts so a future edit that
        // forgets to wire a new Plan 9 tool fails loudly here. These mirror
        // the PackagesToolsTests lifecycle / dirty-guard assertions.

        [Test]
        public void Lifecycle_MutatorsResolveToExpectedPolicies()
        {
            // build_set_target + build_set_defines + settings_set_player can
            // force a recompile / domain reload → RestartThenSettle (dirty
            // guard preflights them).
            Assert.AreEqual(LifecyclePolicy.RestartThenSettle,
                ToolLifecycle.Resolve("unity_open_mcp_build_set_target"));
            Assert.AreEqual(LifecyclePolicy.RestartThenSettle,
                ToolLifecycle.Resolve("unity_open_mcp_build_set_defines"));
            Assert.AreEqual(LifecyclePolicy.RestartThenSettle,
                ToolLifecycle.Resolve("unity_open_mcp_settings_set_player"));
            // The other Plan 9 mutators touch ProjectSettings assets without
            // recompiling (or run an in-process player build) → EditorSettle.
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_build_set_scenes"));
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_build_start"));
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_settings_set_quality"));
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_settings_set_physics"));
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_settings_set_lighting"));
            // Read-only Plan 9 tools default to None (safe / no settle).
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_build_get_targets"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_build_get_active_target"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_build_get_scenes"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_build_get_defines"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_settings_get_player"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_settings_get_quality"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_settings_get_physics"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_settings_get_lighting"));
        }

        [Test]
        public void DirtyGuard_PreflightsRecompileMutators()
        {
            // build_set_target / build_set_defines / settings_set_player can
            // domain-reload, so they get the dirty guard.
            Assert.IsTrue(SceneDirtyGuard.AppliesTo("unity_open_mcp_build_set_target", "{}"),
                "build_set_target must be guarded (RestartThenSettle lifecycle)");
            Assert.IsTrue(SceneDirtyGuard.AppliesTo("unity_open_mcp_build_set_defines", "{}"),
                "build_set_defines must be guarded (RestartThenSettle lifecycle)");
            Assert.IsTrue(SceneDirtyGuard.AppliesTo("unity_open_mcp_settings_set_player", "{}"),
                "settings_set_player must be guarded (RestartThenSettle lifecycle)");
            // Read-only Plan 9 tools are never guarded.
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_build_get_targets", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_build_get_defines", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_settings_get_player", "{}"));
        }

        [Test]
        public void DirtyGuard_RecompileMutators_IgnoreSceneDirtyOptOut()
        {
            Assert.IsFalse(SceneDirtyGuard.AppliesTo(
                "unity_open_mcp_build_set_target", "{\"ignore_scene_dirty\":true}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo(
                "unity_open_mcp_build_set_defines", "{\"ignore_scene_dirty\":true}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo(
                "unity_open_mcp_settings_set_player", "{\"ignore_scene_dirty\":true}"));
        }

        // ----------------------- T20.9.3 settings remainder ----------------

        [Test]
        public void SettingsGetTime_ReturnsOkEnvelope()
        {
            var result = BuildSettingsTools.SettingsGetTime("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            // Runtime Time reads are always present.
            StringAssert.Contains("\"timeScale\":", result.Output);
            StringAssert.Contains("\"fixedDeltaTime\":", result.Output);
            StringAssert.Contains("\"maximumDeltaTime\":", result.Output);
            StringAssert.Contains("\"captureFramerate\":", result.Output);
        }

        [Test]
        public void SettingsSetTime_MissingFields_ReturnsMissingParameter()
        {
            var result = BuildSettingsTools.SettingsSetTime("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void SettingsSetTime_PatchesTimeScale_AndRoundTrips()
        {
            // Capture the original value so we can restore it.
            float original = UnityEngine.Time.timeScale;
            try
            {
                var setResult = BuildSettingsTools.SettingsSetTime(
                    "{\"fields\":[{\"key\":\"timeScale\",\"value\":0.5}]}");
                Assert.IsTrue(setResult.Success, setResult.ErrorMessage);
                StringAssert.Contains("\"status\":\"ok\"", setResult.Output);
                StringAssert.Contains("\"action\":\"set_time\"", setResult.Output);
                StringAssert.Contains("\"applied\":[\"timeScale\"]", setResult.Output);

                // Round-trip: get reports the new on-disk setting.
                var getResult = BuildSettingsTools.SettingsGetTime("{}");
                Assert.IsTrue(getResult.Success, getResult.ErrorMessage);
                StringAssert.Contains("\"timeScaleSetting\":0.5", getResult.Output);
            }
            finally
            {
                // Restore the original value so the test leaves no trace.
                BuildSettingsTools.SettingsSetTime(
                    $"{{\"fields\":[{{\"key\":\"timeScale\",\"value\":{original}}}]}}");
            }
        }

        [Test]
        public void SettingsSetTime_UnknownKey_ReportsNoApplicableKeys()
        {
            var result = BuildSettingsTools.SettingsSetTime(
                "{\"fields\":[{\"key\":\"__not_a_time_key\",\"value\":1}]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("no_applicable_keys", result.ErrorCode);
        }

        [Test]
        public void SettingsGetRenderPipeline_ReportsPipeline_Readonly()
        {
            var result = BuildSettingsTools.SettingsGetRenderPipeline("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"pipeline\":", result.Output);
            // The read-only contract: hasSetter is always false.
            StringAssert.Contains("\"hasSetter\":false", result.Output);
            // The note explains why there is no setter.
            StringAssert.Contains("\"note\":", result.Output);
        }

        [Test]
        public void SettingsSetQualityLevel_MissingLevel_ReturnsMissingParameter()
        {
            var result = BuildSettingsTools.SettingsSetQualityLevel("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void SettingsSetQualityLevel_ByIndex_SwitchesActiveLevel()
        {
            // Capture the original level so we can restore it.
            int original = UnityEngine.QualitySettings.GetQualityLevel();
            try
            {
                // Pick a target index different from the current one (clamp to
                // the available level count).
                var names = UnityEngine.QualitySettings.names;
                int target = names.Length > 1 && original != 0 ? 0 : (names.Length > 1 ? 1 : 0);

                var setResult = BuildSettingsTools.SettingsSetQualityLevel(
                    $"{{\"quality_level\":{target}}}");
                Assert.IsTrue(setResult.Success, setResult.ErrorMessage);
                StringAssert.Contains("\"status\":\"ok\"", setResult.Output);
                StringAssert.Contains("\"action\":\"set_quality_level\"", setResult.Output);
                StringAssert.Contains($"\"requestedLevel\":{target}", setResult.Output);

                var after = UnityEngine.QualitySettings.GetQualityLevel();
                Assert.AreEqual(target, after);
            }
            finally
            {
                UnityEngine.QualitySettings.SetQualityLevel(original);
            }
        }

        [Test]
        public void SettingsSetQualityLevel_InvalidName_ReturnsInvalidQualityLevel()
        {
            var result = BuildSettingsTools.SettingsSetQualityLevel(
                "{\"quality_level\":\"__no_such_level__\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_quality_level", result.ErrorCode);
        }

        // ----------------------- T20.9.3 dispatch wiring -------------------

        [Test]
        public void KnownTools_ContainsSettingsRemainderTools()
        {
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_settings_get_time"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_settings_set_time"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_settings_get_render_pipeline"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_settings_set_quality_level"));
        }

        [Test]
        public void DirectResponseTools_ContainsSettingsRemainderReads()
        {
            Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_settings_get_time"));
            Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_settings_get_render_pipeline"));
        }

        [Test]
        public void MutatingTools_ContainsSettingsRemainderMutators()
        {
            Assert.IsTrue(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_settings_set_time"));
            Assert.IsTrue(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_settings_set_quality_level"));
        }

        [Test]
        public void Lifecycle_SettingsRemainderMutatorsResolveToEditorSettle()
        {
            // set_time + set_quality_level write ProjectSettings/*.asset without
            // recompiling → EditorSettle. Reads default to None.
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_settings_set_time"));
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_settings_set_quality_level"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_settings_get_time"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_settings_get_render_pipeline"));
        }

        [Test]
        public void DirtyGuard_NeverPreflightsSettingsRemainderTools()
        {
            // EditorSettle (not RestartThenSettle) → no dirty guard preflight.
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_settings_set_time", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_settings_set_quality_level", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_settings_get_time", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_settings_get_render_pipeline", "{}"));
        }
    }
}
