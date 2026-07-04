// M20 Plan 3 / T20.3.3 — Constraints & LOD embedded domain tools EditMode
// tests.
//
// Ungated (no UNITY_OPEN_MCP_EXT_CONSTRAINTS): the PositionConstraint /
// RotationConstraint / AimConstraint / ParentConstraint / ScaleConstraint types
// (UnityEngine.AnimationModule) and LODGroup (UnityEngine.CoreModule) are
// built-in engine types present on every Unity install, so the tools — and
// this suite — compile unconditionally. The test asmdef only constrains
// UNITY_TEST_FRAMEWORK.
#pragma warning disable CS0618
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Animations;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Tests.Extensions.Constraints
{
    public class ConstraintsToolsTests
    {
        // The 3 catalog tool ids this domain must register.
        private static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_constraint_add",
            "unity_open_mcp_lod_group_configure",
            "unity_open_mcp_lod_add_level",
        };

        [Test]
        public void Registry_AllThreeToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected constraints tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_AllToolsAreMutatingAndEditorSettle()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var info),
                    $"Expected '{id}' to resolve.");
                Assert.IsTrue(info.IsMutating,
                    $"Expected '{id}' to be mutating.");
                Assert.AreEqual(LifecyclePolicy.EditorSettle, info.Lifecycle,
                    $"Expected '{id}' to use EditorSettle lifecycle.");
            }
        }

        [Test]
        public void Registry_AllToolsAssignedToConstraintsGroup()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var info));
                Assert.AreEqual("constraints", info.Group,
                    $"Expected '{id}' to be in the 'constraints' group.");
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
        public void Dispatch_ConstraintAdd_MissingPathsHint_ReturnsError()
        {
            var go = new GameObject("ConstraintAddNoHint");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_constraint_add",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"constraint_type\":\"AimConstraint\"}");
                AssertErrorEnvelope(result, "paths_hint_required");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_LodGroupConfigure_MissingPathsHint_ReturnsError()
        {
            var go = new GameObject("LodConfigureNoHint");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_lod_group_configure",
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
                AssertErrorEnvelope(result, "paths_hint_required");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_LodAddLevel_MissingPathsHint_ReturnsError()
        {
            var go = new GameObject("LodAddNoHint");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_lod_add_level",
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
                AssertErrorEnvelope(result, "paths_hint_required");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Target / parameter resolution branches.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_ConstraintAdd_OnUnknownTarget_ReturnsTargetNotFound()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_constraint_add",
                "{\"name\":\"__nonexistent_constraint_target__\"," +
                "\"constraint_type\":\"AimConstraint\",\"paths_hint\":[\"Assets/T.unity\"]}");
            AssertErrorEnvelope(result, "target_not_found");
        }

        [Test]
        public void Dispatch_ConstraintAdd_MissingType_ReturnsMissingParameter()
        {
            var go = new GameObject("ConstraintAddNoType");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_constraint_add",
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
        public void Dispatch_ConstraintAdd_InvalidType_ReturnsInvalidConstraintType()
        {
            var go = new GameObject("ConstraintAddBadType");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_constraint_add",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"constraint_type\":\"LookAtConstraint\"," +
                    "\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "invalid_constraint_type");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_ConstraintAdd_UnknownSource_ReturnsSourceNotFound()
        {
            var go = new GameObject("ConstraintAddBadSource");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_constraint_add",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"constraint_type\":\"PositionConstraint\"," +
                    "\"source_path\":\"Does/Not/Exist\"," +
                    "\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "source_not_found");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_LodAddLevel_OnTargetWithoutGroup_ReturnsComponentNotFound()
        {
            var go = new GameObject("LodAddNoGroup");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_lod_add_level",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "component_not_found");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_LodGroupConfigure_InvalidFadeMode_ReturnsInvalidFadeMode()
        {
            var go = new GameObject("LodConfigBadFade");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_lod_group_configure",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"fade_mode\":\"Bogus\"," +
                    "\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "invalid_fade_mode");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Constraint round-trip: add with source → verify activation + source.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_ConstraintAdd_AimConstraint_WithSource_Activates()
        {
            var host = new GameObject("AimHost");
            var source = new GameObject("AimSource");
            try
            {
                var body = "{\"instance_id\":" +InstanceId.Of(host) +
                           ",\"constraint_type\":\"AimConstraint\"," +
                           "\"source_instance_id\":" +InstanceId.Of(source) +
                           ",\"weight\":0.5,\"constraint_active\":true," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_constraint_add", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"added\":true", result.Output);
                StringAssert.Contains("\"type\":\"AimConstraint\"", result.Output);
                StringAssert.Contains("\"constraintActive\":true", result.Output);
                StringAssert.Contains("\"sourceCount\":1", result.Output);
                StringAssert.Contains("\"sourceAdded\":true", result.Output);

                var c = host.GetComponent<AimConstraint>();
                Assert.IsNotNull(c);
                Assert.IsTrue(c.constraintActive);
                Assert.AreEqual(1, c.sourceCount);
                Assert.AreEqual(source.transform, c.GetSource(0).sourceTransform);
                Assert.AreEqual(0.5f, c.GetSource(0).weight);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void RoundTrip_ConstraintAdd_Idempotent_ReusingReportsAddedFalse()
        {
            var host = new GameObject("AimIdem");
            try
            {
                host.AddComponent<AimConstraint>();
                var body = "{\"instance_id\":" +InstanceId.Of(host) +
                           ",\"constraint_type\":\"AimConstraint\"," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_constraint_add", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"added\":false", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RoundTrip_ConstraintAdd_EachType_AddsAndActivates()
        {
            // Every catalog type adds with a source and activates (the
            // acceptance criterion calls out an AimConstraint verify; we widen
            // to all five so a regression on any one type is caught here).
            var source = new GameObject("ConstraintSource");
            try
            {
                var cases = new[]
                {
                    ("PositionConstraint", typeof(PositionConstraint)),
                    ("RotationConstraint", typeof(RotationConstraint)),
                    ("AimConstraint", typeof(AimConstraint)),
                    ("ParentConstraint", typeof(ParentConstraint)),
                    ("ScaleConstraint", typeof(ScaleConstraint)),
                };
                foreach (var (typeName, type) in cases)
                {
                    var host = new GameObject("ConstraintHost_" + typeName);
                    try
                    {
                        var body = "{\"instance_id\":" +InstanceId.Of(host) +
                                   ",\"constraint_type\":\"" + typeName + "\"," +
                                   "\"source_instance_id\":" +InstanceId.Of(source) +
                                   ",\"constraint_active\":true," +
                                   "\"paths_hint\":[\"Assets/T.unity\"]}";
                        var result = BridgeToolRegistry.TryDispatch(
                            "unity_open_mcp_constraint_add", body);
                        Assert.IsTrue(result.Success,
                            $"{typeName}: {result.ErrorMessage ?? result.Output}");
                        StringAssert.Contains("\"added\":true", result.Output);
                        StringAssert.Contains("\"sourceCount\":1", result.Output);

                        Assert.IsNotNull(host.GetComponent(type),
                            $"Expected {typeName} on the host.");
                        var c = host.GetComponent(type) as IConstraint;
                        Assert.IsNotNull(c);
                        Assert.IsTrue(c.constraintActive);
                    }
                    finally
                    {
                        Object.DestroyImmediate(host);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(source);
            }
        }

        // -----------------------------------------------------------------
        // LODGroup round-trip: configure → add level → verify array.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_LodGroupConfigure_AllocatesLodCount()
        {
            var host = new GameObject("LodHost");
            try
            {
                var body = "{\"instance_id\":" +InstanceId.Of(host) +
                           ",\"fade_mode\":\"CrossFade\",\"animate_cross_fading\":true," +
                           "\"lod_count\":3,\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_lod_group_configure", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"added\":true", result.Output);
                StringAssert.Contains("\"fadeMode\":\"CrossFade\"", result.Output);
                StringAssert.Contains("\"animateCrossFading\":true", result.Output);
                StringAssert.Contains("\"lodCount\":3", result.Output);

                var group = host.GetComponent<LODGroup>();
                Assert.IsNotNull(group);
                Assert.AreEqual(LODFadeMode.CrossFade, group.fadeMode);
                Assert.IsTrue(group.animateCrossFading);
                Assert.AreEqual(3, group.lodCount);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RoundTrip_LodGroupConfigure_Idempotent_ReusingReportsAddedFalse()
        {
            var host = new GameObject("LodIdem");
            try
            {
                host.AddComponent<LODGroup>();
                var body = "{\"instance_id\":" +InstanceId.Of(host) +
                           ",\"lod_count\":2,\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_lod_group_configure", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"added\":false", result.Output);
                StringAssert.Contains("\"lodCount\":2", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RoundTrip_LodAddLevel_WiresRenderersAndAppends()
        {
            var host = new GameObject("LodAddHost");
            var mesh1 = new GameObject("LodMesh0");
            var mesh2 = new GameObject("LodMesh1");
            try
            {
                mesh1.transform.SetParent(host.transform);
                mesh2.transform.SetParent(host.transform);
                // LOD renderers must be Renderer components.
                mesh1.AddComponent<MeshRenderer>();
                mesh2.AddComponent<MeshRenderer>();

                // Hierarchy paths are slash-separated from a root —
                // "LodAddHost/LodMesh0" / "LodAddHost/LodMesh1".
                const string mesh1Path = "LodAddHost/LodMesh0";
                const string mesh2Path = "LodAddHost/LodMesh1";

                // First configure a LODGroup with 1 level.
                var configBody = "{\"instance_id\":" +InstanceId.Of(host) +
                                 ",\"lod_count\":1,\"paths_hint\":[\"Assets/T.unity\"]}";
                var config = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_lod_group_configure", configBody);
                Assert.IsTrue(config.Success, config.ErrorMessage ?? config.Output);

                // Replace level 0 with mesh1 (in-place).
                var addBody0 = "{\"instance_id\":" +InstanceId.Of(host) +
                               ",\"index\":0,\"screen_relative_transition_height\":0.6," +
                               "\"renderers\":[\"" + mesh1Path + "\"]," +
                               "\"paths_hint\":[\"Assets/T.unity\"]}";
                var add0 = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_lod_add_level", addBody0);
                Assert.IsTrue(add0.Success, add0.ErrorMessage ?? add0.Output);
                StringAssert.Contains("\"action\":\"replaced\"", add0.Output);
                StringAssert.Contains("\"rendererCount\":1", add0.Output);

                // Append a new level 1 with mesh2.
                var addBody1 = "{\"instance_id\":" +InstanceId.Of(host) +
                               ",\"index\":1,\"screen_relative_transition_height\":0.2," +
                               "\"renderers\":[\"" + mesh2Path + "\"]," +
                               "\"paths_hint\":[\"Assets/T.unity\"]}";
                var add1 = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_lod_add_level", addBody1);
                Assert.IsTrue(add1.Success, add1.ErrorMessage ?? add1.Output);
                StringAssert.Contains("\"action\":\"appended\"", add1.Output);

                var group = host.GetComponent<LODGroup>();
                Assert.IsNotNull(group);
                Assert.AreEqual(2, group.lodCount);
                var lods = group.GetLODs();
                Assert.AreEqual(2, lods.Length);
                Assert.AreEqual(1, lods[0].renderers.Length);
                Assert.AreEqual(mesh1.GetComponent<MeshRenderer>(), lods[0].renderers[0]);
                Assert.AreEqual(0.6f, lods[0].screenRelativeTransitionHeight);
                Assert.AreEqual(1, lods[1].renderers.Length);
                Assert.AreEqual(mesh2.GetComponent<MeshRenderer>(), lods[1].renderers[0]);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RoundTrip_LodAddLevel_OutOfRangeIndex_ReturnsInvalidIndex()
        {
            var host = new GameObject("LodAddBadIndex");
            try
            {
                host.AddComponent<LODGroup>();
                var body = "{\"instance_id\":" +InstanceId.Of(host) +
                           ",\"index\":5,\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_lod_add_level", body);
                AssertErrorEnvelope(result, "invalid_index");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
