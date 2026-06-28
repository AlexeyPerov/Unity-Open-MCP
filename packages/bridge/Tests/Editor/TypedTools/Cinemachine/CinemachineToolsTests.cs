// M20 Plan 6 / T20.6.1 — Cinemachine embedded domain tools EditMode tests.
//
// Reflection-gated (the assembly always compiles — see CinemachineTools.cs).
// The deterministic tests cover the error-envelope paths that do NOT depend on
// Cinemachine being installed: paths_hint_required, missing_parameter, and the
// package / version detection errors. The happy-path tests are gated on
// CinemachineVersion.Supported so they only exercise the live Cinemachine 3.x
// path when the package is actually present in the host project.
//
// NOTE on the error-envelope contract: the registry's TryDispatch wraps every
// successful invocation in ToolDispatchResult.Ok(output). A tool that refuses
// (e.g. missing paths_hint) returns an Ok dispatch whose Output carries the
// JSON error envelope `{"error":{"code":...,"message":...}}`. These tests
// therefore assert against Output content for the refusal paths, and against
// Success + Output for the happy paths.
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.Extensions.Cinemachine;

namespace UnityOpenMcpBridge.Tests.Extensions.Cinemachine
{
    public class CinemachineToolsTests
    {
        // The 7 catalog tool ids this pack must register. Reflection-gated
        // tools ALWAYS register (the assembly compiles in every project).
        private static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_cinemachine_create_camera",
            "unity_open_mcp_cinemachine_set_targets",
            "unity_open_mcp_cinemachine_set_lens",
            "unity_open_mcp_cinemachine_set_body",
            "unity_open_mcp_cinemachine_set_noise",
            "unity_open_mcp_cinemachine_brain_ensure",
            "unity_open_mcp_cinemachine_camera_list",
        };

        [SetUp]
        public void ResetVersionCache()
        {
            // The CinemachineVersion layer caches its detection; clear it
            // between tests so a [OneTimeTearDown] from another suite does
            // not bleed stale state.
            CinemachineVersion.ResetForTests();
        }

