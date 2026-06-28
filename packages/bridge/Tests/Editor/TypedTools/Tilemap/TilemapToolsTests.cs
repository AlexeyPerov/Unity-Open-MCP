// M20 Plan 6 / T20.6.3 — Tilemap embedded domain tools EditMode tests.
//
// Gated by UNITY_OPEN_MCP_EXT_TILEMAP via the owning test asmdef's
// defineConstraints, so the suite only compiles + runs when the
// com.unity.2d.tilemap package is present — matching the compile-gate on the
// tool code under test. The create_rule_tile tool additionally inner-guards
// on UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS — the test asserts the
// tilemap_extras_required envelope when extras is absent, and the happy path
// when present.
//
// NOTE on the error-envelope contract: the registry's TryDispatch wraps every
// successful invocation in ToolDispatchResult.Ok(output). A tool that refuses
// (e.g. missing paths_hint) returns an Ok dispatch whose Output carries the
// JSON error envelope `{"error":{"code":...,"message":...}}`. These tests
// therefore assert against Output content for the refusal paths, and against
// Success + Output for the happy paths.
#if UNITY_OPEN_MCP_EXT_TILEMAP
#pragma warning disable CS0618
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.Extensions.Tilemap;

namespace UnityOpenMcpBridge.Tests.Extensions.Tilemap
{
    public class TilemapToolsTests
    {
        // The 5 catalog tool ids this pack must register.
        private static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_tilemap_create",
            "unity_open_mcp_tilemap_set_tile",
            "unity_open_mcp_tilemap_box_fill",
            "unity_open_mcp_tilemap_create_tile_asset",
            "unity_open_mcp_tilemap_create_rule_tile",
        };

        private string tempTileAssetPath;
        private GameObject tempGrid;

        [SetUp]
        public void SetUp()
        {
            tempTileAssetPath = $"Assets/TilemapTestTile_{System.Guid.NewGuid():N}.asset";
            tempGrid = null;
        }

        [TearDown]
        public void TearDown()
        {
            if (tempGrid != null) Object.DestroyImmediate(tempGrid);
            if (!string.IsNullOrEmpty(tempTileAssetPath) &&
                AssetDatabase.LoadAssetAtPath<Object>(tempTileAssetPath) != null)
            {
                AssetDatabase.DeleteAsset(tempTileAssetPath);
            }
        }

