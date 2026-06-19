// EditMode tests for the Input System extension pack.
//
// Covers the deterministic contracts that protect the agent surface:
//
//   1. All 7 catalog tools are discovered by BridgeToolRegistry (no core
//      bridge edits — proves the [BridgeToolType] assembly scan works for
//      packs).
//   2. Mutating tools refuse to run without paths_hint (the gate contract).
//   3. Asset create + map add + action add + binding add round-trip —
//      inputsystem_get reflects the resulting structure.
//
// These tests write real .inputactions assets under a temp folder inside the
// demo project so the AssetDatabase importer runs. They clean up after
// themselves. They run in EditMode and stay fast.
using NUnit.Framework;
using UnityEditor;
using UnityOpenMcpBridge;
using UnityOpenMcpExtensions.InputSystem;

namespace UnityOpenMcpExtensions.InputSystem.Tests
{
    public class InputSystemToolsTests
    {
        // The 7 catalog tool ids this pack must register.
        static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_inputsystem_asset_create",
            "unity_open_mcp_inputsystem_actionmap_add",
            "unity_open_mcp_inputsystem_action_add",
            "unity_open_mcp_inputsystem_binding_add",
            "unity_open_mcp_inputsystem_binding_composite_add",
            "unity_open_mcp_inputsystem_controlscheme_add",
            "unity_open_mcp_inputsystem_get",
        };

