// M18 Plan 1 — Navigation embedded domain tools EditMode tests.
//
// Ported from packages/extensions/navigation/Tests/Editor (former standalone
// extension pack). Gated by UNITY_OPEN_MCP_EXT_NAVIGATION via the owning test
// asmdef's defineConstraints, so the suite only compiles + runs when the
// AI Navigation package is present — matching the compile-gate on the tool
// code under test.
#if UNITY_OPEN_MCP_EXT_NAVIGATION
#pragma warning disable CS0618
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.Extensions.Navigation;
// Alias the AI Navigation package types explicitly — see NavigationTools.cs
// for the rationale (CS0104 ambiguity with the deprecated UnityEngine.AI
// NavMesh components).
using NavMeshSurface = Unity.AI.Navigation.NavMeshSurface;
using NavMeshModifier = Unity.AI.Navigation.NavMeshModifier;

namespace UnityOpenMcpBridge.Tests.Extensions.Navigation
{
    public class NavigationToolsTests
    {
        // The 11 catalog tool ids this pack must register.
        private static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_navigation_surface_add",
            "unity_open_mcp_navigation_set_bake_settings",
            "unity_open_mcp_navigation_surface_bake",
            "unity_open_mcp_navigation_modifier_add",
            "unity_open_mcp_navigation_modifier_volume_add",
            "unity_open_mcp_navigation_link_add",
            "unity_open_mcp_navigation_agent_add",
            "unity_open_mcp_navigation_agent_set_destination",
            "unity_open_mcp_navigation_list",
            "unity_open_mcp_navigation_get",
            "unity_open_mcp_navigation_modify",
        };

