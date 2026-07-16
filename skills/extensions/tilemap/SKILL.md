# Unity Open MCP — Tilemap Extension

Skill for AI agents driving the Unity Tilemap packages
(`com.unity.2d.tilemap` + optional `com.unity.2d.tilemap.extras` for RuleTile)
in a Unity project through the `unity-open-mcp` MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tools compile
> in only when the project has `com.unity.2d.tilemap` installed (the bridge
> sets the `UNITY_OPEN_MCP_EXT_TILEMAP` define automatically). The RuleTile
> tool additionally requires `com.unity.2d.tilemap.extras` — when extras is
> absent, `tilemap_create_rule_tile` returns a clear install error
> (`tilemap_extras_required`) instead of failing silently. The tool group is
> **hidden** from `ListTools` until the connected session activates it.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The project has `com.unity.2d.tilemap` installed (it ships with the 2D
  template; verify in the Package Manager). For rule tiles, additionally
  install `com.unity.2d.tilemap.extras`. If `capabilities` reports the
  `tilemap` group as `available: false`, install the package and let the
  bridge recompile.
- The `tilemap` tool group is activated — call
  `unity_open_mcp_manage_tools(action="activate", group="tilemap")` before
  invoking any `tilemap_*` tool.
  Fresh sessions start with two default-on groups (`core` and `gate-and-verify`); activate the other groups you need on demand.

## Tool prefix

All tools in this pack use `unity_open_mcp_tilemap_*`. All five tools are
mutating and run the full gate path; `paths_hint` is the host scene path
(or includes the asset path for the asset-create tools).

## Vocabulary

A **Grid** is the parent GameObject that lays out cells. Each **Tilemap** is a
child GameObject holding a `Tilemap` component (the cell data) and a
`TilemapRenderer` (the drawing). Multiple Tilemaps can share one Grid (e.g.
"Ground", "Walls", "Decorations" layers).

A **Tile** is a `ScriptableObject` asset (a `.asset`) that bundles a Sprite +
collider shape. A **TileBase** is the abstract base (Tile is one
implementation; RuleTile is another). Painting is done by cell coordinate
(`x, y, z`), where `x` / `y` are the grid cell and `z` is the layer plane
(usually 0).

A **RuleTile** (from `com.unity.2d.tilemap.extras`) is a smarter tile whose
sprite is selected by its neighbors — perfect for walls, roads, and water
where the visible tile depends on adjacency.

## Canonical workflow: a 2D level

1. **Create the grid** — `unity_open_mcp_tilemap_create` adds a Grid GameObject
   to the active scene with a child Tilemap + TilemapRenderer. Returns the
   Grid + Tilemap instance ids + paths. Note the Tilemap `instanceId`.
2. **Create tile assets** — `unity_open_mcp_tilemap_create_tile_asset` writes
   a `Tile` asset at an `Assets/.../*.asset` path. Optionally seed its sprite
   via `sprite_asset_path`. One Tile asset per distinct visual.
3. **Paint single tiles** — `unity_open_mcp_tilemap_set_tile` paints a tile at
   a cell coordinate. Address the Tilemap by `instance_id` and the tile by
   `tile_asset_path`.
4. **Fill regions** — `unity_open_mcp_tilemap_box_fill` paints a rectangle.
   `x1, y1` and `x2, y2` are the inclusive opposite corners.
5. **Auto-tile with RuleTile** (optional, requires extras) —
   `unity_open_mcp_tilemap_create_rule_tile` writes a RuleTile asset. Paint it
   with `tilemap_set_tile` / `tilemap_box_fill` like any other tile; the rule
   picks the correct sprite based on neighbors.

## Common recipes

### A ground layer

1. `tilemap_create` with `grid_name: "Grid"`, `tilemap_name: "Ground"`.
2. `tilemap_create_tile_asset` with `asset_path: "Assets/Tiles/Ground.asset"`,
   `sprite_asset_path: "Assets/Sprites/ground.png"`.
3. `tilemap_box_fill` with `tile_asset_path: "Assets/Tiles/Ground.asset"`,
   `x1: -10, y1: -10, x2: 10, y2: 10`.

### A walls layer using RuleTile

1. `tilemap_create` with `grid_name: "Grid"`, `tilemap_name: "Walls"`,
   `parent_path: "Grid"` (reuse the existing Grid).
2. `tilemap_create_rule_tile` with `asset_path: "Assets/Tiles/WallRuleTile.asset"`.
3. Configure the rule tile's rules via the Inspector (the typed surface
   doesn't cover rule editing — set up adjacency rules manually or via
   `execute_csharp`).
4. `tilemap_set_tile` to paint individual wall cells; the rule tile picks the
   correct sprite.

## Agent-sense pairing

- `unity_senses_screenshot` (view: "scene") visually confirms the painted
  layout.
- `unity_open_mcp_assets_refresh` after creating tile assets ensures the
  AssetDatabase picks them up before `set_tile` references them.

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `tilemap_create` | yes | editor_settle | Grid + child Tilemap GameObject. |
| `tilemap_set_tile` | yes | editor_settle | Paint one cell. |
| `tilemap_box_fill` | yes | editor_settle | Paint a rectangle (inclusive). |
| `tilemap_create_tile_asset` | yes | editor_settle | New `Tile` .asset. |
| `tilemap_create_rule_tile` | yes | editor_settle | New `RuleTile` .asset (needs extras). |

Address every Tilemap by `instance_id` > `path` > `name`. Every mutating tool
requires a non-empty `paths_hint` scoped to the host scene path (or the asset
path for the asset-create tools) — the gate has no whole-project fallback.