        [Test]
        public void Registry_AllSevenToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected cinemachine tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_CreateCameraIsMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_cinemachine_create_camera", out var create));
            Assert.IsTrue(create.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, create.Lifecycle);
            Assert.AreEqual("cinemachine", create.Group);
        }

        [Test]
        public void Registry_CameraListIsReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_cinemachine_camera_list", out var list));
            Assert.IsFalse(list.IsMutating);
            Assert.IsTrue(list.ReadOnlyHint);
            Assert.AreEqual(GateMode.Off, list.Gate);
        }

        // -----------------------------------------------------------------
        // Group membership — all 7 tools map to the "cinemachine" group.
        // -----------------------------------------------------------------

        [Test]
        public void Registry_AllCinemachineToolsBelongToCinemachineGroup()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var entry),
                    $"Tool '{id}' not registered.");
                Assert.AreEqual("cinemachine", entry.Group,
                    $"Tool '{id}' should belong to the 'cinemachine' group.");
            }
        }

        // -----------------------------------------------------------------
        // paths_hint contract — every mutating tool refuses empty scope by
        // returning the paths_hint_required error envelope. This path runs
        // BEFORE the Cinemachine version check, so it succeeds regardless of
        // whether the package is installed.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_CreateCamera_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_cinemachine_create_camera",
                "{\"name\":\"NoHintCamera\"}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success); // invocation succeeded; refusal is in Output.
            StringAssert.Contains("\"error\"", result.Output);
            StringAssert.Contains("paths_hint_required", result.Output);
        }

        [Test]
        public void Dispatch_SetTargets_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_cinemachine_set_targets",
                "{\"follow_path\":\"Player\"}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("paths_hint_required", result.Output);
        }

        [Test]
        public void Dispatch_SetLens_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_cinemachine_set_lens",
                "{\"field_of_view\":60}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("paths_hint_required", result.Output);
        }

        [Test]
        public void Dispatch_BrainEnsure_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_cinemachine_brain_ensure",
                "{}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("paths_hint_required", result.Output);
        }

        // -----------------------------------------------------------------
        // SetLens / SetBody / SetNoise require their primary field — these
        // paths run AFTER the version check, so they only assert the
        // missing_parameter envelope when Cinemachine 3.x is present (the
        // package-missing / version-too-old envelope is otherwise asserted).
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_SetLens_NoFields_ReturnsPackageOrMissingError()
        {
            var go = new GameObject("CinemachineNoLens");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_cinemachine_set_lens",
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"paths_hint\":[\"Assets/NoScene.unity\"]}");
                Assert.IsNotNull(result);
                Assert.IsTrue(result.Success);
                // Either Cinemachine is missing (cinemachine_package_required /
                // cinemachine_3x_required) or it is present and the tool refuses
                // for missing_parameter.
                Assert.IsTrue(
                    result.Output.Contains("cinemachine_package_required") ||
                    result.Output.Contains("cinemachine_3x_required") ||
                    result.Output.Contains("missing_parameter"),
                    $"Unexpected output: {result.Output}");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_SetBody_NoBodyName_ReturnsPackageOrMissingError()
        {
            var go = new GameObject("CinemachineNoBody");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_cinemachine_set_body",
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"paths_hint\":[\"Assets/NoScene.unity\"]}");
                Assert.IsNotNull(result);
                Assert.IsTrue(result.Success);
                Assert.IsTrue(
                    result.Output.Contains("cinemachine_package_required") ||
                    result.Output.Contains("cinemachine_3x_required") ||
                    result.Output.Contains("missing_parameter"),
                    $"Unexpected output: {result.Output}");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Happy path — only exercised when Cinemachine 3.x is present.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_CreateCamera_BrainEnsure_ListCameras_WhenCinemachinePresent()
        {
            CinemachineVersion.ResetForTests();
            if (!CinemachineVersion.Supported)
            {
                Assert.Ignore(
                    "Cinemachine 3.x not installed in this project — skipping " +
                    "the live round-trip test. The error-envelope paths above " +
                    "still cover the reflection-gating surface.");
            }

            GameObject camera = null;
            GameObject brainHost = null;
            try
            {
                // Create a host Camera for the Brain.
                brainHost = new GameObject("Main Camera");
                brainHost.AddComponent<Camera>();
                brainHost.tag = "MainCamera";

                var brain = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_cinemachine_brain_ensure",
                    "{\"instance_id\":" + brainHost.GetInstanceID() +
                    ",\"paths_hint\":[\"Assets/CinemachineTest.unity\"]}");
                Assert.IsNotNull(brain);
                Assert.IsTrue(brain.Success, brain.ErrorMessage ?? brain.Output);
                StringAssert.Contains("\"status\":\"ok\"", brain.Output);

                // Create the CinemachineCamera.
                var create = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_cinemachine_create_camera",
                    "{\"name\":\"RoundTripCamera\",\"priority\":5," +
                    "\"paths_hint\":[\"Assets/CinemachineTest.unity\"]}");
                Assert.IsNotNull(create);
                Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);
                StringAssert.Contains("\"status\":\"ok\"", create.Output);

                var id = ExtractInt(create.Output, "instanceId");
                Assert.AreNotEqual(0, id);
                camera = EditorUtility.InstanceIDToObject(id) as GameObject;
                Assert.IsNotNull(camera);

                // Set lens.
                var lens = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_cinemachine_set_lens",
                    "{\"instance_id\":" + id + ",\"field_of_view\":45," +
                    "\"paths_hint\":[\"Assets/CinemachineTest.unity\"]}");
                Assert.IsNotNull(lens);
                Assert.IsTrue(lens.Success, lens.ErrorMessage ?? lens.Output);
                StringAssert.Contains("\"fieldOfView\":45", lens.Output);

                // List should include the new camera.
                var list = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_cinemachine_camera_list",
                    "{}");
                Assert.IsNotNull(list);
                Assert.IsTrue(list.Success, list.ErrorMessage ?? list.Output);
                StringAssert.Contains("\"instanceId\":" + id, list.Output);
            }
            finally
            {
                if (camera != null) Object.DestroyImmediate(camera);
                if (brainHost != null) Object.DestroyImmediate(brainHost);
            }
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static int ExtractInt(string json, string key)
        {
            var pattern = "\"" + key + "\":";
            var idx = json.IndexOf(pattern, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(idx, 0, $"Expected key '{key}' in output: {json}");
            var start = idx + pattern.Length;
            var end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            return int.Parse(json.Substring(start, end - start));
        }
    }
}
