// M20 Plan 6 / T20.6.3 — Tilemap embedded domain tools (compile-gated).
//
// Five typed tools for in-editor 2D level design:
//   - create: Grid GameObject + child Tilemap/TilemapRenderer
//   - set_tile: paint a single tile at a cell coordinate
//   - box_fill: fill a rectangular cell region
//   - create_tile_asset: create a Tile ScriptableObject asset (.asset)
//   - create_rule_tile: create a RuleTile asset (com.unity.2d.tilemap.extras)
//
// Two gates (the canonical two-dependency pattern):
//   - UNITY_OPEN_MCP_EXT_TILEMAP gates the whole pack (com.unity.2d.tilemap).
//   - UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS gates ONLY create_rule_tile's body
//     (com.unity.2d.tilemap.extras for RuleTile). When the extras package is
//     absent, create_rule_tile returns a clean install error instead of
//     compiling in a broken reference.
//
// All five tools are mutating and run the full gate path. tilemap_create adds
// a new GameObject to the active scene (paths_hint is the scene path); the
// asset-create tools write a .asset (paths_hint includes the asset path).
//
// Compile-gate-only: Tilemap has a single stable public API (UnityEngine.
// Tilemaps namespace). When the package is absent the tools are not compiled
// in and the capability surface reports the domain as `available: false
// (dependency missing: com.unity.2d.tilemap)`.
//
// Naming: `unity_open_mcp_tilemap_<action>` (snake_case domain prefix — mirrors
// the kebab `tilemap-*` ids in the upstream Unity-AI-Tilemap reference pack).
#if UNITY_OPEN_MCP_EXT_TILEMAP
#pragma warning disable CS0618
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEngine.Tilemaps;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Extensions.Tilemap
{
    [BridgeToolType]
    public static class TilemapTools
    {
        // =====================================================================
        // create
        // =====================================================================

        // Create a Grid GameObject with a child Tilemap + TilemapRenderer in
        // the active scene. tilemap_name optionally names the child; grid_name
        // optionally names the Grid root.
        [BridgeTool("unity_open_mcp_tilemap_create",
            Title = "Tilemap: Create",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "tilemap")]
        [System.ComponentModel.Description(
            "Create a Grid GameObject in the active scene with a child Tilemap " +
            "+ TilemapRenderer. grid_name optionally names the Grid root; " +
            "tilemap_name optionally names the child Tilemap. Returns the Grid " +
            "and Tilemap instance ids + paths. Mutating: runs the gate path; " +
            "paths_hint is the active scene path. Requires the com.unity.2d.tilemap " +
            "package installed in the project.")]
        public static string Create(
            string grid_name = null,
            string tilemap_name = null,
            string parent_path = null,
            string position = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TilemapJson.Error("paths_hint_required",
                    "tilemap_create is mutating; pass a non-empty paths_hint " +
                    "scoped to the active scene path.");

            var grid = new GameObject(string.IsNullOrEmpty(grid_name) ? "Grid" : grid_name);
            Undo.RegisterCreatedObjectUndo(grid, "Create Tilemap Grid");

            Transform parent = null;
            if (!string.IsNullOrEmpty(parent_path))
            {
                var parentGo = TilemapTargets.FindByPath(parent_path);
                if (parentGo == null)
                {
                    Object.DestroyImmediate(grid);
                    return TilemapJson.Error("parent_not_found",
                        $"No GameObject at parent_path '{parent_path}'.");
                }
                parent = parentGo.transform;
                grid.transform.SetParent(parent, false);
            }

            if (!string.IsNullOrEmpty(position))
            {
                var p = ParseVector3(position, Vector3.zero);
                if (parent != null) grid.transform.localPosition = p;
                else grid.transform.position = p;
            }

            Undo.AddComponent<Grid>(grid);

            // Child Tilemap GameObject — the conventional 2D layout puts the
            // Tilemap as a child of the Grid.
            var tilemapGo = new GameObject(string.IsNullOrEmpty(tilemap_name) ? "Tilemap" : tilemap_name);
            Undo.RegisterCreatedObjectUndo(tilemapGo, "Create Tilemap");
            tilemapGo.transform.SetParent(grid.transform, false);
            Undo.AddComponent<Tilemap>(tilemapGo);
            Undo.AddComponent<TilemapRenderer>(tilemapGo);

            EditorUtility.SetDirty(grid);

            var tilemap = tilemapGo.GetComponent<Tilemap>();
            var sb = new StringBuilder(200);
            sb.Append("\"tilemap\":{");
            sb.Append("\"gridInstanceId\":").Append(grid.GetInstanceID()).Append(',');
            sb.Append("\"gridPath\":").Append(TilemapJson.Esc(TilemapTargets.BuildPath(grid))).Append(',');
            sb.Append("\"tilemapInstanceId\":").Append(tilemapGo.GetInstanceID()).Append(',');
            sb.Append("\"tilemapPath\":").Append(TilemapJson.Esc(TilemapTargets.BuildPath(tilemapGo))).Append(',');
            sb.Append("\"cellBounds\":").Append(TilemapJson.Vec3Int(tilemap.cellBounds.size));
            sb.Append('}');
            return TilemapJson.Ok(sb.ToString());
        }

        // =====================================================================
        // set_tile
        // =====================================================================

        // Paint a single tile at a cell coordinate. Address the Tilemap by
        // instance_id > path > name; address the TileBase by asset_path.
        [BridgeTool("unity_open_mcp_tilemap_set_tile",
            Title = "Tilemap: Set Tile",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "tilemap")]
        [System.ComponentModel.Description(
            "Paint a single tile at a cell coordinate. Address the Tilemap by " +
            "instance_id > path > name; address the TileBase by tile_asset_path. " +
            "x / y are the cell coordinates (z defaults to 0). Mutating: runs " +
            "the gate path; paths_hint is the host scene path. Requires " +
            "com.unity.2d.tilemap.")]
        public static string SetTile(
            int instance_id = 0,
            string path = null,
            string name = null,
            string tile_asset_path = null,
            int x = 0,
            int y = 0,
            int z = 0,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TilemapJson.Error("paths_hint_required",
                    "tilemap_set_tile is mutating; pass a non-empty paths_hint.");

            if (string.IsNullOrEmpty(tile_asset_path))
                return TilemapJson.Error("missing_parameter",
                    "'tile_asset_path' is required (an 'Assets/.../*.asset' " +
                    "TileBase reference).");

            var host = TilemapTargets.Resolve(instance_id, path, name);
            if (host == null) return TargetNotFound();

            var tilemap = host.GetComponent<Tilemap>();
            if (tilemap == null)
                return TilemapJson.Error("component_not_found",
                    "Target has no Tilemap component.");

            var tile = AssetDatabase.LoadAssetAtPath<TileBase>(tile_asset_path);
            if (tile == null)
                return TilemapJson.Error("tile_not_found",
                    $"No TileBase found at '{tile_asset_path}'.");

            var cell = new Vector3Int(x, y, z);
            Undo.RecordObject(tilemap, "Set tile");
            tilemap.SetTile(cell, tile);
            EditorUtility.SetDirty(tilemap);

            var sb = new StringBuilder(140);
            sb.Append("\"tile\":{");
            sb.Append("\"cell\":").Append(TilemapJson.Vec3Int(cell)).Append(',');
            sb.Append("\"tileAsset\":").Append(TilemapJson.Esc(tile.name));
            sb.Append('}');
            return TilemapJson.Ok(sb.ToString());
        }

        // =====================================================================
        // box_fill
        // =====================================================================

        // Fill a rectangular cell region. x1/y1 and x2/y2 are the opposite
        // corners (inclusive); the tile is painted into every cell in the box.
        [BridgeTool("unity_open_mcp_tilemap_box_fill",
            Title = "Tilemap: Box Fill",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "tilemap")]
        [System.ComponentModel.Description(
            "Fill a rectangular cell region with a tile. x1 / y1 and x2 / y2 " +
            "are the opposite corners (inclusive); z defaults to 0. Address the " +
            "Tilemap by instance_id > path > name and the tile by " +
            "tile_asset_path. Mutating: runs the gate path; paths_hint is the " +
            "host scene path. Requires com.unity.2d.tilemap.")]
        public static string BoxFill(
            int instance_id = 0,
            string path = null,
            string name = null,
            string tile_asset_path = null,
            int x1 = 0,
            int y1 = 0,
            int x2 = 0,
            int y2 = 0,
            int z = 0,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TilemapJson.Error("paths_hint_required",
                    "tilemap_box_fill is mutating; pass a non-empty paths_hint.");

            if (string.IsNullOrEmpty(tile_asset_path))
                return TilemapJson.Error("missing_parameter",
                    "'tile_asset_path' is required (an 'Assets/.../*.asset' " +
                    "TileBase reference).");

            var host = TilemapTargets.Resolve(instance_id, path, name);
            if (host == null) return TargetNotFound();

            var tilemap = host.GetComponent<Tilemap>();
            if (tilemap == null)
                return TilemapJson.Error("component_not_found",
                    "Target has no Tilemap component.");

            var tile = AssetDatabase.LoadAssetAtPath<TileBase>(tile_asset_path);
            if (tile == null)
                return TilemapJson.Error("tile_not_found",
                    $"No TileBase found at '{tile_asset_path}'.");

            // Tilemap.BoxFill(position, tile, xMin, yMin, xMax, yMax) — position
            // is the cell origin for the box's z plane.
            Undo.RecordObject(tilemap, "Box fill tiles");
            var origin = new Vector3Int(System.Math.Min(x1, x2), System.Math.Min(y1, y2), z);
            tilemap.BoxFill(origin, tile,
                System.Math.Min(x1, x2), System.Math.Min(y1, y2),
                System.Math.Max(x1, x2), System.Math.Max(y1, y2));
            EditorUtility.SetDirty(tilemap);

            var width = System.Math.Abs(x2 - x1) + 1;
            var height = System.Math.Abs(y2 - y1) + 1;
            var sb = new StringBuilder(160);
            sb.Append("\"boxFill\":{");
            sb.Append("\"origin\":").Append(TilemapJson.Vec3Int(origin)).Append(',');
            sb.Append("\"width\":").Append(width).Append(',');
            sb.Append("\"height\":").Append(height).Append(',');
            sb.Append("\"cellsPainted\":").Append(width * height);
            sb.Append('}');
            return TilemapJson.Ok(sb.ToString());
        }

        // =====================================================================
        // create_tile_asset
        // =====================================================================

        // Create a Tile ScriptableObject asset (.asset) at the given path. The
        // Tile has no sprite by default — set its sprite field via modify /
        // asset writes afterwards, or pass sprite_asset_path to seed it.
        [BridgeTool("unity_open_mcp_tilemap_create_tile_asset",
            Title = "Tilemap: Create Tile Asset",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "tilemap")]
        [System.ComponentModel.Description(
            "Create a Tile ScriptableObject asset (.asset) at the given " +
            "asset_path. Optionally seed its sprite via sprite_asset_path. The " +
            "parent folder must already exist. Mutating: runs the gate path; " +
            "paths_hint includes the new asset path. Requires com.unity.2d.tilemap.")]
        public static string CreateTileAsset(
            string asset_path = null,
            string sprite_asset_path = null,
            string tile_name = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TilemapJson.Error("paths_hint_required",
                    "tilemap_create_tile_asset is mutating; pass a non-empty " +
                    "paths_hint that includes the new asset path.");

            if (string.IsNullOrEmpty(asset_path))
                return TilemapJson.Error("missing_parameter",
                    "'asset_path' is required (an 'Assets/.../*.asset' path).");

            if (!asset_path.EndsWith(".asset"))
                return TilemapJson.Error("invalid_parameter",
                    "'asset_path' must end with '.asset'.");

            if (AssetDatabase.LoadAssetAtPath<TileBase>(asset_path) != null)
                return TilemapJson.Error("already_exists",
                    $"A TileBase asset already exists at '{asset_path}'.");

            var tile = ScriptableObject.CreateInstance<Tile>();
            if (!string.IsNullOrEmpty(tile_name)) tile.name = tile_name;

            if (!string.IsNullOrEmpty(sprite_asset_path))
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(sprite_asset_path);
                if (sprite == null)
                    return TilemapJson.Error("sprite_not_found",
                        $"No Sprite found at '{sprite_asset_path}'.");
                tile.sprite = sprite;
            }

            AssetDatabase.CreateAsset(tile, asset_path);
            EditorUtility.SetDirty(tile);

            var sb = new StringBuilder(160);
            sb.Append("\"tileAsset\":{");
            sb.Append("\"assetPath\":").Append(TilemapJson.Esc(asset_path)).Append(',');
            sb.Append("\"instanceId\":").Append(tile.GetInstanceID()).Append(',');
            sb.Append("\"name\":").Append(TilemapJson.Esc(tile.name)).Append(',');
            sb.Append("\"sprite\":").Append(TilemapJson.Esc(tile.sprite != null ? tile.sprite.name : ""));
            sb.Append('}');
            return TilemapJson.Ok(sb.ToString());
        }

        // =====================================================================
        // create_rule_tile (com.unity.2d.tilemap.extras — inner-guarded)
        // =====================================================================

        // Create a RuleTile asset at the given path. com.unity.2d.tilemap.extras
        // is an OPTIONAL dependency: this tool's body is inner-guarded by
        // UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS. When extras is absent, the tool
        // compiles in (the outer pack gate passes) but returns a clean install
        // error — two defines, two guards.
        [BridgeTool("unity_open_mcp_tilemap_create_rule_tile",
            Title = "Tilemap: Create Rule Tile",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "tilemap")]
        [System.ComponentModel.Description(
            "Create a RuleTile asset at the given asset_path. RuleTile is " +
            "shipped by com.unity.2d.tilemap.extras — when extras is not " +
            "installed, this tool returns a clear install error. Mutating: " +
            "runs the gate path; paths_hint includes the new asset path.")]
        public static string CreateRuleTile(
            string asset_path = null,
            string rule_tile_name = null,
            string default_sprite_asset_path = null,
            string[] paths_hint = null)
        {
#if !UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS
            // The core tilemap pack compiled in, but extras (RuleTile) did not.
            // Surface a clear install error rather than a missing-type compile
            // break — this is the two-defines-two-guards pattern from the plan.
            if (paths_hint == null || paths_hint.Length == 0)
                return TilemapJson.Error("paths_hint_required",
                    "tilemap_create_rule_tile is mutating; pass a non-empty paths_hint.");
            return TilemapJson.Error("tilemap_extras_required",
                "RuleTile ships in `com.unity.2d.tilemap.extras` — install it via " +
                "the Package Manager to enable rule tiles.");
#else
            if (paths_hint == null || paths_hint.Length == 0)
                return TilemapJson.Error("paths_hint_required",
                    "tilemap_create_rule_tile is mutating; pass a non-empty " +
                    "paths_hint that includes the new asset path.");

            if (string.IsNullOrEmpty(asset_path))
                return TilemapJson.Error("missing_parameter",
                    "'asset_path' is required (an 'Assets/.../*.asset' path).");

            if (!asset_path.EndsWith(".asset"))
                return TilemapJson.Error("invalid_parameter",
                    "'asset_path' must end with '.asset'.");

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(asset_path) != null)
                return TilemapJson.Error("already_exists",
                    $"An asset already exists at '{asset_path}'.");

            var ruleTile = ScriptableObject.CreateInstance<RuleTile>();
            if (!string.IsNullOrEmpty(rule_tile_name)) ruleTile.name = rule_tile_name;

            if (!string.IsNullOrEmpty(default_sprite_asset_path))
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(default_sprite_asset_path);
                if (sprite == null)
                    return TilemapJson.Error("sprite_not_found",
                        $"No Sprite found at '{default_sprite_asset_path}'.");
                ruleTile.m_DefaultSprite = sprite;
            }

            AssetDatabase.CreateAsset(ruleTile, asset_path);
            EditorUtility.SetDirty(ruleTile);

            var sb = new StringBuilder(160);
            sb.Append("\"ruleTile\":{");
            sb.Append("\"assetPath\":").Append(TilemapJson.Esc(asset_path)).Append(',');
            sb.Append("\"instanceId\":").Append(ruleTile.GetInstanceID()).Append(',');
            sb.Append("\"name\":").Append(TilemapJson.Esc(ruleTile.name));
            sb.Append('}');
            return TilemapJson.Ok(sb.ToString());
#endif
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static Vector3 ParseVector3(string s, Vector3 fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            var parts = s.Split(',');
            if (parts.Length != 3) return fallback;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return fallback;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return fallback;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z)) return fallback;
            return new Vector3(x, y, z);
        }

        private static string TargetNotFound()
            => TilemapJson.Error("target_not_found",
                "No GameObject resolved. Address by instance_id > path > name.");
    }
}
#endif
