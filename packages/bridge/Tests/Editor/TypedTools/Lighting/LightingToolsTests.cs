// M20 Plan 2 — Lighting embedded domain tools EditMode tests.
//
// Ungated (no UNITY_OPEN_MCP_EXT_LIGHTING): the Light / ReflectionProbe /
// RenderSettings / Lightmapping types are built-in engine modules present on
// every Unity install, so the tools — and this suite — compile unconditionally.
// The test asmdef only constrains UNITY_TEST_FRAMEWORK.
#pragma warning disable CS0618
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Tests.Extensions.LightingExt
{
    public class LightingToolsTests
    {
        // The 7 catalog tool ids this domain must register.
        private static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_light_add",
            "unity_open_mcp_light_set",
            "unity_open_mcp_light_modify",
            "unity_open_mcp_reflection_probe_bake",
            "unity_open_mcp_reflection_probe_get",
            "unity_open_mcp_skybox_set",
            "unity_open_mcp_skybox_get",
        };

        [Test]
        public void Registry_AllSevenToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected lighting tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_ReadToolsAreReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_reflection_probe_get", out var probeGet));
            Assert.IsFalse(probeGet.IsMutating);
            Assert.IsTrue(probeGet.ReadOnlyHint);

            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_skybox_get", out var skyGet));
            Assert.IsFalse(skyGet.IsMutating);
            Assert.IsTrue(skyGet.ReadOnlyHint);
        }

        [Test]
        public void Registry_MutatingToolsAreMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_light_add", out var add));
            Assert.IsTrue(add.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, add.Lifecycle);

            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_light_set", out var set));
            Assert.IsTrue(set.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, set.Lifecycle);

            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_light_modify", out var mod));
            Assert.IsTrue(mod.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, mod.Lifecycle);

            // The bake tool is a long mutation — it MUST be EditorSettle so the
            // dispatcher waits for the bake.
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_reflection_probe_bake", out var bake));
            Assert.IsTrue(bake.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, bake.Lifecycle);

            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_skybox_set", out var skySet));
            Assert.IsTrue(skySet.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, skySet.Lifecycle);
        }

        [Test]
        public void Registry_AllToolsAssignedToLightingGroup()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var info));
                Assert.AreEqual("lighting", info.Group,
                    $"Expected '{id}' to be in the 'lighting' group.");
            }
        }

        // -----------------------------------------------------------------
        // paths_hint contract — every mutating tool refuses empty scope.
        //
        // Two layers enforce this: the bridge HTTP server short-circuits
        // mutating calls with an empty paths_hint BEFORE invoking the tool
        // (returning a `paths_hint_required` envelope with Success=false), and
        // the tool method itself returns the same envelope defensively. We
        // assert on the output envelope (unambiguous) AND tolerate either
        // dispatcher outcome (Success=false + ErrorCode, or Success=true with
        // the error envelope in Output) so the test is correct regardless of
        // which layer the agent-side test runner exercises.
        // -----------------------------------------------------------------

        private static void AssertErrorEnvelope(ToolDispatchResult result, string expectedCode)
        {
            Assert.IsNotNull(result);
            bool sawEnvelope = (result.Output ?? "").Contains("\"code\":\"" + expectedCode + "\"");
            bool sawFail = !result.Success && result.ErrorCode == expectedCode;
            Assert.IsTrue(sawEnvelope || sawFail,
                $"Expected '{expectedCode}' envelope. Got Success={result.Success}, " +
                $"ErrorCode={result.ErrorCode}, Output={result.Output}");
        }

        [Test]
        public void Dispatch_LightAdd_MissingPathsHint_ReturnsError()
        {
            var go = new GameObject("LightAddNoHint");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_light_add",
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
                AssertErrorEnvelope(result, "paths_hint_required");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_LightSet_MissingPathsHint_ReturnsError()
        {
            var go = new GameObject("LightSetNoHint");
            try
            {
                go.AddComponent<Light>();
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_light_set",
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
                AssertErrorEnvelope(result, "paths_hint_required");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_LightModify_MissingPathsHint_ReturnsError()
        {
            var go = new GameObject("LightModifyNoHint");
            try
            {
                go.AddComponent<Light>();
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_light_modify",
                    "{\"instance_id\":" +InstanceId.Of(go) + ",\"fields_json\":\"[]\"}");
                AssertErrorEnvelope(result, "paths_hint_required");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_ReflectionProbeBake_MissingPathsHint_ReturnsError()
        {
            var go = new GameObject("ProbeBakeNoHint");
            try
            {
                go.AddComponent<ReflectionProbe>();
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_reflection_probe_bake",
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
                AssertErrorEnvelope(result, "paths_hint_required");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_SkyboxSet_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_skybox_set",
                "{\"material_path\":\"Assets/Tmp.mat\"}");
            AssertErrorEnvelope(result, "paths_hint_required");
        }

        // -----------------------------------------------------------------
        // Target resolution branches.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_LightAdd_OnUnknownTarget_ReturnsTargetNotFound()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_light_add",
                "{\"name\":\"__nonexistent_light_target__\",\"paths_hint\":[\"Assets/T.unity\"]}");
            AssertErrorEnvelope(result, "target_not_found");
        }

        [Test]
        public void Dispatch_LightSet_OnTargetWithoutLight_ReturnsComponentNotFound()
        {
            var go = new GameObject("LightSetNoComp");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_light_set",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"intensity\":2.0,\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "component_not_found");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_ReflectionProbeGet_OnTargetWithoutProbe_ReturnsComponentNotFound()
        {
            var go = new GameObject("ProbeGetNoComp");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_reflection_probe_get",
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
                AssertErrorEnvelope(result, "component_not_found");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Light round-trip: add Directional → set color + intensity → verify.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_LightAdd_DirectionalThenSetColorAndIntensity()
        {
            var go = new GameObject("LightRoundTrip");
            try
            {
                var addBody = "{\"instance_id\":" +InstanceId.Of(go) +
                              ",\"light_type\":\"Directional\",\"intensity\":1.0," +
                              "\"paths_hint\":[\"Assets/T.unity\"]}";
                var add = BridgeToolRegistry.TryDispatch("unity_open_mcp_light_add", addBody);
                Assert.IsTrue(add.Success, add.ErrorMessage ?? add.Output);
                StringAssert.Contains("\"added\":true", add.Output);
                StringAssert.Contains("\"type\":\"Directional\"", add.Output);

                var light = go.GetComponent<Light>();
                Assert.IsNotNull(light);
                Assert.AreEqual(LightType.Directional, light.type);

                // Set color + intensity via the typed mutator.
                var setBody = "{\"instance_id\":" +InstanceId.Of(go) +
                              ",\"color\":[1,0,0,1],\"intensity\":3.5," +
                              "\"paths_hint\":[\"Assets/T.unity\"]}";
                var set = BridgeToolRegistry.TryDispatch("unity_open_mcp_light_set", setBody);
                Assert.IsTrue(set.Success, set.ErrorMessage ?? set.Output);
                StringAssert.Contains("\"intensity\":3.5", set.Output);

                Assert.AreEqual(new Color(1f, 0f, 0f, 1f), light.color);
                Assert.AreEqual(3.5f, light.intensity);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RoundTrip_LightAdd_Spot_SetsSpotAngle()
        {
            var go = new GameObject("LightSpot");
            try
            {
                var addBody = "{\"instance_id\":" +InstanceId.Of(go) +
                              ",\"light_type\":\"Spot\",\"spot_angle\":45.0," +
                              "\"paths_hint\":[\"Assets/T.unity\"]}";
                var add = BridgeToolRegistry.TryDispatch("unity_open_mcp_light_add", addBody);
                Assert.IsTrue(add.Success, add.ErrorMessage ?? add.Output);
                StringAssert.Contains("\"type\":\"Spot\"", add.Output);
                StringAssert.Contains("\"spotAngle\":45", add.Output);

                var light = go.GetComponent<Light>();
                Assert.AreEqual(LightType.Spot, light.type);
                Assert.AreEqual(45f, light.spotAngle);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RoundTrip_LightAdd_Idempotent_ReusingReportsAddedFalse()
        {
            var go = new GameObject("LightIdem");
            try
            {
                go.AddComponent<Light>();
                var body = "{\"instance_id\":" +InstanceId.Of(go) +
                           ",\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch("unity_open_mcp_light_add", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"added\":false", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Light_modify reflective field patch.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_LightModify_IntensityAndType_AppliesAndReports()
        {
            var go = new GameObject("LightModifyHost");
            try
            {
                var light = go.AddComponent<Light>();
                var fieldsJson = "[{\"field\":\"intensity\",\"value\":4.5,\"type\":\"float\"}," +
                                 "{\"field\":\"type\",\"value\":\"Point\",\"type\":\"string\"}]";
                var body = "{\"instance_id\":" +InstanceId.Of(go) +
                           ",\"fields_json\":\"" + JsonEscape(fieldsJson) + "\"," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var mod = BridgeToolRegistry.TryDispatch("unity_open_mcp_light_modify", body);
                Assert.IsTrue(mod.Success, mod.ErrorMessage ?? mod.Output);
                StringAssert.Contains("\"field\":\"intensity\"", mod.Output);
                StringAssert.Contains("\"field\":\"type\"", mod.Output);
                Assert.AreEqual(4.5f, light.intensity);
                Assert.AreEqual(LightType.Point, light.type);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_LightModify_MissingFieldsJson_ReturnsError()
        {
            var go = new GameObject("LightModifyMissingFields");
            try
            {
                go.AddComponent<Light>();
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_light_modify",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "missing_parameter");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_LightModify_UnknownField_ReportsError_BatchSucceeds()
        {
            var go = new GameObject("LightModifyUnknown");
            try
            {
                go.AddComponent<Light>();
                var fieldsJson = "[{\"field\":\"bogusField\",\"value\":1,\"type\":\"int\"}," +
                                 "{\"field\":\"intensity\",\"value\":2.0,\"type\":\"float\"}]";
                var body = "{\"instance_id\":" +InstanceId.Of(go) +
                           ",\"fields_json\":\"" + JsonEscape(fieldsJson) + "\"," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch("unity_open_mcp_light_modify", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"bogusField\"", result.Output);
                StringAssert.Contains("Unknown field", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Reflection probe: get reads settings; bake realtime runs without
        // error. The custom-bake path is covered by a separate test below
        // (it writes a .cubemap asset, so it exercises the asset branch).
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_ReflectionProbeGet_ReportsSettings()
        {
            var go = new GameObject("ProbeGetHost");
            try
            {
                var probe = go.AddComponent<ReflectionProbe>();
                probe.resolution = 256;
                probe.hdr = true;
                var body = "{\"instance_id\":" +InstanceId.Of(go) + "}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_reflection_probe_get", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"probe\":{", result.Output);
                StringAssert.Contains("\"resolution\":256", result.Output);
                StringAssert.Contains("\"hdr\":true", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_ReflectionProbeBake_InvalidMode_ReturnsError()
        {
            var go = new GameObject("ProbeBakeBadMode");
            try
            {
                go.AddComponent<ReflectionProbe>();
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_reflection_probe_bake",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"bake_mode\":\"nope\",\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "invalid_bake_mode");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_ReflectionProbeBake_CustomMissingTarget_ReturnsError()
        {
            var go = new GameObject("ProbeBakeCustomNoTarget");
            try
            {
                go.AddComponent<ReflectionProbe>();
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_reflection_probe_bake",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"bake_mode\":\"custom\",\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "missing_parameter");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RoundTrip_ReflectionProbeBake_Realtime_RunsAndReportsBaked()
        {
            var go = new GameObject("ProbeBakeRealtime");
            try
            {
                go.AddComponent<ReflectionProbe>();
                var body = "{\"instance_id\":" +InstanceId.Of(go) +
                           ",\"bake_mode\":\"realtime\",\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_reflection_probe_bake", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"baked\":true", result.Output);
                StringAssert.Contains("\"bakeMode\":\"realtime\"", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RoundTrip_ReflectionProbeBake_Custom_WritesCubemapAsset()
        {
            const string TmpDir = "Assets/TmpLightingTests";
            const string CubemapPath = TmpDir + "/ProbeBaked.cubemap";
            var go = new GameObject("ProbeBakeCustom");
            try
            {
                // Clean any leftover from a prior run.
                if (UnityEditor.AssetDatabase.LoadAssetAtPath<Cubemap>(CubemapPath) != null)
                    UnityEditor.AssetDatabase.DeleteAsset(CubemapPath);

                go.AddComponent<ReflectionProbe>();
                var body = "{\"instance_id\":" +InstanceId.Of(go) +
                           ",\"bake_mode\":\"custom\",\"target_path\":\"" + CubemapPath + "\"," +
                           "\"paths_hint\":[\"Assets/T.unity\",\"" + CubemapPath + "\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_reflection_probe_bake", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"bakeMode\":\"custom\"", result.Output);
                StringAssert.Contains("\"cubemapPath\":\"" + CubemapPath + "\"", result.Output);
                // The cubemap asset must exist after a custom bake.
                var baked = UnityEditor.AssetDatabase.LoadAssetAtPath<Cubemap>(CubemapPath);
                Assert.IsNotNull(baked, "Custom bake should create the .cubemap asset.");
            }
            finally
            {
                Object.DestroyImmediate(go);
                if (UnityEditor.AssetDatabase.IsValidFolder(TmpDir))
                    UnityEditor.AssetDatabase.DeleteAsset(TmpDir);
            }
        }

        // -----------------------------------------------------------------
        // Skybox round-trip.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_SkyboxSet_ClearThenGet_ReflectsNull()
        {
            var original = RenderSettings.skybox;
            try
            {
                var clear = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_skybox_set",
                    "{\"paths_hint\":[\"Assets/T.unity\"]}");
                Assert.IsTrue(clear.Success, clear.ErrorMessage ?? clear.Output);
                StringAssert.Contains("\"cleared\":true", clear.Output);
                Assert.IsNull(RenderSettings.skybox);

                var get = BridgeToolRegistry.TryDispatch("unity_open_mcp_skybox_get", "{}");
                Assert.IsTrue(get.Success);
                StringAssert.Contains("\"hasSkybox\":false", get.Output);
            }
            finally
            {
                RenderSettings.skybox = original;
            }
        }

        [Test]
        public void Dispatch_SkyboxSet_InvalidPath_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_skybox_set",
                "{\"material_path\":\"not/Assets/rooted.mat\",\"paths_hint\":[\"Assets/T.unity\"]}");
            AssertErrorEnvelope(result, "invalid_asset_path");
        }

        [Test]
        public void Dispatch_SkyboxSet_MissingAsset_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_skybox_set",
                "{\"material_path\":\"Assets/Does/NotExist.mat\",\"paths_hint\":[\"Assets/T.unity\"]}");
            AssertErrorEnvelope(result, "asset_not_found");
        }

        // -----------------------------------------------------------------
        // Test helpers.
        // -----------------------------------------------------------------

        // Escape a JSON payload for embedding as a string literal inside the
        // outer tool body. Backslashes + quotes only — the bridge body parser
        // unescapes once, leaving the fields_json value as the raw JSON string
        // the field parser expects.
        private static string JsonEscape(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