        [Test]
        public void Registry_AllFiveToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected tilemap tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_CreateIsMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_tilemap_create", out var create));
            Assert.IsTrue(create.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, create.Lifecycle);
            Assert.AreEqual("tilemap", create.Group);
        }

        [Test]
        public void Registry_AllTilemapToolsBelongToTilemapGroup()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var entry),
                    $"Tool '{id}' not registered.");
                Assert.AreEqual("tilemap", entry.Group,
                    $"Tool '{id}' should belong to the 'tilemap' group.");
            }
        }

        // -----------------------------------------------------------------
        // paths_hint contract — every mutating tool refuses empty scope.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_Create_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_tilemap_create",
                "{\"grid_name\":\"NoHintGrid\"}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("paths_hint_required", result.Output);
        }

        [Test]
        public void Dispatch_SetTile_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_tilemap_set_tile",
                "{\"tile_asset_path\":\"Assets/Foo.asset\"}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("paths_hint_required", result.Output);
        }

        [Test]
        public void Dispatch_BoxFill_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_tilemap_box_fill",
                "{\"tile_asset_path\":\"Assets/Foo.asset\"}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("paths_hint_required", result.Output);
        }

        [Test]
        public void Dispatch_CreateTileAsset_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_tilemap_create_tile_asset",
                "{\"asset_path\":\"Assets/Foo.asset\"}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("paths_hint_required", result.Output);
        }

        [Test]
        public void Dispatch_CreateRuleTile_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_tilemap_create_rule_tile",
                "{\"asset_path\":\"Assets/Foo.asset\"}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("paths_hint_required", result.Output);
        }

        // -----------------------------------------------------------------
        // Validation branches.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_SetTile_MissingTileAssetPath_ReturnsError()
        {
            var go = new GameObject("TilemapNoTile");
            go.AddComponent<Tilemap>();
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_tilemap_set_tile",
                    "{\"instance_id\":" + go.GetInstanceID() + "," +
                    "\"paths_hint\":[\"Assets/NoScene.unity\"]}");
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
        public void Dispatch_SetTile_NoTilemapComponent_ReturnsComponentNotFoundError()
        {
            var go = new GameObject("NoTilemapComponent");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_tilemap_set_tile",
                    "{\"instance_id\":" + go.GetInstanceID() + "," +
                    "\"tile_asset_path\":\"Assets/Foo.asset\"," +
                    "\"paths_hint\":[\"Assets/NoScene.unity\"]}");
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
        public void Dispatch_CreateTileAsset_BadExtension_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_tilemap_create_tile_asset",
                "{\"asset_path\":\"Assets/Foo.prefab\"," +
                "\"paths_hint\":[\"Assets/Foo.prefab\"]}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("invalid_parameter", result.Output);
        }

        // -----------------------------------------------------------------
        // Happy path round-trip: create grid → create tile asset → set tile.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_CreateGrid_CreateTileAsset_SetTile_PaintsCell()
        {
            CreateGrid();
            CreateTileAsset();

            var grid = tempGrid.GetComponent<Grid>();
            var tilemap = tempGrid.GetComponentInChildren<Tilemap>();
            int tilemapId = tilemap.gameObject.GetInstanceID();

            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_tilemap_set_tile",
                "{\"instance_id\":" + tilemapId + "," +
                "\"tile_asset_path\":\"" + tempTileAssetPath + "\"," +
                "\"x\":2,\"y\":3," +
                "\"paths_hint\":[\"Assets/TilemapTest.unity\"]}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);

            var tile = AssetDatabase.LoadAssetAtPath<TileBase>(tempTileAssetPath);
            Assert.AreEqual(tile, tilemap.GetTile(new Vector3Int(2, 3, 0)),
                "Cell (2,3) should hold the created Tile.");
        }

        [Test]
        public void RoundTrip_BoxFill_PaintsRectangle()
        {
            CreateGrid();
            CreateTileAsset();

            var tilemap = tempGrid.GetComponentInChildren<Tilemap>();
            int tilemapId = tilemap.gameObject.GetInstanceID();

            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_tilemap_box_fill",
                "{\"instance_id\":" + tilemapId + "," +
                "\"tile_asset_path\":\"" + tempTileAssetPath + "\"," +
                "\"x1\":0,\"y1\":0,\"x2\":2,\"y2\":2," +
                "\"paths_hint\":[\"Assets/TilemapTest.unity\"]}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
            StringAssert.Contains("\"cellsPainted\":9", result.Output);

            var tile = AssetDatabase.LoadAssetAtPath<TileBase>(tempTileAssetPath);
            for (int x = 0; x <= 2; x++)
                for (int y = 0; y <= 2; y++)
                    Assert.AreEqual(tile, tilemap.GetTile(new Vector3Int(x, y, 0)),
                        $"Cell ({x},{y}) should be painted.");
        }

        // -----------------------------------------------------------------
        // create_rule_tile — extras-guarded. When extras is absent the tool
        // returns tilemap_extras_required; when present it creates the asset.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_CreateRuleTile_ReturnsExtrasErrorOrCreatesAsset()
        {
            var ruleTilePath = $"Assets/TilemapTestRuleTile_{System.Guid.NewGuid():N}.asset";
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_tilemap_create_rule_tile",
                    "{\"asset_path\":\"" + ruleTilePath + "\"," +
                    "\"paths_hint\":[\"" + ruleTilePath + "\"]}");
                Assert.IsNotNull(result);
                Assert.IsTrue(result.Success);
#if UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS
                StringAssert.Contains("\"status\":\"ok\"", result.Output);
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Object>(ruleTilePath));
#else
                StringAssert.Contains("tilemap_extras_required", result.Output);
                Assert.IsNull(AssetDatabase.LoadAssetAtPath<Object>(ruleTilePath));
#endif
            }
            finally
            {
                if (AssetDatabase.LoadAssetAtPath<Object>(ruleTilePath) != null)
                    AssetDatabase.DeleteAsset(ruleTilePath);
            }
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private void CreateGrid()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_tilemap_create",
                "{\"grid_name\":\"TestGrid\"," +
                "\"paths_hint\":[\"Assets/TilemapTest.unity\"]}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);

            var id = ExtractInt(result.Output, "gridInstanceId");
            tempGrid = EditorUtility.InstanceIDToObject(id) as GameObject;
            Assert.IsNotNull(tempGrid);
            Assert.IsNotNull(tempGrid.GetComponent<Grid>());
            Assert.IsNotNull(tempGrid.GetComponentInChildren<Tilemap>());
        }

        private void CreateTileAsset()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_tilemap_create_tile_asset",
                "{\"asset_path\":\"" + tempTileAssetPath + "\"," +
                "\"tile_name\":\"TestTile\"," +
                "\"paths_hint\":[\"" + tempTileAssetPath + "\"]}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<TileBase>(tempTileAssetPath));
        }

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
