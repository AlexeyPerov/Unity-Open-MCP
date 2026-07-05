// M20 Plan 7 / T20.7.2 — VFX Graph embedded domain tools EditMode tests.
//
// Gated by UNITY_OPEN_MCP_EXT_VFX via the owning test asmdef's
// defineConstraints, so the suite only compiles + runs when the
// com.unity.visualeffectgraph package is present — matching the compile-gate
// on the tool code under test. When the package is absent neither the tools
// nor this suite compile (mirrors the Timeline/Tilemap test-gating model).
//
// Coverage shape:
//   - registry discovery (all 3 ids) + group membership + lifecycle/gate hints
//   - deterministic error-envelope paths (always assert): paths_hint_required,
//     missing_parameter, invalid_parameter, asset_not_found
//   - happy-path `list` (deterministic — AssetDatabase.FindAssets over the
//     project, returns a valid envelope whether or not any .vfx exists)
//
// What is deliberately NOT covered here: a full `open` / `block_edit` round-
// trip against a real .vfx asset. Authoring a valid .vfx in EditMode is not
// feasible — the serialized graph format is package-internal and complex
// (which is exactly why even the competitor ships only list/open for VFX).
// The mutating `block_edit` path additionally requires the VFX Graph window
// to be open and degrades to a structured `vfx_block_edit_requires_editor_window`
// error otherwise; that live-Editor flow stays manual (see the manual
// checklist). The deterministic refusal paths below still pin the gate/model
// surface regardless of the host environment.
//
// NOTE on the error-envelope contract: the registry's TryDispatch wraps every
// successful invocation in ToolDispatchResult.Ok(output). A tool that refuses
// (e.g. missing paths_hint) returns an Ok dispatch whose Output carries the
// JSON error envelope `{"error":{"code":...,"message":...}}`. These tests
// therefore assert against Output content for the refusal paths, and against
// Success + Output for the happy paths.
#if UNITY_OPEN_MCP_EXT_VFX
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Tests.Extensions.VFXExt
{
    public class VFXToolsTests
    {
        // The 3 catalog tool ids this pack must register.
        private static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_vfx_list",
            "unity_open_mcp_vfx_open",
            "unity_open_mcp_vfx_block_edit",
        };

        // -----------------------------------------------------------------
        // Registry discovery + metadata.
        // -----------------------------------------------------------------

        [Test]
        public void Registry_AllThreeToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected vfx tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_AllVfxToolsBelongToVfxGroup()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var entry),
                    $"Tool '{id}' not registered.");
                Assert.AreEqual("vfx", entry.Group,
                    $"Tool '{id}' should belong to the 'vfx' group.");
            }
        }

        [Test]
        public void Registry_BlockEditIsMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_vfx_block_edit", out var edit));
            Assert.IsTrue(edit.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, edit.Lifecycle);
            Assert.AreEqual(GateMode.Enforce, edit.Gate);
        }

        [Test]
        public void Registry_ListIsReadOnlyAndGateOff()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_vfx_list", out var list));
            Assert.IsFalse(list.IsMutating);
            Assert.IsTrue(list.ReadOnlyHint);
            Assert.AreEqual(GateMode.Off, list.Gate);
            Assert.AreEqual(LifecyclePolicy.None, list.Lifecycle);
        }

        [Test]
        public void Registry_OpenIsReadOnlyAndGateOff()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_vfx_open", out var open));
            Assert.IsFalse(open.IsMutating);
            Assert.IsTrue(open.ReadOnlyHint);
            Assert.AreEqual(GateMode.Off, open.Gate);
            Assert.AreEqual(LifecyclePolicy.None, open.Lifecycle);
        }

        // -----------------------------------------------------------------
        // paths_hint contract — the mutating tool refuses empty scope.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_BlockEdit_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_vfx_block_edit",
                "{\"asset_path\":\"Assets/Some.vfx\"," +
                "\"block_selector\":\"SetVelocity\",\"property\":\"Speed\"}");
            AssertEnvelope(result, "paths_hint_required");
        }

        // -----------------------------------------------------------------
        // Validation branches (always deterministic).
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_BlockEdit_MissingAssetPath_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_vfx_block_edit",
                "{\"paths_hint\":[\"Assets/Some.vfx\"]}");
            AssertEnvelope(result, "missing_parameter");
        }

        [Test]
        public void Dispatch_BlockEdit_BadExtension_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_vfx_block_edit",
                "{\"asset_path\":\"Assets/NotAVfx.mat\"," +
                "\"paths_hint\":[\"Assets/NotAVfx.mat\"]}");
            AssertEnvelope(result, "invalid_parameter");
        }

        [Test]
        public void Dispatch_BlockEdit_MissingBlockSelector_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_vfx_block_edit",
                "{\"asset_path\":\"Assets/Some.vfx\"," +
                "\"property\":\"Speed\"," +
                "\"paths_hint\":[\"Assets/Some.vfx\"]}");
            AssertEnvelope(result, "missing_parameter");
        }

        [Test]
        public void Dispatch_BlockEdit_MissingProperty_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_vfx_block_edit",
                "{\"asset_path\":\"Assets/Some.vfx\"," +
                "\"block_selector\":\"SetVelocity\"," +
                "\"paths_hint\":[\"Assets/Some.vfx\"]}");
            AssertEnvelope(result, "missing_parameter");
        }

        [Test]
        public void Dispatch_BlockEdit_MissingValueJson_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_vfx_block_edit",
                "{\"asset_path\":\"Assets/Some.vfx\"," +
                "\"block_selector\":\"SetVelocity\",\"property\":\"Speed\"," +
                "\"paths_hint\":[\"Assets/Some.vfx\"]}");
            AssertEnvelope(result, "missing_parameter");
        }

        [Test]
        public void Dispatch_Open_MissingAssetPath_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_vfx_open", "{}");
            AssertEnvelope(result, "missing_parameter");
        }

        [Test]
        public void Dispatch_Open_AssetNotFound_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_vfx_open",
                "{\"asset_path\":\"Assets/DoesNotExist.vfx\"}");
            AssertEnvelope(result, "asset_not_found");
        }

        // -----------------------------------------------------------------
        // Happy path — list is deterministic. It uses AssetDatabase.FindAssets
        // over the project and returns a valid envelope whether or not any
        // .vfx asset exists (count may be 0). This pins the read path that
        // works over the public runtime VisualEffectAsset type.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_List_ReturnsValidEnvelope()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_vfx_list", "{}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"vfxList\":", result.Output);
            StringAssert.Contains("\"count\":", result.Output);
            StringAssert.Contains("\"assets\":", result.Output);
        }

        [Test]
        public void Dispatch_List_WithFilterAndCap_ReturnsValidEnvelope()
        {
            // A filter that matches nothing still returns a valid envelope with
            // count 0; the filter is echoed back. max_results is honored.
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_vfx_list",
                "{\"filter\":\"__NoMatchFilter__\",\"max_results\":5}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"count\":0", result.Output);
            StringAssert.Contains("__NoMatchFilter__", result.Output);
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static void AssertEnvelope(ToolDispatchResult result, string expectedCode)
        {
            Assert.IsNotNull(result);
            // invocation succeeded; the refusal is carried in Output as a JSON
            // error envelope.
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"error\"", result.Output);
            StringAssert.Contains(expectedCode, result.Output);
        }
    }
}
#endif
