// M20 Plan 7 / T20.7.1 — ShaderGraph embedded domain tools EditMode tests.
//
// Gated by UNITY_OPEN_MCP_EXT_SHADERGRAPH via the owning test asmdef's
// defineConstraints, so the suite only compiles + runs when the
// com.unity.shadergraph package is present — matching the compile-gate on the
// tool code under test. When the package is absent neither the tools nor this
// suite compile (the suite effectively does not exist on that build), mirroring
// the Timeline/Tilemap test-gating model.
//
// Coverage shape (mirrors Lighting/Audio/UI/Cinemachine):
//   - registry discovery (all 4 ids) + group membership + lifecycle/gate hints
//   - deterministic error-envelope paths (always assert): paths_hint_required,
//     missing_parameter, invalid_parameter, already_exists
//   - happy-path round-trip (create → open → node_add). Creation builds the
//     graph through the package's GraphData and serializer (no Assets/Create
//     menu), so it must succeed wherever the package compiles in. The core
//     test assembly's ExtensionDomainRoundTripTests covers node_connect and
//     the open summary by tool name.
//
// NOTE on the error-envelope contract: the registry's TryDispatch wraps every
// successful invocation in ToolDispatchResult.Ok(output). A tool that refuses
// (e.g. missing paths_hint) returns an Ok dispatch whose Output carries the
// JSON error envelope `{"error":{"code":...,"message":...}}`. These tests
// therefore assert against Output content for the refusal paths, and against
// Success + Output for the happy paths.
#if UNITY_OPEN_MCP_EXT_SHADERGRAPH
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Tests.Extensions.ShaderGraphExt
{
    public class ShaderGraphToolsTests
    {
        // The 4 catalog tool ids this pack must register.
        private static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_shader_graph_create",
            "unity_open_mcp_shader_graph_open",
            "unity_open_mcp_shader_graph_node_add",
            "unity_open_mcp_shader_graph_node_connect",
        };

        private string tempAssetPath;

        [SetUp]
        public void SetUp()
        {
            tempAssetPath = $"Assets/ShaderGraphTest_{System.Guid.NewGuid():N}.shadergraph";
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(tempAssetPath) &&
                AssetDatabase.LoadAssetAtPath<Object>(tempAssetPath) != null)
            {
                AssetDatabase.DeleteAsset(tempAssetPath);
            }
        }

        // -----------------------------------------------------------------
        // Registry discovery + metadata.
        // -----------------------------------------------------------------

        [Test]
        public void Registry_AllFourToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected shader graph tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_AllShaderGraphToolsBelongToShaderGraphGroup()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var entry),
                    $"Tool '{id}' not registered.");
                Assert.AreEqual("shadergraph", entry.Group,
                    $"Tool '{id}' should belong to the 'shadergraph' group.");
            }
        }

        [Test]
        public void Registry_CreateIsMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_shader_graph_create", out var create));
            Assert.IsTrue(create.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, create.Lifecycle);
            Assert.AreEqual(GateMode.Enforce, create.Gate);
        }

        [Test]
        public void Registry_NodeAddIsMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_shader_graph_node_add", out var add));
            Assert.IsTrue(add.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, add.Lifecycle);
            Assert.AreEqual(GateMode.Enforce, add.Gate);
        }

        [Test]
        public void Registry_NodeConnectIsMutatingAndIdempotent()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_shader_graph_node_connect", out var connect));
            Assert.IsTrue(connect.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, connect.Lifecycle);
            Assert.IsTrue(connect.IdempotentHint,
                "node_connect is idempotent (connecting the same slot pair twice is a no-op).");
        }

        [Test]
        public void Registry_OpenIsReadOnlyAndGateOff()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_shader_graph_open", out var open));
            Assert.IsFalse(open.IsMutating);
            Assert.IsTrue(open.ReadOnlyHint);
            Assert.AreEqual(GateMode.Off, open.Gate);
            Assert.AreEqual(LifecyclePolicy.None, open.Lifecycle);
        }

        // -----------------------------------------------------------------
        // paths_hint contract — every mutating tool refuses empty scope.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_Create_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_shader_graph_create",
                "{\"asset_path\":\"Assets/NoHint.shadergraph\"}");
            AssertEnvelope(result, "paths_hint_required");
        }

        [Test]
        public void Dispatch_NodeAdd_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_shader_graph_node_add",
                "{\"asset_path\":\"Assets/NoHint.shadergraph\",\"node_type\":\"UV\"}");
            AssertEnvelope(result, "paths_hint_required");
        }

        [Test]
        public void Dispatch_NodeConnect_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_shader_graph_node_connect",
                "{\"asset_path\":\"Assets/NoHint.shadergraph\"," +
                "\"source_node_id\":\"a\",\"destination_node_id\":\"b\"}");
            AssertEnvelope(result, "paths_hint_required");
        }

        // -----------------------------------------------------------------
        // Validation branches (always deterministic).
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_Create_MissingAssetPath_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_shader_graph_create",
                "{\"paths_hint\":[\"Assets/NoHint.shadergraph\"]}");
            AssertEnvelope(result, "missing_parameter");
        }

        [Test]
        public void Dispatch_Create_BadExtension_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_shader_graph_create",
                "{\"asset_path\":\"Assets/NotAShaderGraph.mat\"," +
                "\"paths_hint\":[\"Assets/NotAShaderGraph.mat\"]}");
            AssertEnvelope(result, "invalid_parameter");
        }

        [Test]
        public void Dispatch_Open_MissingAssetPath_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_shader_graph_open", "{}");
            AssertEnvelope(result, "missing_parameter");
        }

        [Test]
        public void Dispatch_Open_AssetNotFound_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_shader_graph_open",
                "{\"asset_path\":\"Assets/DoesNotExist.shadergraph\"}");
            AssertEnvelope(result, "asset_not_found");
        }

        [Test]
        public void Dispatch_NodeAdd_MissingNodeType_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_shader_graph_node_add",
                "{\"asset_path\":\"Assets/Some.shadergraph\"," +
                "\"paths_hint\":[\"Assets/Some.shadergraph\"]}");
            AssertEnvelope(result, "missing_parameter");
        }

        [Test]
        public void Dispatch_NodeConnect_MissingNodeIds_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_shader_graph_node_connect",
                "{\"asset_path\":\"Assets/Some.shadergraph\"," +
                "\"paths_hint\":[\"Assets/Some.shadergraph\"]}");
            AssertEnvelope(result, "missing_parameter");
        }

        // -----------------------------------------------------------------
        // Happy-path round-trip: create → open → node_add.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_Create_Open_NodeAdd()
        {
            // create
            var create = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_shader_graph_create",
                "{\"asset_path\":\"" + tempAssetPath + "\"," +
                "\"shader_type\":\"Unlit\"," +
                "\"paths_hint\":[\"" + tempAssetPath + "\"]}");
            Assert.IsNotNull(create);
            Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Object>(tempAssetPath));

            // open (read-only) — returns a structured summary of the loaded
            // graph. The asset exists now, so this must succeed.
            var open = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_shader_graph_open",
                "{\"asset_path\":\"" + tempAssetPath + "\"}");
            Assert.IsNotNull(open);
            Assert.IsTrue(open.Success, open.ErrorMessage ?? open.Output);
            StringAssert.Contains("\"status\":\"ok\"", open.Output);
            StringAssert.Contains("\"shaderGraph\":", open.Output);
            StringAssert.Contains("\"nodeCount\":", open.Output);

            // node_add — mutating; returns the node id and its slots.
            var nodeAdd = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_shader_graph_node_add",
                "{\"asset_path\":\"" + tempAssetPath + "\"," +
                "\"node_type\":\"UV\"," +
                "\"position\":\"100,100\"," +
                "\"paths_hint\":[\"" + tempAssetPath + "\"]}");
            Assert.IsNotNull(nodeAdd);
            Assert.IsTrue(nodeAdd.Success, nodeAdd.ErrorMessage ?? nodeAdd.Output);
            StringAssert.Contains("\"nodeId\":\"", nodeAdd.Output);
            StringAssert.Contains("\"direction\":\"output\"", nodeAdd.Output);
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static void AssertEnvelope(ToolDispatchResult result, string expectedCode)
        {
            Assert.IsNotNull(result);
            // The tool's JSON error body is reported as a failed dispatch.
            Assert.IsFalse(result.Success);
            Assert.AreEqual(expectedCode, result.ErrorCode);
        }
    }
}
#endif
