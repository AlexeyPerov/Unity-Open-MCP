// M18 Plan 7 / T18.7.3 — Splines embedded domain tools EditMode tests.
//
// Gated by UNITY_OPEN_MCP_EXT_SPLINES via the owning test asmdef's
// defineConstraints, so the suite only compiles + runs when the
// com.unity.splines package is present — matching the compile-gate on the
// tool code under test.
//
// NOTE on the error-envelope contract: the registry's TryDispatch wraps every
// successful invocation in ToolDispatchResult.Ok(output). A tool that refuses
// (e.g. missing paths_hint) returns an Ok dispatch whose Output carries the
// JSON error envelope `{"error":{"code":...,"message":...}}`. These tests
// therefore assert against Output content for the refusal paths, and against
// Success + Output for the happy paths. (The dogfood demo does not install
// com.unity.splines, so this assembly is not compiled in CI — it runs only
// in a project that opts into the Splines package.)
#if UNITY_OPEN_MCP_EXT_SPLINES
#pragma warning disable CS0618
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.Extensions.Splines;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Tests.Extensions.Splines
{
    public class SplinesToolsTests
    {
        // The 7 catalog tool ids this pack must register.
        private static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_splines_container_create",
            "unity_open_mcp_splines_add_knot",
            "unity_open_mcp_splines_set_knot",
            "unity_open_mcp_splines_set_tangent_mode",
            "unity_open_mcp_splines_evaluate",
            "unity_open_mcp_splines_get_knots",
            "unity_open_mcp_splines_modify",
        };

        [Test]
        public void Registry_AllSevenToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected splines tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_ContainerCreateIsMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_splines_container_create", out var create));
            Assert.IsTrue(create.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, create.Lifecycle);
            Assert.AreEqual("splines", create.Group);
        }

        [Test]
        public void Registry_EvaluateAndGetKnotsAreReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_splines_evaluate", out var eval));
            Assert.IsFalse(eval.IsMutating);
            Assert.IsTrue(eval.ReadOnlyHint);
            Assert.AreEqual(GateMode.Off, eval.Gate);

            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_splines_get_knots", out var get));
            Assert.IsFalse(get.IsMutating);
            Assert.IsTrue(get.ReadOnlyHint);
        }

        // -----------------------------------------------------------------
        // Group membership — all 7 tools map to the "splines" group.
        // -----------------------------------------------------------------

        [Test]
        public void Registry_AllSplinesToolsBelongToSplinesGroup()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var entry),
                    $"Tool '{id}' not registered.");
                Assert.AreEqual("splines", entry.Group,
                    $"Tool '{id}' should belong to the 'splines' group.");
            }
        }

        // -----------------------------------------------------------------
        // paths_hint contract — every mutating tool refuses empty scope by
        // returning the paths_hint_required error envelope.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_ContainerCreate_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_splines_container_create",
                "{\"name\":\"NoHintContainer\"}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success); // invocation succeeded; refusal is in Output.
            StringAssert.Contains("\"error\"", result.Output);
            StringAssert.Contains("paths_hint_required", result.Output);
        }

        [Test]
        public void Dispatch_AddKnot_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_splines_add_knot",
                "{\"position\":\"0,0,0\"}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("paths_hint_required", result.Output);
        }

        // -----------------------------------------------------------------
        // Validation branches (no scene mutation — pure validation).
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_AddKnot_MissingPosition_ReturnsError()
        {
            // Even with paths_hint, position is required.
            var go = new GameObject("SplinesNoPos");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_splines_add_knot",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"paths_hint\":[\"Assets/NoScene.unity\"]}");
                Assert.IsNotNull(result);
                Assert.IsTrue(result.Success);
                StringAssert.Contains("missing_parameter", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_AddKnot_NoContainer_ReturnsComponentNotFoundError()
        {
            var go = new GameObject("SplinesNoContainer");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_splines_add_knot",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"position\":\"0,0,0\"" +
                    ",\"paths_hint\":[\"Assets/NoScene.unity\"]}");
                Assert.IsNotNull(result);
                Assert.IsTrue(result.Success);
                StringAssert.Contains("component_not_found", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_SetTangentMode_InvalidMode_ReturnsError()
        {
            var go = new GameObject("SplinesBadMode");
            go.AddComponent<SplineContainer>();
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_splines_set_tangent_mode",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"tangent_mode\":\"NotAMode\"" +
                    ",\"paths_hint\":[\"Assets/NoScene.unity\"]}");
                Assert.IsNotNull(result);
                Assert.IsTrue(result.Success);
                StringAssert.Contains("invalid_tangent_mode", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Happy paths — create container → add knots → evaluate.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_CreateContainer_AddKnots_Evaluate_ReturnsPosition()
        {
            GameObject created = null;
            try
            {
                // Create a container at the scene root.
                var create = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_splines_container_create",
                    "{\"name\":\"SplinesRoundTrip\"," +
                    "\"paths_hint\":[\"Assets/SplinesTest.unity\"]}");
                Assert.IsNotNull(create);
                Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);
                StringAssert.Contains("\"status\":\"ok\"", create.Output);
                StringAssert.Contains("\"splineCount\":1", create.Output);

                var id = ExtractInt(create.Output, "instanceId");
                Assert.AreNotEqual(0, id, "Container should report an instance id.");

                created = InstanceId.ToObject(id) as GameObject;
                Assert.IsNotNull(created, "Created GameObject should resolve by id.");
                Assert.IsNotNull(created.GetComponent<SplineContainer>());

                // Add three knots to form a curve.
                var hint = ",\"paths_hint\":[\"Assets/SplinesTest.unity\"]}";
                AddKnot(id, "0,0,0", hint);
                AddKnot(id, "5,0,0", hint);
                AddKnot(id, "10,0,5", hint);

                // get_knots reports three knots.
                var knots = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_splines_get_knots",
                    "{\"instance_id\":" + id + "}");
                Assert.IsNotNull(knots);
                Assert.IsTrue(knots.Success, knots.ErrorMessage ?? knots.Output);
                StringAssert.Contains("\"knotCount\":3", knots.Output);

                // evaluate at t=0.5 must report a status:ok position.
                var eval = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_splines_evaluate",
                    "{\"instance_id\":" + id + ",\"t\":0.5}");
                Assert.IsNotNull(eval);
                Assert.IsTrue(eval.Success, eval.ErrorMessage ?? eval.Output);
                StringAssert.Contains("\"position\":", eval.Output);
                StringAssert.Contains("\"tangent\":", eval.Output);
            }
            finally
            {
                if (created != null) Object.DestroyImmediate(created);
            }
        }

        [Test]
        public void Dispatch_Evaluate_TooFewKnots_ReturnsError()
        {
            var go = new GameObject("SplinesEmptyEval");
            go.AddComponent<SplineContainer>();
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_splines_evaluate",
                    "{\"instance_id\":" +InstanceId.Of(go) + ",\"t\":0.5}");
                Assert.IsNotNull(result);
                Assert.IsTrue(result.Success);
                StringAssert.Contains("spline_too_short", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static void AddKnot(int instanceId, string position, string hint)
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_splines_add_knot",
                "{\"instance_id\":" + instanceId +
                ",\"position\":\"" + position + "\"" + hint);
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
            StringAssert.Contains("\"added\":true", result.Output);
        }

        // Pull an integer value out of a flat JSON output by key. Mirrors the
        // helper in ProBuilderToolsTests.
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
#endif