        // Temp folder for test assets. Under the project root so AssetDatabase
        // can import the .inputactions file.
        const string TempFolder = "Assets/__InputSystemTests";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.CreateFolder("Assets", "__InputSystemTests");
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.DeleteAsset(TempFolder);
            AssetDatabase.Refresh();
        }

        [Test]
        public void Registry_AllSevenToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected inputsystem tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_AssetCreateIsMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_inputsystem_asset_create", out var create));
            Assert.IsTrue(create.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, create.Lifecycle);
        }

        [Test]
        public void Registry_GetIsReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_inputsystem_get", out var get));
            Assert.IsFalse(get.IsMutating);
            Assert.IsTrue(get.ReadOnlyHint);
        }

        // -----------------------------------------------------------------
        // paths_hint contract — every mutating tool refuses empty scope.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_AssetCreate_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_asset_create",
                "{\"asset_path\":\"Assets/Foo.inputactions\"}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("paths_hint_required", result.ErrorCode);
        }

        [Test]
        public void Dispatch_ActionMapAdd_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_actionmap_add",
                "{\"asset_path\":\"Assets/Foo.inputactions\",\"map_name\":\"Player\"}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("paths_hint_required", result.ErrorCode);
        }

        // -----------------------------------------------------------------
        // Invalid path validation (no AssetDatabase write — pure validation).
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_AssetCreate_InvalidExtension_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_asset_create",
                "{\"asset_path\":\"Assets/Foo.txt\",\"paths_hint\":[\"Assets/Foo.txt\"]}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_asset_path", result.ErrorCode);
        }

        [Test]
        public void Dispatch_AssetCreate_NotAssetsRooted_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_asset_create",
                "{\"asset_path\":\"Foo.inputactions\",\"paths_hint\":[\"Foo.inputactions\"]}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_asset_path", result.ErrorCode);
        }

        // -----------------------------------------------------------------
        // Asset create → actionmap add → action add → binding add → get.
        // This is the round-trip the acceptance criteria call out.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_CreateMapActionBinding_GetReflectsState()
        {
            var assetPath = TempFolder + "/Player.inputactions";

            // 1. Create the asset with an initial map.
            var createBody = "{\"asset_path\":\"" + assetPath + "\"," +
                             "\"initial_action_map\":\"Player\"," +
                             "\"paths_hint\":[\"" + assetPath + "\"]}";
            var create = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_asset_create", createBody);
            Assert.IsNotNull(create, "Tool must be registered.");
            Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);
            StringAssert.Contains("\"status\":\"ok\"", create.Output);
            StringAssert.Contains("\"actionMapCount\":1", create.Output);

            // 2. Add a second ActionMap.
            var mapBody = "{\"asset_path\":\"" + assetPath + "\"," +
                          "\"map_name\":\"UI\"," +
                          "\"paths_hint\":[\"" + assetPath + "\"]}";
            var mapAdd = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_actionmap_add", mapBody);
            Assert.IsTrue(mapAdd.Success, mapAdd.ErrorMessage ?? mapAdd.Output);
            StringAssert.Contains("\"actionMapCount\":2", mapAdd.Output);

            // 3. Add an Action to the Player map with an initial binding.
            var actionBody = "{\"asset_path\":\"" + assetPath + "\"," +
                             "\"map_name\":\"Player\"," +
                             "\"action_name\":\"Jump\"," +
                             "\"action_type\":\"Button\"," +
                             "\"binding\":\"<Keyboard>/space\"," +
                             "\"paths_hint\":[\"" + assetPath + "\"]}";
            var actionAdd = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_action_add", actionBody);
            Assert.IsTrue(actionAdd.Success, actionAdd.ErrorMessage ?? actionAdd.Output);
            StringAssert.Contains("\"actionName\":\"Jump\"", actionAdd.Output);
            StringAssert.Contains("\"bindingCount\":1", actionAdd.Output);

            // 4. Add a second simple binding to the same action.
            var bindingBody = "{\"asset_path\":\"" + assetPath + "\"," +
                              "\"map_name\":\"Player\"," +
                              "\"action_name\":\"Jump\"," +
                              "\"path\":\"<Gamepad>/buttonSouth\"," +
                              "\"paths_hint\":[\"" + assetPath + "\"]}";
            var bindingAdd = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_binding_add", bindingBody);
            Assert.IsTrue(bindingAdd.Success, bindingAdd.ErrorMessage ?? bindingAdd.Output);
            StringAssert.Contains("\"bindingCount\":2", bindingAdd.Output);

            // 5. inputsystem_get reflects the resulting structure.
            var getBody = "{\"asset_path\":\"" + assetPath + "\"}";
            var get = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_get", getBody);
            Assert.IsTrue(get.Success, get.ErrorMessage ?? get.Output);
            StringAssert.Contains("\"actionMaps\":[", get.Output);
            StringAssert.Contains("\"name\":\"Player\"", get.Output);
            StringAssert.Contains("\"name\":\"UI\"", get.Output);
            StringAssert.Contains("\"name\":\"Jump\"", get.Output);
            StringAssert.Contains("<Keyboard>/space", get.Output);
            StringAssert.Contains("<Gamepad>/buttonSouth", get.Output);
        }

        [Test]
        public void RoundTrip_CompositeBinding_AddsParts()
        {
            var assetPath = TempFolder + "/Move.inputactions";

            // Create + map + Move action.
            Assert.IsTrue(BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_asset_create",
                "{\"asset_path\":\"" + assetPath + "\",\"initial_action_map\":\"Player\"," +
                "\"paths_hint\":[\"" + assetPath + "\"]}").Success);

            Assert.IsTrue(BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_action_add",
                "{\"asset_path\":\"" + assetPath + "\",\"map_name\":\"Player\"," +
                "\"action_name\":\"Move\",\"action_type\":\"Value\"," +
                "\"expected_control_type\":\"Vector2\"," +
                "\"paths_hint\":[\"" + assetPath + "\"]}").Success);

            // Add a 2DVector WASD composite.
            var compositeBody = "{\"asset_path\":\"" + assetPath + "\"," +
                                "\"map_name\":\"Player\"," +
                                "\"action_name\":\"Move\"," +
                                "\"composite\":\"2DVector\"," +
                                "\"parts_json\":\"[{\\\"name\\\":\\\"up\\\",\\\"path\\\":\\\"<Keyboard>/w\\\"},{\\\"name\\\":\\\"down\\\",\\\"path\\\":\\\"<Keyboard>/s\\\"},{\\\"name\\\":\\\"left\\\",\\\"path\\\":\\\"<Keyboard>/a\\\"},{\\\"name\\\":\\\"right\\\",\\\"path\\\":\\\"<Keyboard>/d\\\"}]\"," +
                                "\"paths_hint\":[\"" + assetPath + "\"]}";
            var composite = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_binding_composite_add", compositeBody);
            Assert.IsTrue(composite.Success, composite.ErrorMessage ?? composite.Output);
            StringAssert.Contains("\"partCount\":4", composite.Output);
            // Composite root + 4 parts = 5 bindings.
            StringAssert.Contains("\"bindingCount\":5", composite.Output);

            // get reflects the composite.
            var get = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_get",
                "{\"asset_path\":\"" + assetPath + "\"}");
            Assert.IsTrue(get.Success, get.ErrorMessage ?? get.Output);
            StringAssert.Contains("\"isComposite\":true", get.Output);
            StringAssert.Contains("\"isPartOfComposite\":true", get.Output);
        }

        [Test]
        public void RoundTrip_ControlScheme_AddsToAsset()
        {
            var assetPath = TempFolder + "/Scheme.inputactions";

            Assert.IsTrue(BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_asset_create",
                "{\"asset_path\":\"" + assetPath + "\"," +
                "\"paths_hint\":[\"" + assetPath + "\"]}").Success);

            var schemeBody = "{\"asset_path\":\"" + assetPath + "\"," +
                             "\"scheme_name\":\"KeyboardMouse\"," +
                             "\"required_devices\":[\"<Keyboard>\",\"<Mouse>\"]," +
                             "\"paths_hint\":[\"" + assetPath + "\"]}";
            var scheme = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_controlscheme_add", schemeBody);
            Assert.IsTrue(scheme.Success, scheme.ErrorMessage ?? scheme.Output);
            StringAssert.Contains("\"requiredDeviceCount\":2", scheme.Output);
            StringAssert.Contains("\"controlSchemeCount\":1", scheme.Output);

            var get = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_get",
                "{\"asset_path\":\"" + assetPath + "\"}");
            Assert.IsTrue(get.Success, get.ErrorMessage ?? get.Output);
            StringAssert.Contains("\"controlSchemes\":[", get.Output);
            StringAssert.Contains("\"KeyboardMouse\"", get.Output);
        }

        // -----------------------------------------------------------------
        // Idempotency / not-found branches.
        // -----------------------------------------------------------------

        [Test]
        public void ActionMapAdd_DuplicateMap_ReturnsAlreadyExists()
        {
            var assetPath = TempFolder + "/Dup.inputactions";

            Assert.IsTrue(BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_asset_create",
                "{\"asset_path\":\"" + assetPath + "\",\"initial_action_map\":\"Player\"," +
                "\"paths_hint\":[\"" + assetPath + "\"]}").Success);

            var dup = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_actionmap_add",
                "{\"asset_path\":\"" + assetPath + "\",\"map_name\":\"Player\"," +
                "\"paths_hint\":[\"" + assetPath + "\"]}");
            Assert.IsFalse(dup.Success);
            Assert.AreEqual("actionmap_already_exists", dup.ErrorCode);
        }

        [Test]
        public void ActionAdd_MissingMap_ReturnsActionMapNotFound()
        {
            var assetPath = TempFolder + "/MissingMap.inputactions";

            Assert.IsTrue(BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_asset_create",
                "{\"asset_path\":\"" + assetPath + "\"," +
                "\"paths_hint\":[\"" + assetPath + "\"]}").Success);

            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_action_add",
                "{\"asset_path\":\"" + assetPath + "\",\"map_name\":\"Nope\"," +
                "\"action_name\":\"Jump\"," +
                "\"paths_hint\":[\"" + assetPath + "\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("actionmap_not_found", result.ErrorCode);
        }

        [Test]
        public void Get_AssetNotFound_ReturnsAssetNotFound()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_inputsystem_get",
                "{\"asset_path\":\"" + TempFolder + "/Missing.inputactions\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("asset_not_found", result.ErrorCode);
        }
    }
}
