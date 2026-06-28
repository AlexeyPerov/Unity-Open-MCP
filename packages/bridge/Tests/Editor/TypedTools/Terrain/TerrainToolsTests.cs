// M20 Plan 4 / T20.4 — Terrain embedded domain tools EditMode tests.
//
// Ungated (no UNITY_OPEN_MCP_EXT_TERRAIN): the Terrain / TerrainData /
// TreePrototype / TerrainLayer types (UnityEngine.TerrainModule +
// UnityEngine.CoreModule) are built-in engine types present on every Unity
// install, so the tools — and this suite — compile unconditionally. The test
// asmdef only constrains UNITY_TEST_FRAMEWORK.
#pragma warning disable CS0618
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests.Extensions.Terrain
{
    public class TerrainToolsTests
    {
        // The 5 catalog tool ids this domain must register.
        private static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_terrain_create",
            "unity_open_mcp_terrain_set_heights",
            "unity_open_mcp_terrain_paint_layer",
            "unity_open_mcp_terrain_place_trees",
            "unity_open_mcp_terrain_set_neighbors",
        };

        // -----------------------------------------------------------------
        // Registry discovery + group assignment.
        // -----------------------------------------------------------------

        [Test]
        public void Registry_AllFiveToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected terrain tool '{id}' to be discovered by BridgeToolRegistry.");
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
        public void Registry_AllToolsAssignedToTerrainGroup()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var info));
                Assert.AreEqual("terrain", info.Group,
                    $"Expected '{id}' to be in the 'terrain' group.");
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
        public void Dispatch_TerrainCreate_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_terrain_create",
                "{\"terrain_name\":\"T\"}");
            AssertErrorEnvelope(result, "paths_hint_required");
        }

        [Test]
        public void Dispatch_TerrainSetHeights_MissingPathsHint_ReturnsError()
        {
            var go = new GameObject("TerrainSetHeightsNoHint");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_set_heights",
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"heights\":[[0.5]]}");
                AssertErrorEnvelope(result, "paths_hint_required");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_TerrainPaintLayer_MissingPathsHint_ReturnsError()
        {
            var go = new GameObject("TerrainPaintNoHint");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_paint_layer",
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"layer_index\":0,\"alphamap\":[[1]]}");
                AssertErrorEnvelope(result, "paths_hint_required");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_TerrainPlaceTrees_MissingPathsHint_ReturnsError()
        {
            var go = new GameObject("TerrainTreesNoHint");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_place_trees",
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"tree_prototype_index\":0," +
                    "\"instances\":[{\"position\":\"0.5,0.5\"}]}");
                AssertErrorEnvelope(result, "paths_hint_required");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_TerrainSetNeighbors_MissingPathsHint_ReturnsError()
        {
            var go = new GameObject("TerrainNeighborsNoHint");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_set_neighbors",
                    "{\"instance_id\":" + go.GetInstanceID() + "}");
                AssertErrorEnvelope(result, "paths_hint_required");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Validation branches.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_TerrainCreate_InvalidHeightmapResolution_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_terrain_create",
                "{\"terrain_name\":\"T\",\"heightmap_resolution\":130," +
                "\"paths_hint\":[\"Assets/T.unity\"]}");
            AssertErrorEnvelope(result, "invalid_heightmap_resolution");
        }

        [Test]
        public void Dispatch_TerrainCreate_NonPositiveSize_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_terrain_create",
                "{\"terrain_name\":\"T\",\"width\":0," +
                "\"paths_hint\":[\"Assets/T.unity\"]}");
            AssertErrorEnvelope(result, "invalid_size");
        }

        [Test]
        public void Dispatch_TerrainSetHeights_OnNonTerrain_ReturnsComponentNotFound()
        {
            var go = new GameObject("TerrainSetHeightsNoTerrain");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_set_heights",
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"heights\":[[0.5]],\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "component_not_found");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_TerrainSetHeights_InvalidArray_ReturnsError()
        {
            var terrain = CreateTestTerrain("TerrainSetHeightsBadArray");
            try
            {
                // Ragged array — row 0 has 2 cols, row 1 has 1.
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_set_heights",
                    "{\"instance_id\":" + terrain.gameObject.GetInstanceID() +
                    ",\"heights\":[[0.5,0.5],[0.5]]," +
                    "\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "invalid_heights_array");
            }
            finally
            {
                DestroyTerrain(terrain);
            }
        }

        [Test]
        public void Dispatch_TerrainSetHeights_RegionOutOfBounds_ReturnsError()
        {
            var terrain = CreateTestTerrain("TerrainSetHeightsOob");
            try
            {
                // 129x129 heightmap; writing 2 cells starting at 128 overflows.
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_set_heights",
                    "{\"instance_id\":" + terrain.gameObject.GetInstanceID() +
                    ",\"x_offset\":128,\"y_offset\":128,\"heights\":[[0.5,0.5],[0.5,0.5]]," +
                    "\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "region_out_of_bounds");
            }
            finally
            {
                DestroyTerrain(terrain);
            }
        }

        [Test]
        public void Dispatch_TerrainPlaceTrees_NewPrototypeWithoutPrefab_ReturnsError()
        {
            var terrain = CreateTestTerrain("TerrainTreesNoPrefab");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_place_trees",
                    "{\"instance_id\":" + terrain.gameObject.GetInstanceID() +
                    ",\"tree_prototype_index\":0," +
                    "\"instances\":[{\"position\":\"0.5,0.5\"}]," +
                    "\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "missing_parameter");
            }
            finally
            {
                DestroyTerrain(terrain);
            }
        }

        [Test]
        public void Dispatch_TerrainSetNeighbors_NeighborNotTerrain_ReturnsError()
        {
            var terrain = CreateTestTerrain("TerrainNeighborsHost");
            var notTerrain = new GameObject("TerrainNeighborsNotTerrain");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_set_neighbors",
                    "{\"instance_id\":" + terrain.gameObject.GetInstanceID() +
                    ",\"top_instance_id\":" + notTerrain.GetInstanceID() +
                    ",\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "neighbor_not_terrain");
            }
            finally
            {
                DestroyTerrain(terrain);
                Object.DestroyImmediate(notTerrain);
            }
        }

        // -----------------------------------------------------------------
        // Large-array guard — the catalog minimum's tiling hint.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_TerrainSetHeights_OversizedArray_ReturnsTilingHint()
        {
            var terrain = CreateTestTerrain("TerrainSetHeightsOversized");
            try
            {
                // Build a 514x514 array — exceeds the 513 per-side cap.
                var sb = new System.Text.StringBuilder();
                sb.Append('[');
                for (int y = 0; y < 514; y++)
                {
                    if (y > 0) sb.Append(',');
                    sb.Append('[');
                    for (int x = 0; x < 514; x++)
                    {
                        if (x > 0) sb.Append(',');
                        sb.Append("0.5");
                    }
                    sb.Append(']');
                }
                sb.Append(']');
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_set_heights",
                    "{\"instance_id\":" + terrain.gameObject.GetInstanceID() +
                    ",\"heights\":" + sb +
                    ",\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "invalid_heights_array");
                StringAssert.Contains("tiles", result.Output ?? result.ErrorMessage ?? "");
            }
            finally
            {
                DestroyTerrain(terrain);
            }
        }

        // -----------------------------------------------------------------
        // Round-trip: create → set heights → paint layer → place trees →
        // set neighbors. The acceptance criterion calls for all 5 working
        // end-to-end on a 129×129 test terrain.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_TerrainCreate_InSceneOnly_Works()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_terrain_create",
                "{\"terrain_name\":\"RoundTripTerrain\"," +
                "\"heightmap_resolution\":129,\"width\":100,\"length\":100,\"height\":50," +
                "\"paths_hint\":[\"Assets/T.unity\"]}");
            Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
            try
            {
                StringAssert.Contains("\"created\":true", result.Output);
                StringAssert.Contains("\"heightmapResolution\":129", result.Output);
                StringAssert.Contains("\"assetWritten\":false", result.Output);
                var id = ExtractInstanceId(result.Output);
                Assert.AreNotEqual(0, id);
                var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                Assert.IsNotNull(go);
                var terrain = go.GetComponent<Terrain>();
                Assert.IsNotNull(terrain);
                Assert.AreEqual(129, terrain.terrainData.heightmapResolution);
            }
            finally
            {
                var id = ExtractInstanceId(result.Output);
                if (id != 0)
                {
                    var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                    if (go != null) Object.DestroyImmediate(go);
                }
            }
        }

        [Test]
        public void RoundTrip_TerrainCreate_WithAsset_WritesTerrainDataAsset()
        {
            const string TmpDir = "Assets/TmpTerrainTests";
            const string AssetPath = TmpDir + "/RoundTripTerrain.asset";
            try
            {
                if (UnityEditor.AssetDatabase.LoadMainAssetAtPath(AssetPath) != null)
                    UnityEditor.AssetDatabase.DeleteAsset(AssetPath);

                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_create",
                    "{\"terrain_name\":\"AssetTerrain\",\"heightmap_resolution\":129," +
                    "\"asset_path\":\"" + AssetPath + "\"," +
                    "\"paths_hint\":[\"Assets/T.unity\",\"" + AssetPath + "\"]}");
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"assetWritten\":true", result.Output);
                StringAssert.Contains("\"assetPath\":\"" + AssetPath + "\"", result.Output);
                var td = UnityEditor.AssetDatabase.LoadAssetAtPath<TerrainData>(AssetPath);
                Assert.IsNotNull(td, "terrain_create should create the .asset.");
            }
            finally
            {
                if (UnityEditor.AssetDatabase.IsValidFolder(TmpDir))
                    UnityEditor.AssetDatabase.DeleteAsset(TmpDir);
            }
        }

        [Test]
        public void RoundTrip_TerrainSetHeights_WritesRegionAndClamps()
        {
            var terrain = CreateTestTerrain("TerrainSetHeightsRoundTrip");
            try
            {
                // 3x3 region of 0.5 (and one out-of-range value to test clamp).
                var body = "{\"instance_id\":" + terrain.gameObject.GetInstanceID() +
                           ",\"x_offset\":10,\"y_offset\":20," +
                           "\"heights\":[[0.5,0.6,0.7],[1.5,-0.1,0.3],[0.2,0.4,0.8]]," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_set_heights", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"written\":true", result.Output);
                StringAssert.Contains("\"width\":3", result.Output);
                StringAssert.Contains("\"height\":3", result.Output);

                // Read back the heightmap region — the 1.5 must clamp to 1.0
                // and the -0.1 to 0.0.
                var td = terrain.terrainData;
                var got = td.GetHeights(10, 20, 3, 3);
                Assert.AreEqual(0.5f, got[0, 0]);
                Assert.AreEqual(0.6f, got[0, 1]);
                Assert.AreEqual(0.7f, got[0, 2]);
                Assert.AreEqual(1.0f, got[1, 0], "1.5 should clamp to 1.0");
                Assert.AreEqual(0.0f, got[1, 1], "-0.1 should clamp to 0.0");
                Assert.AreEqual(0.3f, got[1, 2]);
                Assert.AreEqual(0.2f, got[2, 0]);
                Assert.AreEqual(0.4f, got[2, 1]);
                Assert.AreEqual(0.8f, got[2, 2]);
            }
            finally
            {
                DestroyTerrain(terrain);
            }
        }

        [Test]
        public void RoundTrip_TerrainPaintLayer_AddsLayerAndPaintsWeights()
        {
            var terrain = CreateTestTerrain("TerrainPaintLayerRoundTrip");
            try
            {
                var body = "{\"instance_id\":" + terrain.gameObject.GetInstanceID() +
                           ",\"layer_index\":0,\"x_offset\":0,\"y_offset\":0," +
                           "\"alphamap\":[[1,0.5],[0.25,0.75]]," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_paint_layer", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"painted\":true", result.Output);
                StringAssert.Contains("\"layerCount\":1", result.Output);

                var td = terrain.terrainData;
                Assert.AreEqual(1, td.alphamapLayers, "Should have 1 layer now.");
                var got = td.GetAlphamaps(0, 0, 2, 2);
                Assert.AreEqual(1.0f, got[0, 0, 0]);
                Assert.AreEqual(0.5f, got[0, 1, 0]);
                Assert.AreEqual(0.25f, got[1, 0, 0]);
                Assert.AreEqual(0.75f, got[1, 1, 0]);
            }
            finally
            {
                DestroyTerrain(terrain);
            }
        }

        [Test]
        public void RoundTrip_TerrainPaintLayer_NewLayerWithAssetPath_CreatesLayer()
        {
            const string TmpDir = "Assets/TmpTerrainTests";
            const string LayerPath = TmpDir + "/TestLayer.terrainlayer";
            var terrain = CreateTestTerrain("TerrainPaintLayerAsset");
            try
            {
                if (UnityEditor.AssetDatabase.LoadMainAssetAtPath(LayerPath) != null)
                    UnityEditor.AssetDatabase.DeleteAsset(LayerPath);

                var body = "{\"instance_id\":" + terrain.gameObject.GetInstanceID() +
                           ",\"layer_index\":0,\"alphamap\":[[1]]," +
                           "\"layer_path\":\"" + LayerPath + "\"," +
                           "\"paths_hint\":[\"Assets/T.unity\",\"" + LayerPath + "\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_paint_layer", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"layerAdded\":true", result.Output);
                StringAssert.Contains("\"createdLayerPath\":\"" + LayerPath + "\"", result.Output);
                var layer = UnityEditor.AssetDatabase.LoadAssetAtPath<TerrainLayer>(LayerPath);
                Assert.IsNotNull(layer, "paint_layer should create the .terrainlayer.");
            }
            finally
            {
                DestroyTerrain(terrain);
                if (UnityEditor.AssetDatabase.IsValidFolder(TmpDir))
                    UnityEditor.AssetDatabase.DeleteAsset(TmpDir);
            }
        }

        [Test]
        public void RoundTrip_TerrainPlaceTrees_SeedsPrototypeAndPlacesInstances()
        {
            const string TmpDir = "Assets/TmpTerrainTests";
            const string PrefabPath = TmpDir + "/TreePrefab.prefab";
            var terrain = CreateTestTerrain("TerrainTreesRoundTrip");
            try
            {
                // Build a throwaway prefab to seed the prototype.
                if (UnityEditor.AssetDatabase.LoadMainAssetAtPath(PrefabPath) != null)
                    UnityEditor.AssetDatabase.DeleteAsset(PrefabPath);
                var prefabSrc = new GameObject("TreeSrc");
                prefabSrc.AddComponent<MeshRenderer>();
                UnityEditor.PrefabUtility.SaveAsPrefabAsset(prefabSrc, PrefabPath);
                Object.DestroyImmediate(prefabSrc);

                var body = "{\"instance_id\":" + terrain.gameObject.GetInstanceID() +
                           ",\"tree_prototype_index\":0," +
                           "\"prototype_prefab_path\":\"" + PrefabPath + "\"," +
                           "\"instances\":[" +
                           "{\"position\":\"0.5,0.5\",\"height_scale\":1.2,\"width_scale\":0.8,\"rotation\":45}," +
                           "{\"position\":\"0.6,0.4\"}" +
                           "],\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_place_trees", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"placedCount\":2", result.Output);
                StringAssert.Contains("\"prototypeCount\":1", result.Output);
                StringAssert.Contains("\"prototypeAdded\":true", result.Output);
                StringAssert.Contains("\"totalTreeCount\":2", result.Output);

                var td = terrain.terrainData;
                Assert.AreEqual(1, td.treePrototypes.Length);
                Assert.AreEqual(2, td.treeInstanceCount);
                // Verify the first instance's prototype index + rotation.
                var inst0 = td.GetTreeInstance(0);
                Assert.AreEqual(0, inst0.prototypeIndex);
                Assert.AreEqual(45f * Mathf.Deg2Rad, inst0.rotation, 0.001f);
            }
            finally
            {
                DestroyTerrain(terrain);
                if (UnityEditor.AssetDatabase.IsValidFolder(TmpDir))
                    UnityEditor.AssetDatabase.DeleteAsset(TmpDir);
            }
        }

        [Test]
        public void RoundTrip_TerrainSetNeighbors_WiresAndClears()
        {
            var host = CreateTestTerrain("TerrainNeighborsHost");
            var right = CreateTestTerrain("TerrainNeighborsRight");
            try
            {
                // Wire the right neighbor.
                var body = "{\"instance_id\":" + host.gameObject.GetInstanceID() +
                           ",\"right_instance_id\":" + right.gameObject.GetInstanceID() +
                           ",\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_set_neighbors", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"right\":\"TerrainNeighborsRight\"", result.Output);
                Assert.AreEqual(right, host.rightNeighbor);

                // Clear the right neighbor (omit the resolver).
                var clearBody = "{\"instance_id\":" + host.gameObject.GetInstanceID() +
                                ",\"paths_hint\":[\"Assets/T.unity\"]}";
                var clearResult = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_terrain_set_neighbors", clearBody);
                Assert.IsTrue(clearResult.Success, clearResult.ErrorMessage ?? clearResult.Output);
                // After clearing all four, no neighbor should be wired.
                Assert.IsNull(host.rightNeighbor);
            }
            finally
            {
                DestroyTerrain(host);
                DestroyTerrain(right);
            }
        }

        // -----------------------------------------------------------------
        // Helpers.
        // -----------------------------------------------------------------

        // Build a 129x129 in-scene terrain (no asset) for the round-trip tests.
        // We construct it directly (not via the tool) so a failure in
        // terrain_create does not cascade into the set_heights / paint_layer /
        // place_trees / set_neighbors tests.
        private static Terrain CreateTestTerrain(string name)
        {
            var td = ScriptableObject.CreateInstance<TerrainData>();
            td.heightmapResolution = 129;
            td.size = new Vector3(100, 50, 100);
            td.alphamapResolution = 129;
            td.baseMapResolution = 129;
            var go = Terrain.CreateTerrainGameObject(td);
            go.name = name;
            return go.GetComponent<Terrain>();
        }

        private static void DestroyTerrain(Terrain terrain)
        {
            if (terrain == null) return;
            var go = terrain.gameObject;
            var td = terrain.terrainData;
            Object.DestroyImmediate(go);
            // TerrainData is a separate ScriptableObject; clean it up so the
            // tests do not leak across runs.
            if (td != null && UnityEditor.AssetDatabase.GetAssetPath(td) == "")
                Object.DestroyImmediate(td);
        }

        // Extract the instanceId from a tool output JSON envelope. Returns 0
        // when not found (the caller treats 0 as "no terrain to clean up").
        private static int ExtractInstanceId(string output)
        {
            if (string.IsNullOrEmpty(output)) return 0;
            var key = "\"instanceId\":";
            var idx = output.IndexOf(key, System.StringComparison.Ordinal);
            if (idx < 0) return 0;
            int start = idx + key.Length;
            int end = start;
            while (end < output.Length &&
                   (char.IsDigit(output[end]) || output[end] == '-'))
                end++;
            int.TryParse(output.Substring(start, end - start), out var id);
            return id;
        }
    }
}
