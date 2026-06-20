// Deliberate use of deprecated GetInstanceID() / EditorUtility.InstanceIDToObject() — see docs/code-conventions.md §Instance IDs.
#pragma warning disable CS0618
// EditMode tests for the Particle System extension pack.
//
// Covers the deterministic contracts that protect the agent surface:
//
//   1. Both catalog tools are discovered by BridgeToolRegistry (no core
//      bridge edits — proves the [BridgeToolType] assembly scan works for
//      packs).
//   2. modify refuses to run without paths_hint (the gate contract).
//   3. modify validates its required parameters (module, fields_json).
//   4. get on a scene ParticleSystem reports runtime state + main module.
//   5. modify → get round-trip: set main.maxParticles then read it back.
//   6. Unknown module / unknown field surface as structured errors (no thrown
//      exceptions to MCP).
//
// These tests run in EditMode against the live demo project. They create a
// transient ParticleSystem GameObject per test and clean up after themselves.
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;

namespace UnityOpenMcpExtensions.ParticleSystemExt.Tests
{
    public class ParticleSystemToolsTests
    {
        // The 2 catalog tool ids this pack must register.
        static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_particle_system_get",
            "unity_open_mcp_particle_system_modify",
        };

        [Test]
        public void Registry_BothToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected particle_system tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_GetIsReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_particle_system_get", out var get));
            Assert.IsFalse(get.IsMutating);
            Assert.IsTrue(get.ReadOnlyHint);
        }

        [Test]
        public void Registry_ModifyIsMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_particle_system_modify", out var mod));
            Assert.IsTrue(mod.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, mod.Lifecycle);
        }

        // -----------------------------------------------------------------
        // paths_hint contract — mutating tool refuses empty scope.
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

        static void AssertErrorEnvelope(ToolDispatchResult result, string expectedCode)
        {
            Assert.IsNotNull(result);
            bool sawEnvelope = (result.Output ?? "").Contains("\"code\":\"" + expectedCode + "\"");
            bool sawFail = !result.Success && result.ErrorCode == expectedCode;
            Assert.IsTrue(sawEnvelope || sawFail,
                $"Expected '{expectedCode}' envelope. Got Success={result.Success}, " +
                $"ErrorCode={result.ErrorCode}, Output={result.Output}");
        }

        [Test]
        public void Dispatch_Modify_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_particle_system_modify",
                "{\"module\":\"main\",\"fields_json\":\"{\\\"maxParticles\\\":100}\"}");
            AssertErrorEnvelope(result, "paths_hint_required");
        }

        // -----------------------------------------------------------------
        // Parameter validation branches (no scene mutation).
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_Modify_MissingModule_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_particle_system_modify",
                "{\"paths_hint\":[\"Assets/PSTest.unity\"]}");
            AssertErrorEnvelope(result, "missing_parameter");
        }

        [Test]
        public void Dispatch_Modify_MissingFieldsJson_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_particle_system_modify",
                "{\"module\":\"main\",\"paths_hint\":[\"Assets/PSTest.unity\"]}");
            AssertErrorEnvelope(result, "missing_parameter");
        }

        // -----------------------------------------------------------------
        // Target resolution branches.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_Get_OnUnknownTarget_ReturnsTargetNotFound()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_particle_system_get",
                "{\"name\":\"__nonexistent_ps_target__\"}");
            AssertErrorEnvelope(result, "target_not_found");
        }

        [Test]
        public void Dispatch_Get_OnTargetWithoutPS_ReturnsComponentNotFound()
        {
            var go = new GameObject("PSTestHostNoPS");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_particle_system_get",
                    "{\"instance_id\":" + go.GetInstanceID() + "}");
                AssertErrorEnvelope(result, "component_not_found");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Get + Modify round-trip on a scene ParticleSystem.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_Get_ReportsRuntimeAndMainModule()
        {
            var go = new GameObject("PSGetHost");
            try
            {
                var ps = go.AddComponent<ParticleSystem>();
                var body = "{\"instance_id\":" + go.GetInstanceID() +
                           ",\"include_main\":true,\"include_emission\":true}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_particle_system_get", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"runtime\":{", result.Output);
                StringAssert.Contains("\"particleCount\":0", result.Output);
                StringAssert.Contains("\"main\":{", result.Output);
                StringAssert.Contains("\"emission\":{", result.Output);
                StringAssert.Contains("\"maxParticles\":", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RoundTrip_ModifyMaxParticles_GetReflectsChange()
        {
            var go = new GameObject("PSModifyHost");
            try
            {
                var ps = go.AddComponent<ParticleSystem>();
                // Bump from the default (1000) to a known value.
                var modifyBody = "{\"instance_id\":" + go.GetInstanceID() +
                                 ",\"module\":\"main\"," +
                                 "\"fields_json\":\"{\\\"maxParticles\\\":5000,\\\"loop\\\":false}\"," +
                                 "\"paths_hint\":[\"Assets/PSTest.unity\"]}";
                var modify = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_particle_system_modify", modifyBody);
                Assert.IsTrue(modify.Success, modify.ErrorMessage ?? modify.Output);
                StringAssert.Contains("\"module\":\"main\"", modify.Output);
                StringAssert.Contains("\"maxParticles\"", modify.Output);
                StringAssert.Contains("\"loop\"", modify.Output);
                Assert.AreEqual(5000, ps.main.maxParticles);
                Assert.IsFalse(ps.main.loop);

                // Verify the read side reflects it.
                var get = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_particle_system_get",
                    "{\"instance_id\":" + go.GetInstanceID() + ",\"include_main\":true}");
                Assert.IsTrue(get.Success);
                StringAssert.Contains("\"maxParticles\":5000", get.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RoundTrip_ModifyEmissionRate_AppliesToModule()
        {
            var go = new GameObject("PSEmissionHost");
            try
            {
                var ps = go.AddComponent<ParticleSystem>();
                var modifyBody = "{\"instance_id\":" + go.GetInstanceID() +
                                 ",\"module\":\"emission\"," +
                                 "\"fields_json\":\"{\\\"rateOverTime\\\":42.0,\\\"enabled\\\":true}\"," +
                                 "\"paths_hint\":[\"Assets/PSTest.unity\"]}";
                var modify = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_particle_system_modify", modifyBody);
                Assert.IsTrue(modify.Success, modify.ErrorMessage ?? modify.Output);
                StringAssert.Contains("\"module\":\"emission\"", modify.Output);
                Assert.IsTrue(ps.emission.enabled);
                Assert.AreEqual(42f, ps.emission.rateOverTime.constant);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Structured-error branches (no thrown exceptions to MCP).
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_Modify_UnknownModule_ReturnsInvalidModule()
        {
            var go = new GameObject("PSUnknownModuleHost");
            try
            {
                go.AddComponent<ParticleSystem>();
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_particle_system_modify",
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"module\":\"not_a_module\"," +
                    "\"fields_json\":\"{}\"," +
                    "\"paths_hint\":[\"Assets/PSTest.unity\"]}");
                AssertErrorEnvelope(result, "invalid_module");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_Modify_MalformedFieldsJson_ReturnsInvalidFieldsJson()
        {
            var go = new GameObject("PSMalformedHost");
            try
            {
                go.AddComponent<ParticleSystem>();
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_particle_system_modify",
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"module\":\"main\"," +
                    "\"fields_json\":\"not an object\"," +
                    "\"paths_hint\":[\"Assets/PSTest.unity\"]}");
                AssertErrorEnvelope(result, "invalid_fields_json");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_Modify_UnknownField_ReportsUnknownFieldAndAppliesRest()
        {
            var go = new GameObject("PSUnknownFieldHost");
            try
            {
                var ps = go.AddComponent<ParticleSystem>();
                // 'maxParticles' is valid; 'bogusField' is not — both must be
                // reported and the call must still succeed (200) so the agent
                // can iterate on the valid subset.
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_particle_system_modify",
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"module\":\"main\"," +
                    "\"fields_json\":\"{\\\"maxParticles\\\":200,\\\"bogusField\\\":1}\"," +
                    "\"paths_hint\":[\"Assets/PSTest.unity\"]}");
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"appliedFields\":[\"maxParticles\"]", result.Output);
                StringAssert.Contains("\"unknownFields\":[\"bogusField\"]", result.Output);
                Assert.AreEqual(200, ps.main.maxParticles);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
