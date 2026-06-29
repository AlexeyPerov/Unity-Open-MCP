// M20 Plan 4 / T20.4 — Terrain embedded domain tools.
//
// Five typed tools covering the heightmap / splatmap / tree / neighbor
//
//   terrain_create        — allocate TerrainData (+ optional .asset) and a
//                           Terrain GameObject (width / length / height /
//                           heightmapResolution / position / dataPath).
//   terrain_set_heights   — set a heightmap region from a 2D array of
//                           normalized 0-1 values (xBase / yBase + 2D heights).
//   terrain_paint_layer   — paint one layer's splat from a 2D alphamap. When
//                           the layer index is new, optionally seed a new
//                           TerrainLayer asset (or load one by path).
//   terrain_place_trees   — place tree instances (position + scale + rotation)
//                           against a prototype index. When the prototype
//                           index is new, optionally seed a prototype from a
//                           prefab path.
//   terrain_set_neighbors — set the top / bottom / left / right neighbor
//                           Terrains for LOD stitching.
//
// The Terrain / TerrainData / TreePrototype / TerrainLayer types live in the
// built-in engine modules (UnityEngine.TerrainModule + UnityEngine.CoreModule)
// and are present in every Unity install, so this domain ships UNGATED — no
// UNITY_OPEN_MCP_EXT_TERRAIN define. The `terrain` tool group is still hidden
// from ListTools until the session activates it via
// unity_open_mcp_manage_tools (group visibility is a session concern,
// independent of compile-gating).
//
// Large arrays: heightmap + splat arrays can be large. The 2D-array parser
// (TerrainArrays) refuses any side larger than 513 per call with a clear
// tiling hint pointing the agent at x_offset / y_offset region writes.
//
// Naming: `unity_open_mcp_terrain_<action>` (snake_case domain prefix).
#pragma warning disable CS0618
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Extensions.Terrain
{
    // M20 Plan 4 / T20.4 — Terrain tools. Registry-discovered via
    // [BridgeToolType] + [BridgeTool]. All five tools are mutating (creating a
    // terrain, writing heights, painting splat, placing trees, and stitching
    // neighbors all write scene / asset state) and declare IsMutating = true
    // with a snake_case paths_hint (bound to the C# pathsHint parameter by
    // name) so the gate can scope the verify checkpoint.
    [BridgeToolType]
    public static class TerrainTools
    {
        // =====================================================================
        // Terrain — create (TerrainData + Terrain GameObject)
        // =====================================================================

        // Allocate a TerrainData (optionally saved as a .asset) and a Terrain
        // GameObject carrying it. When asset_path is provided, the TerrainData
        // is written under Assets/... and added to paths_hint so the gate
        // validates both the asset and the scene mutation (width / length /
        // height / heightmapResolution / position / dataPath).
        [BridgeTool("unity_open_mcp_terrain_create",
            Title = "Terrain: Create Terrain",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "terrain")]
        [System.ComponentModel.Description(
            "Create a Terrain (allocates TerrainData + a Terrain GameObject). " +
            "Optional: width (X size, default 500), length (Z size, default 500), " +
            "height (Y max, default 200), heightmap_resolution (power-of-two-plus-one, " +
            "default 129 — must be 33/65/129/257/513/1025/2049), position " +
            "('x,y,z' world-space), asset_path (Assets/.../.asset for the " +
            "TerrainData — when omitted, the TerrainData is in-scene only and is " +
            "NOT saved to disk). Mutating: runs the gate path; paths_hint is the " +
            "scene path + the asset path (when provided).")]
        public static string TerrainCreate(
            string terrain_name = "Terrain",
            float width = 500f,
            float length = 500f,
            float height = 200f,
            int heightmap_resolution = 129,
            string position = null,
            string asset_path = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TerrainJson.Error("paths_hint_required",
                    "terrain_create is mutating; pass a non-empty paths_hint " +
                    "scoped to the new terrain's scene path (+ the asset path " +
                    "when one is provided).");

            // Validate the heightmap resolution. Unity requires a value of the
            // form 2^k + 1 (33, 65, 129, 257, 513, 1025, 2049, 4097). We clamp
            // to the documented set and refuse anything else with a clear
            // hint — TerrainData.heightmapResolution silently rounds to a
            // valid value, which would surprise the agent.
            if (!IsValidHeightmapResolution(heightmap_resolution))
                return TerrainJson.Error("invalid_heightmap_resolution",
                    $"heightmap_resolution must be a power-of-two plus one " +
                    $"(33/65/129/257/513/1025/2049/4097); got {heightmap_resolution}.");

            if (width <= 0f || length <= 0f)
                return TerrainJson.Error("invalid_size",
                    "width and length must be positive (in world units).");
            if (height <= 0f)
                return TerrainJson.Error("invalid_size",
                    "height must be positive (in world units).");

            // Validate the asset path when provided. It must live under
            // Assets/, end in .asset, and its parent folder must exist (we do
            // not create folders here — the agent uses assets_create_folder).
            bool hasAsset = !string.IsNullOrEmpty(asset_path);
            if (hasAsset)
            {
                asset_path = asset_path.Replace('\\', '/');
                if (!asset_path.StartsWith("Assets/"))
                    return TerrainJson.Error("invalid_asset_path",
                        "asset_path must start with 'Assets/' and end in '.asset'.");
                if (!asset_path.EndsWith(".asset"))
                    return TerrainJson.Error("invalid_asset_path",
                        "asset_path must end in '.asset'.");
                if (AssetDatabase.LoadMainAssetAtPath(asset_path) != null)
                    return TerrainJson.Error("asset_path_in_use",
                        $"An asset already exists at '{asset_path}'. Choose a " +
                        "unique path or omit asset_path for an in-scene-only terrain.");
            }

            // Allocate the TerrainData. We use TerrainData.CreateInstance via
            // ScriptableObject (the public ctor) and configure size +
            // heightmap resolution before the GameObject is created.
            var td = ScriptableObject.CreateInstance<TerrainData>();
            try
            {
                td.heightmapResolution = heightmap_resolution;
                td.size = new Vector3(width, height, length);
                // Alphamap (splat) resolution must be set explicitly; default
                // is 512 but the catalog minimum expects a sane value aligned
                // to the heightmap. SetAlphamaps is 2x2 supersampled against
                // the heightmap.
                td.alphamapResolution = Mathf.NextPowerOfTwo(heightmap_resolution - 1) + 1;
                td.baseMapResolution = Mathf.NextPowerOfTwo(heightmap_resolution - 1) + 1;
            }
            catch (System.Exception e)
            {
                Object.DestroyImmediate(td);
                return TerrainJson.Error("terrain_data_failed",
                    "Failed to configure TerrainData: " + e.Message);
            }

            // Create the Terrain GameObject. Terrain.CreateTerrainGameObject
            // wires the collider + renderer + the TerrainData in one call.
            var go = Terrain.CreateTerrainGameObject(td);
            if (go == null)
            {
                Object.DestroyImmediate(td);
                return TerrainJson.Error("terrain_create_failed",
                    "Terrain.CreateTerrainGameObject returned null.");
            }
            go.name = string.IsNullOrEmpty(terrain_name) ? "Terrain" : terrain_name;
            Undo.RegisterCreatedObjectUndo(go, "Create Terrain");

            // Apply position when provided.
            if (!string.IsNullOrEmpty(position))
            {
                var pos = ParseVector3(position, Vector3.zero);
                Undo.RecordObject(go.transform, "Position Terrain");
                go.transform.position = pos;
            }

            // Persist the TerrainData to disk when an asset path was given.
            // This is the documented asset-vs-scene dual mutation — the gate
            // must see both the scene path and the .asset path in paths_hint.
            bool assetWritten = false;
            if (hasAsset)
            {
                EnsureFolderFor(asset_path);
                AssetDatabase.CreateAsset(td, asset_path);
                assetWritten = true;
            }

            EditorUtility.SetDirty(go);
            if (assetWritten) EditorUtility.SetDirty(td);

            var sb = new StringBuilder(320);
            sb.Append("\"terrain\":{");
            sb.Append("\"created\":true,");
            sb.Append("\"name\":").Append(TerrainJson.Esc(go.name)).Append(',');
            sb.Append("\"instanceId\":").Append(go.GetInstanceID()).Append(',');
            sb.Append("\"path\":").Append(TerrainJson.Esc(TerrainTargets.BuildPath(go))).Append(',');
            sb.Append("\"terrainDataInstanceId\":").Append(td.GetInstanceID()).Append(',');
            sb.Append("\"size\":").Append(Vec3(td.size)).Append(',');
            sb.Append("\"heightmapResolution\":").Append(td.heightmapResolution).Append(',');
            sb.Append("\"alphamapResolution\":").Append(td.alphamapResolution).Append(',');
            sb.Append("\"assetPath\":").Append(TerrainJson.Esc(assetWritten ? asset_path : "")).Append(',');
            sb.Append("\"assetWritten\":").Append(assetWritten ? "true" : "false");
            sb.Append('}');
            return TerrainJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Terrain — set heights (region write)
        // =====================================================================

        // Set a heightmap region from a 2D array of normalized 0-1 values.
        // x_offset / y_offset position the region inside the heightmap; the
        // array shape defines the region size. Arrays larger than 513x513 are
        // refused with a tiling hint (write in tiles via repeated calls).
        [BridgeTool("unity_open_mcp_terrain_set_heights",
            Title = "Terrain: Set Heightmap Region",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "terrain")]
        [System.ComponentModel.Description(
            "Set a heightmap region. heights is a row-major 2D array of " +
            "normalized 0-1 values; x_offset / y_offset position the region " +
            "inside the heightmap. Arrays larger than 513x513 per call are " +
            "refused — write in tiles via repeated calls instead. Mutating: " +
            "runs the gate path; paths_hint is the host scene path.")]
        public static string TerrainSetHeights(
            int instance_id = 0,
            string path = null,
            string name = null,
            int x_offset = 0,
            int y_offset = 0,
            string heights = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TerrainJson.Error("paths_hint_required",
                    "terrain_set_heights is mutating; pass a non-empty paths_hint.");

            var terrain = TerrainTargets.ResolveTerrain(instance_id, path, name);
            if (terrain == null)
                return TerrainComponentNotFound();

            if (string.IsNullOrEmpty(heights))
                return TerrainJson.Error("missing_parameter",
                    "'heights' is required (a row-major 2D JSON array of " +
                    "normalized 0-1 values).");

            var rows = TerrainArrays.ParseFloat2D(heights, out var parseError);
            if (rows == null)
                return TerrainJson.Error("invalid_heights_array",
                    "Could not parse 'heights': " + parseError);

            var td = terrain.terrainData;
            var resH = td.heightmapResolution;

            // Bounds-check the region against the heightmap. Unity's
            // SetHeights(xBase, yBase, heights) requires xBase + width <= resH
            // (heightmap is resH x resH). We surface the bounds explicitly.
            int w = rows[0].Length;
            int h = rows.Length;
            if (x_offset < 0 || y_offset < 0 ||
                x_offset + w > resH || y_offset + h > resH)
                return TerrainJson.Error("region_out_of_bounds",
                    $"Region [{x_offset}..{x_offset + w - 1}, " +
                    $"{y_offset}..{y_offset + h - 1}] does not fit inside the " +
                    $"heightmap (resolution {resH}x{resH}).");

            Undo.RecordObject(td, "Set Terrain heights");

            // Clamp each value to [0,1] defensively (Unity clamps internally
            // but we want the response to reflect what we actually wrote).
            var grid = TerrainArrays.ToHeightmap(rows);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    grid[y, x] = Mathf.Clamp01(grid[y, x]);

            td.SetHeights(x_offset, y_offset, grid);
            // Flush + dirty so the change is picked up by the renderer + the
            // gate delta. terrain.Flush() rebuilds the LOD; SetDirty marks the
            // TerrainData for the asset database.
            terrain.Flush();
            EditorUtility.SetDirty(td);

            var sb = new StringBuilder(220);
            sb.Append("\"heights\":{");
            sb.Append("\"written\":true,");
            sb.Append("\"xOffset\":").Append(x_offset).Append(',');
            sb.Append("\"yOffset\":").Append(y_offset).Append(',');
            sb.Append("\"width\":").Append(w).Append(',');
            sb.Append("\"height\":").Append(h).Append(',');
            sb.Append("\"heightmapResolution\":").Append(resH);
            sb.Append('}');
            return TerrainJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Terrain — paint layer (splat region write)
        // =====================================================================

        // Paint one terrain layer's splat from a 2D alphamap (0-1 weights).
        // When layer_index is new (>= the terrain's current layer count) a
        // new TerrainLayer is seeded — either loaded from layer_path (an
        // existing .terrainlayer asset) or created fresh at that path. Mirrors
        // Ivan Terrain.PaintLayer.cs.
        [BridgeTool("unity_open_mcp_terrain_paint_layer",
            Title = "Terrain: Paint Layer (Splat)",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "terrain")]
        [System.ComponentModel.Description(
            "Paint a terrain layer's splat in a region. alphamap is a row-major " +
            "2D array of 0-1 weights for layer_index; x_offset / y_offset position " +
            "the region inside the alphamap. When layer_index is new (>= the " +
            "terrain's current layer count), a new TerrainLayer is added — " +
            "either loaded from layer_path (an existing .terrainlayer asset) or " +
            "created fresh at that path. Arrays larger than 513x513 per call are " +
            "refused — write in tiles. Mutating: runs the gate path; paths_hint " +
            "is the scene path (+ the layer asset path when a new layer is created).")]
        public static string TerrainPaintLayer(
            int instance_id = 0,
            string path = null,
            string name = null,
            int layer_index = 0,
            int x_offset = 0,
            int y_offset = 0,
            string alphamap = null,
            string layer_path = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TerrainJson.Error("paths_hint_required",
                    "terrain_paint_layer is mutating; pass a non-empty paths_hint.");

            if (layer_index < 0)
                return TerrainJson.Error("invalid_layer_index",
                    "layer_index must be >= 0.");

            var terrain = TerrainTargets.ResolveTerrain(instance_id, path, name);
            if (terrain == null)
                return TerrainComponentNotFound();

            if (string.IsNullOrEmpty(alphamap))
                return TerrainJson.Error("missing_parameter",
                    "'alphamap' is required (a row-major 2D JSON array of " +
                    "normalized 0-1 weights).");

            var rows = TerrainArrays.ParseFloat2D(alphamap, out var parseError);
            if (rows == null)
                return TerrainJson.Error("invalid_alphamap_array",
                    "Could not parse 'alphamap': " + parseError);

            var td = terrain.terrainData;
            var alphamapRes = td.alphamapResolution;
            int w = rows[0].Length;
            int h = rows.Length;
            if (x_offset < 0 || y_offset < 0 ||
                x_offset + w > alphamapRes || y_offset + h > alphamapRes)
                return TerrainJson.Error("region_out_of_bounds",
                    $"Region [{x_offset}..{x_offset + w - 1}, " +
                    $"{y_offset}..{y_offset + h - 1}] does not fit inside the " +
                    $"alphamap (resolution {alphamapRes}x{alphamapRes}).");

            Undo.RecordObject(td, "Paint Terrain layer");

            // Ensure the layer exists. When the index is beyond the current
            // layer count, seed a new TerrainLayer (load from layer_path or
            // create a fresh asset there). Unity caps terrain layers at 8 for
            // the standard terrain shader.
            bool layerAdded = false;
            string createdLayerPath = "";
            var existingLayers = td.terrainLayers;
            int maxLayers = 8;
            if (layer_index >= maxLayers)
            {
                return TerrainJson.Error("invalid_layer_index",
                    $"layer_index {layer_index} exceeds the standard terrain " +
                    $"shader cap of {maxLayers} layers.");
            }

            if (layer_index >= existingLayers.Length)
            {
                // Grow the layer array up to layer_index + 1, filling gaps
                // with fresh in-scene layers so the array has no nulls (Unity's
                // terrainLayers setter rejects null entries). The agent can
                // swap gap layers later via terrain_paint_layer at the
                // missing indices with a real layer_path.
                var grown = new TerrainLayer[layer_index + 1];
                for (int i = 0; i < existingLayers.Length; i++)
                    grown[i] = existingLayers[i];
                for (int i = existingLayers.Length; i < layer_index; i++)
                    grown[i] = new TerrainLayer();

                TerrainLayer newLayer = null;
                if (!string.IsNullOrEmpty(layer_path))
                {
                    var norm = layer_path.Replace('\\', '/');
                    var loaded = AssetDatabase.LoadMainAssetAtPath(norm);
                    if (loaded is TerrainLayer existing)
                    {
                        newLayer = existing;
                    }
                    else
                    {
                        // Create a fresh TerrainLayer asset at the path. The
                        // agent can later assign its diffuse / normal maps
                        // via asset property editing.
                        newLayer = new TerrainLayer();
                        if (norm.StartsWith("Assets/") && norm.EndsWith(".terrainlayer"))
                        {
                            EnsureFolderFor(norm);
                            AssetDatabase.CreateAsset(newLayer, norm);
                            createdLayerPath = norm;
                            layerAdded = true;
                        }
                        else
                        {
                            // Path is not a usable asset path — keep the layer
                            // in-scene (unsaved). Still functional for the
                            // paint, but it won't persist across reloads.
                        }
                    }
                }
                else
                {
                    // No path — create an in-scene-only layer.
                    newLayer = new TerrainLayer();
                }
                grown[layer_index] = newLayer;
                td.terrainLayers = grown;
            }

            // Read the current alphamap slice for the region, apply the new
            // layer weights, and write back. SetAlphamaps expects
            // float[regionY, regionX, layerCount]; we read the live slice so
            // other layers keep their weights.
            int layerCount = td.alphamapLayers;
            var current = td.GetAlphamaps(x_offset, y_offset, w, h);
            // current is [regionY, regionX, layerCount]. If our grown layer
            // array is larger than the live slice's z, re-read after the
            // terrainLayers assignment above (Unity rebuilds the alphamap).
            if (current.GetUpperBound(2) + 1 != layerCount)
                current = td.GetAlphamaps(x_offset, y_offset, w, h);

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    current[y, x, layer_index] = Mathf.Clamp01(rows[y][x]);

            td.SetAlphamaps(x_offset, y_offset, current);
            EditorUtility.SetDirty(td);

            var sb = new StringBuilder(260);
            sb.Append("\"layer\":{");
            sb.Append("\"painted\":true,");
            sb.Append("\"layerIndex\":").Append(layer_index).Append(',');
            sb.Append("\"layerCount\":").Append(td.terrainLayers.Length).Append(',');
            sb.Append("\"layerAdded\":").Append(layerAdded ? "true" : "false").Append(',');
            sb.Append("\"createdLayerPath\":").Append(TerrainJson.Esc(createdLayerPath)).Append(',');
            sb.Append("\"xOffset\":").Append(x_offset).Append(',');
            sb.Append("\"yOffset\":").Append(y_offset).Append(',');
            sb.Append("\"width\":").Append(w).Append(',');
            sb.Append("\"height\":").Append(h).Append(',');
            sb.Append("\"alphamapResolution\":").Append(alphamapRes);
            sb.Append('}');
            return TerrainJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Terrain — place trees (instances against a prototype)
        // =====================================================================

        // Place tree instances against a tree prototype index. Each instance
        // carries a position (normalized 0-1 on the terrain), height_scale,
        // width_scale, and rotation (degrees). When the prototype index is new
        // (>= the terrain's current prototype count), a prototype is seeded
        // from prototype_prefab_path (a .prefab asset). Mirrors Ivan
        // Terrain.PlaceTrees.cs.
        [BridgeTool("unity_open_mcp_terrain_place_trees",
            Title = "Terrain: Place Trees",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "terrain")]
        [System.ComponentModel.Description(
            "Place tree instances on a terrain. instances is an array of " +
            "{position ('x,z' normalized 0-1 on the terrain), height_scale " +
            "(default 1), width_scale (default 1), rotation (degrees, default 0)}. " +
            "tree_prototype_index selects the prototype. When the index is new " +
            "(>= the terrain's current prototype count), a prototype is seeded " +
            "from prototype_prefab_path (a .prefab asset). Mutating: runs the " +
            "gate path; paths_hint is the host scene path.")]
        public static string TerrainPlaceTrees(
            int instance_id = 0,
            string path = null,
            string name = null,
            int tree_prototype_index = 0,
            string instances = null,
            string prototype_prefab_path = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TerrainJson.Error("paths_hint_required",
                    "terrain_place_trees is mutating; pass a non-empty paths_hint.");

            if (tree_prototype_index < 0)
                return TerrainJson.Error("invalid_prototype_index",
                    "tree_prototype_index must be >= 0.");

            var terrain = TerrainTargets.ResolveTerrain(instance_id, path, name);
            if (terrain == null)
                return TerrainComponentNotFound();

            if (string.IsNullOrEmpty(instances))
                return TerrainJson.Error("missing_parameter",
                    "'instances' is required (a JSON array of " +
                    "{position, height_scale?, width_scale?, rotation?}).");

            var parsed = ParseTreeInstances(instances, out var parseError);
            if (parsed == null)
                return TerrainJson.Error("invalid_instances_array",
                    "Could not parse 'instances': " + parseError);
            if (parsed.Count == 0)
                return TerrainJson.Error("missing_parameter",
                    "'instances' must contain at least one tree instance.");

            var td = terrain.terrainData;
            Undo.RecordObject(td, "Place trees");

            // Ensure the prototype exists. When the index is beyond the
            // current prototype count, grow the array and seed a prototype
            // from the prefab path.
            bool prototypeAdded = false;
            var existingProtos = td.treePrototypes;
            if (tree_prototype_index >= existingProtos.Length)
            {
                if (string.IsNullOrEmpty(prototype_prefab_path))
                    return TerrainJson.Error("missing_parameter",
                        $"tree_prototype_index {tree_prototype_index} is new " +
                        "(>= the current count " + existingProtos.Length + ") — " +
                        "prototype_prefab_path is required to seed a prototype.");

                var norm = prototype_prefab_path.Replace('\\', '/');
                var prefab = AssetDatabase.LoadMainAssetAtPath(norm);
                if (!(prefab is GameObject prefabGo))
                    return TerrainJson.Error("prototype_prefab_not_found",
                        $"No GameObject prefab found at '{norm}'. The path must " +
                        "point at a .prefab asset under Assets/.");

                var grown = new TreePrototype[tree_prototype_index + 1];
                for (int i = 0; i < existingProtos.Length; i++)
                    grown[i] = existingProtos[i];
                // Fill any gap indices (between the old count and the requested
                // index) with placeholder prototypes pointing at the same
                // prefab — TreePrototype is a class and nulls are not valid.
                for (int i = existingProtos.Length; i < tree_prototype_index; i++)
                    grown[i] = new TreePrototype { prefab = prefabGo };
                var proto = new TreePrototype { prefab = prefabGo };
                grown[tree_prototype_index] = proto;
                td.treePrototypes = grown;
                prototypeAdded = true;
            }

            // Append the tree instances to the terrain's instance list. TreeInstance
            // uses a normalized position (Vector3 with x/z in 0-1 on the terrain,
            // y is the height in world units — we set y to the sampled height).
            var current = new List<TreeInstance>(td.treeInstances);
            foreach (var inst in parsed)
            {
                var ti = new TreeInstance
                {
                    position = inst.Position,
                    heightScale = inst.HeightScale,
                    widthScale = inst.WidthScale,
                    rotation = inst.Rotation * Mathf.Deg2Rad,
                    color = Color.white,
                    lightmapColor = Color.white,
                    prototypeIndex = tree_prototype_index,
                };
                current.Add(ti);
            }
            td.SetTreeInstances(current.ToArray(), true);
            EditorUtility.SetDirty(td);

            var sb = new StringBuilder(220);
            sb.Append("\"trees\":{");
            sb.Append("\"placed\":true,");
            sb.Append("\"placedCount\":").Append(parsed.Count).Append(',');
            sb.Append("\"prototypeIndex\":").Append(tree_prototype_index).Append(',');
            sb.Append("\"prototypeCount\":").Append(td.treePrototypes.Length).Append(',');
            sb.Append("\"prototypeAdded\":").Append(prototypeAdded ? "true" : "false").Append(',');
            sb.Append("\"totalTreeCount\":").Append(td.treeInstanceCount);
            sb.Append('}');
            return TerrainJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Terrain — set neighbors (LOD stitching)
        // =====================================================================

        // Set the top / bottom / left / right neighbor Terrains for LOD
        // stitching. Each neighbor is resolved by hierarchy path / instance id
        // / name; pass null (or omit) to clear a side.
        [BridgeTool("unity_open_mcp_terrain_set_neighbors",
            Title = "Terrain: Set Neighbors",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "terrain")]
        [System.ComponentModel.Description(
            "Set neighbor Terrains for LOD stitching. Each side (top / bottom / " +
            "left / right) accepts a terrain GameObject resolved by instance_id > " +
            "path > name. Pass an empty value or omit a side to clear it. " +
            "Idempotent — re-setting the same neighbors reports set:true. " +
            "Mutating: runs the gate path; paths_hint is the host scene path.")]
        public static string TerrainSetNeighbors(
            int instance_id = 0,
            string path = null,
            string name = null,
            string top_path = null,
            int top_instance_id = 0,
            string bottom_path = null,
            int bottom_instance_id = 0,
            string left_path = null,
            int left_instance_id = 0,
            string right_path = null,
            int right_instance_id = 0,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TerrainJson.Error("paths_hint_required",
                    "terrain_set_neighbors is mutating; pass a non-empty paths_hint.");

            var terrain = TerrainTargets.ResolveTerrain(instance_id, path, name);
            if (terrain == null)
                return TerrainComponentNotFound();

            // Resolve each side. A null/empty side clears it; a populated side
            // must resolve to a Terrain (else neighbor_not_found). The host
            // cannot be its own neighbor.
            Terrain top = null, bottom = null, left = null, right = null;
            string error;
            if ((error = ResolveNeighborSide(terrain, top_instance_id, top_path, "top", out top)) != null) return error;
            if ((error = ResolveNeighborSide(terrain, bottom_instance_id, bottom_path, "bottom", out bottom)) != null) return error;
            if ((error = ResolveNeighborSide(terrain, left_instance_id, left_path, "left", out left)) != null) return error;
            if ((error = ResolveNeighborSide(terrain, right_instance_id, right_path, "right", out right)) != null) return error;

            Undo.RecordObject(terrain, "Set Terrain neighbors");
            terrain.SetNeighbors(left, top, right, bottom);
            // Flush theTerrain so the stitching rebuilds.
            terrain.Flush();
            EditorUtility.SetDirty(terrain);

            return TerrainJson.Ok(NeighborState(terrain));
        }

        // =====================================================================
        // Helpers — neighbor resolution
        // =====================================================================

        // Resolve one neighbor side. Returns null on success (neighbor is set,
        // possibly to null for a clear); returns a non-null error envelope
        // string when resolution failed (the caller returns it directly).
        private static string ResolveNeighborSide(Terrain host, int instanceId, string sidePath, string side, out Terrain neighbor)
        {
            neighbor = null;
            // No hint for this side → leave it null (clear).
            if (instanceId == 0 && string.IsNullOrEmpty(sidePath)) return null;

            var go = TerrainTargets.Resolve(instanceId, sidePath, null);
            if (go == null)
                return TerrainJson.Error("neighbor_not_found",
                    $"Neighbor '{side}' not resolved. Address by instance_id > path.");
            if (go == host.gameObject)
                return TerrainJson.Error("neighbor_is_self",
                    $"Neighbor '{side}' resolves to the host terrain itself — a " +
                    "terrain cannot be its own neighbor.");
            var t = go.GetComponent<Terrain>();
            if (t == null)
                return TerrainJson.Error("neighbor_not_terrain",
                    $"Neighbor '{side}' has no Terrain component. All neighbors " +
                    "must be terrain GameObjects.");
            neighbor = t;
            return null;
        }

        private static string NeighborState(Terrain terrain)
        {
            var sb = new StringBuilder(220);
            sb.Append("\"neighbors\":{");
            sb.Append("\"top\":").Append(TerrainJson.Esc(terrain.topNeighbor != null ? terrain.topNeighbor.gameObject.name : "")).Append(',');
            sb.Append("\"bottom\":").Append(TerrainJson.Esc(terrain.bottomNeighbor != null ? terrain.bottomNeighbor.gameObject.name : "")).Append(',');
            sb.Append("\"left\":").Append(TerrainJson.Esc(terrain.leftNeighbor != null ? terrain.leftNeighbor.gameObject.name : "")).Append(',');
            sb.Append("\"right\":").Append(TerrainJson.Esc(terrain.rightNeighbor != null ? terrain.rightNeighbor.gameObject.name : ""));
            sb.Append('}');
            return sb.ToString();
        }

        // =====================================================================
        // Helpers — tree instance parsing
        // =====================================================================

        struct ParsedTreeInstance
        {
            public Vector3 Position; // normalized 0-1 on the terrain (x/z), y=0
            public float HeightScale;
            public float WidthScale;
            public float Rotation; // degrees
        }

        // Parse the instances JSON array. Each entry is { position, height_scale?,
        // width_scale?, rotation? }. position is "x,z" (normalized 0-1 on the
        // terrain). We use the same hand-rolled object-array parser the
        // constraint / nav modify tools use (the payload is small).
        private static List<ParsedTreeInstance> ParseTreeInstances(string json, out string error)
        {
            error = null;
            var result = new List<ParsedTreeInstance>();
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]"))
            {
                error = "instances must be a JSON array.";
                return null;
            }

            int depth = 0;
            int objStart = -1;
            int i = 1;
            while (i < trimmed.Length - 1)
            {
                var c = trimmed[i];
                if (c == '{')
                {
                    if (depth == 0) objStart = i + 1;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        var body = trimmed.Substring(objStart, i - objStart);
                        var inst = ParseOneTreeInstance(body, out error);
                        if (inst == null) return null;
                        result.Add(inst.Value);
                        objStart = -1;
                    }
                }
                i++;
            }
            if (depth != 0)
            {
                error = "unbalanced braces in instances array.";
                return null;
            }
            return result;
        }

        private static ParsedTreeInstance? ParseOneTreeInstance(string body, out string error)
        {
            error = null;
            var posStr = ExtractStringValue(body, "position");
            if (string.IsNullOrEmpty(posStr))
            {
                error = "each instance needs a 'position' ('x,z' normalized 0-1).";
                return null;
            }
            var pos = ParseNormalized2(posStr);
            if (!pos.HasValue)
            {
                error = $"could not parse position '{posStr}' ('x,z' normalized 0-1).";
                return null;
            }
            var inst = new ParsedTreeInstance
            {
                Position = new Vector3(Mathf.Clamp01(pos.Value.x), 0f, Mathf.Clamp01(pos.Value.y)),
                HeightScale = ExtractFloat(body, "height_scale", 1f),
                WidthScale = ExtractFloat(body, "width_scale", 1f),
                Rotation = ExtractFloat(body, "rotation", 0f),
            };
            if (inst.HeightScale <= 0f) inst.HeightScale = 1f;
            if (inst.WidthScale <= 0f) inst.WidthScale = 1f;
            return inst;
        }

        // =====================================================================
        // Helpers — JSON value extraction (small, hand-rolled)
        // =====================================================================

        // Extract a quoted string value for a key from a flat JSON object body.
        // Returns null when the key is absent.
        private static string ExtractStringValue(string body, string key)
        {
            var raw = ExtractRawValue(body, key);
            if (string.IsNullOrEmpty(raw)) return null;
            if (raw.StartsWith("\"") && raw.EndsWith("\"") && raw.Length >= 2)
                return raw.Substring(1, raw.Length - 2);
            return raw;
        }

        // Extract a float value for a key, falling back to the default when
        // absent or unparseable.
        private static float ExtractFloat(string body, string key, float fallback)
        {
            var raw = ExtractRawValue(body, key);
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (float.TryParse(raw.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var f)) return f;
            return fallback;
        }

        private static string ExtractRawValue(string body, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = body.IndexOf(pattern, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            var colon = body.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            var start = colon + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start])) start++;
            if (start >= body.Length) return null;

            if (body[start] == '"')
            {
                var end = start + 1;
                while (end < body.Length)
                {
                    if (body[end] == '\\' && end + 1 < body.Length) { end += 2; continue; }
                    if (body[end] == '"') break;
                    end++;
                }
                return body.Substring(start, System.Math.Min(end + 1, body.Length) - start);
            }
            // Primitive — capture up to comma or brace.
            var primitiveEnd = start;
            while (primitiveEnd < body.Length &&
                   body[primitiveEnd] != ',' &&
                   body[primitiveEnd] != '}')
                primitiveEnd++;
            return body.Substring(start, primitiveEnd - start).Trim();
        }

        // =====================================================================
        // Helpers — common
        // =====================================================================

        private static bool IsValidHeightmapResolution(int r)
        {
            // Unity accepts 2^k + 1 from 33 up to 4097.
            switch (r)
            {
                case 33:
                case 65:
                case 129:
                case 257:
                case 513:
                case 1025:
                case 2049:
                case 4097:
                    return true;
                default:
                    return false;
            }
        }

        private static Vector3 ParseVector3(string s, Vector3 fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            var parts = s.Split(',');
            if (parts.Length != 3) return fallback;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var x)) return fallback;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var y)) return fallback;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var z)) return fallback;
            return new Vector3(x, y, z);
        }

        // Parse a 2-component normalized position "x,z". Returns null on a
        // parse error (the caller surfaces a clear message).
        private static Vector2? ParseNormalized2(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split(',');
            if (parts.Length != 2) return null;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var x)) return null;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var y)) return null;
            return new Vector2(x, y);
        }

        private static string Vec3(Vector3 v)
            => "[" + TerrainJson.Num(v.x) + "," + TerrainJson.Num(v.y) + "," + TerrainJson.Num(v.z) + "]";

        // Ensure the parent folder chain for an asset path exists. Mirrors
        // LightingTools.EnsureFolderFor — AssetDatabase.CreateAsset does NOT
        // auto-create folders, so terrain_create + terrain_paint_layer must
        // build the chain first (otherwise the asset write fails silently).
        private static void EnsureFolderFor(string assetPath)
        {
            var slash = assetPath.LastIndexOf('/');
            if (slash < 0) return;
            var dir = assetPath.Substring(0, slash);
            if (string.IsNullOrEmpty(dir) || !dir.StartsWith("Assets")) return;
            if (AssetDatabase.IsValidFolder(dir)) return;
            var segments = dir.Split('/');
            var current = segments[0]; // "Assets"
            for (int i = 1; i < segments.Length; i++)
            {
                var next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
        }

        // Terrain-component-not-found helper — kept short so call sites read cleanly.
        private static string TerrainComponentNotFound()
            => TerrainJson.Error("component_not_found",
                "No Terrain on the resolved GameObject. Address the host by " +
                "instance_id > path > name, or create one with terrain_create.");
    }
}