        [Test]
        public void Registry_AllElevenToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected navigation tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_BakeIsMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_navigation_surface_bake", out var bake));
            Assert.IsTrue(bake.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, bake.Lifecycle);
        }

        [Test]
        public void Registry_ListAndGetAreReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_navigation_list", out var list));
            Assert.IsFalse(list.IsMutating);
            Assert.IsTrue(list.ReadOnlyHint);

            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_navigation_get", out var get));
            Assert.IsFalse(get.IsMutating);
            Assert.IsTrue(get.ReadOnlyHint);
        }

        // -----------------------------------------------------------------
        // paths_hint contract — every mutating tool refuses empty scope.
        // -----------------------------------------------------------------

        [Test]
        public void SurfaceAdd_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_navigation_surface_add",
                "{\"name\":\"NoHintTarget\"}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("paths_hint_required", result.ErrorCode);
        }

        [Test]
        public void SurfaceBake_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_navigation_surface_bake",
                "{\"name\":\"NoHintTarget\"}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("paths_hint_required", result.ErrorCode);
        }

        [Test]
        public void ModifierAdd_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_navigation_modifier_add",
                "{\"name\":\"NoHintTarget\"}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("paths_hint_required", result.ErrorCode);
        }

        [Test]
        public void AgentSetDestination_MissingDestination_ReturnsError()
        {
            // Even with paths_hint, the destination param is required.
            var go = new GameObject("AgentDestTarget");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_navigation_agent_set_destination",
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"paths_hint\":[\"Assets/NoScene.unity\"]}");
                Assert.IsNotNull(result);
                Assert.IsFalse(result.Success);
                Assert.AreEqual("missing_parameter", result.ErrorCode);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Surface add + bake round-trip on a minimal scene slice.
        // -----------------------------------------------------------------

        [Test]
        public void SurfaceAdd_OnNewHost_AddsComponent()
        {
            var host = new GameObject("NavTestSurface");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_navigation_surface_add",
                    "{\"instance_id\":" + host.GetInstanceID() +
                    ",\"agent_type\":\"Humanoid\",\"collect_objects\":\"All\"" +
                    ",\"paths_hint\":[\"Assets/NavTest.unity\"]}");

                Assert.IsNotNull(result);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"status\":\"ok\"", result.Output);
                StringAssert.Contains("\"added\":true", result.Output);
                Assert.IsNotNull(host.GetComponent<NavMeshSurface>(),
                    "NavMeshSurface should be attached after surface_add.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void SurfaceAdd_IsIdempotent_SecondCallReportsAddedFalse()
        {
            var host = new GameObject("NavTestSurfaceIdempotent");
            try
            {
                var hint = ",\"paths_hint\":[\"Assets/NavTest.unity\"]}";
                var idField = "{\"instance_id\":" + host.GetInstanceID();

                var first = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_navigation_surface_add", idField + hint);
                Assert.IsNotNull(first);
                Assert.IsTrue(first.Success, first.ErrorMessage ?? first.Output);

                var second = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_navigation_surface_add", idField + hint);
                Assert.IsNotNull(second);
                Assert.IsTrue(second.Success, second.ErrorMessage ?? second.Output);
                StringAssert.Contains("\"added\":false", second.Output);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void SurfaceBake_OnFreshSurface_ProducesNavMeshData()
        {
            // Minimal bake fixture: a plane (walkable) + surface. Bake should
            // allocate a NavMeshData sub-asset. This is the contract the
            // acceptance criteria call out — bake must be covered by EditMode.
            var host = new GameObject("NavBakeHost");
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            try
            {
                // Surface collects All geometry by default, so the plane is
                // included without extra setup.
                var add = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_navigation_surface_add",
                    "{\"instance_id\":" + host.GetInstanceID() +
                    ",\"collect_objects\":\"All\"" +
                    ",\"paths_hint\":[\"Assets/NavBake.unity\"]}");
                Assert.IsNotNull(add);
                Assert.IsTrue(add.Success, add.ErrorMessage ?? add.Output);

                var bake = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_navigation_surface_bake",
                    "{\"instance_id\":" + host.GetInstanceID() +
                    ",\"paths_hint\":[\"Assets/NavBake.unity\"]}");
                Assert.IsNotNull(bake);
                Assert.IsTrue(bake.Success, bake.ErrorMessage ?? bake.Output);
                StringAssert.Contains("\"baked\":true", bake.Output);
                StringAssert.Contains("\"hasNavMeshData\":true", bake.Output);

                var surface = host.GetComponent<NavMeshSurface>();
                Assert.IsNotNull(surface.navMeshData,
                    "NavMeshSurface.navMeshData should be populated after bake.");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(plane);
            }
        }

        // -----------------------------------------------------------------
        // List / Get discovery round-trip.
        // -----------------------------------------------------------------

        [Test]
        public void List_ReportsSurfacesAndAgents()
        {
            var surface = new GameObject("NavListSurface");
            var agent = new GameObject("NavListAgent");
            try
            {
                surface.AddComponent<NavMeshSurface>();
                var navAgent = agent.AddComponent<NavMeshAgent>();
                navAgent.speed = 7f;

                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_navigation_list", "{}");
                Assert.IsNotNull(result);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"surfaces\":", result.Output);
                StringAssert.Contains("NavListSurface", result.Output);
                StringAssert.Contains("\"agents\":", result.Output);
                StringAssert.Contains("NavListAgent", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(surface);
                Object.DestroyImmediate(agent);
            }
        }

        [Test]
        public void Get_OnTargetWithNavMeshComponents_ReturnsAll()
        {
            var host = new GameObject("NavGetHost");
            try
            {
                host.AddComponent<NavMeshSurface>();
                host.AddComponent<NavMeshAgent>();
                host.AddComponent<NavMeshModifier>();

                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_navigation_get",
                    "{\"instance_id\":" + host.GetInstanceID() + "}");
                Assert.IsNotNull(result);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"type\":\"NavMeshSurface\"", result.Output);
                StringAssert.Contains("\"type\":\"NavMeshAgent\"", result.Output);
                StringAssert.Contains("\"type\":\"NavMeshModifier\"", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Get_OnUnknownTarget_ReturnsTargetNotFound()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_navigation_get",
                "{\"name\":\"__nonexistent_navigation_target__\"}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("target_not_found", result.ErrorCode);
        }

        [Test]
        public void SurfaceAdd_OnUnknownTarget_ReturnsTargetNotFound()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_navigation_surface_add",
                "{\"name\":\"__nonexistent_navigation_target__\"" +
                ",\"paths_hint\":[\"Assets/NavTest.unity\"]}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("target_not_found", result.ErrorCode);
        }

        // -----------------------------------------------------------------
        // Modify — reflective field setter.
        // -----------------------------------------------------------------

        [Test]
        public void Modify_OnNavMeshAgent_UpdatesSpeed()
        {
            var host = new GameObject("NavModifyAgent");
            try
            {
                var agent = host.AddComponent<NavMeshAgent>();
                agent.speed = 1f;

                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_navigation_modify",
                    "{\"instance_id\":" + host.GetInstanceID() +
                    ",\"component_type\":\"NavMeshAgent\"" +
                    ",\"fields_json\":\"[{\\\"field\\\":\\\"speed\\\",\\\"value\\\":5.5,\\\"type\\\":\\\"float\\\"}]\"" +
                    ",\"paths_hint\":[\"Assets/NavTest.unity\"]}");

                Assert.IsNotNull(result);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"applied\":[", result.Output);
                Assert.AreEqual(5.5f, agent.speed);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Modify_MissingComponentType_ReturnsMissingParameter()
        {
            var host = new GameObject("NavModifyNoType");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_navigation_modify",
                    "{\"instance_id\":" + host.GetInstanceID() +
                    ",\"fields_json\":\"[]\"" +
                    ",\"paths_hint\":[\"Assets/NavTest.unity\"]}");
                Assert.IsNotNull(result);
                Assert.IsFalse(result.Success);
                Assert.AreEqual("missing_parameter", result.ErrorCode);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Modify_OnMissingComponent_ReturnsComponentNotFound()
        {
            var host = new GameObject("NavModifyNoComp");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_navigation_modify",
                    "{\"instance_id\":" + host.GetInstanceID() +
                    ",\"component_type\":\"NavMeshSurface\"" +
                    ",\"fields_json\":\"[{\\\"field\\\":\\\"agentTypeID\\\",\\\"value\\\":1}]\"" +
                    ",\"paths_hint\":[\"Assets/NavTest.unity\"]}");
                Assert.IsNotNull(result);
                Assert.IsFalse(result.Success);
                Assert.AreEqual("component_not_found", result.ErrorCode);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
#endif
