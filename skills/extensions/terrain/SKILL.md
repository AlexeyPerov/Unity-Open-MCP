# Unity Open MCP — Terrain Extension

Skill for AI agents authoring Unity Terrain (heightmaps, splatmaps, trees,
neighbor stitching) in a project through the `unity-open-mcp` MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tools use the
> built-in engine modules (`UnityEngine.TerrainModule` for Terrain /
> TerrainData, `UnityEngine.CoreModule` for TreePrototype / TerrainLayer) — no
> Unity package install is required, and they compile into every bridge build.
> Its tool group is **hidden** from `ListTools` until the connected session
> activates it.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The `terrain` tool group is activated — call
  `unity_open_mcp_manage_tools(action="activate", group="terrain")` before
  invoking any Terrain tool. Fresh sessions start with only `core` visible.
  Because these types are built-in, `capabilities` always reports the
  `terrain` group as `available: true` (no `domainDefine`).

## Tool prefix

All five tools share the `unity_open_mcp_terrain_` prefix and one tool group:

- `unity_open_mcp_terrain_create` — allocate TerrainData (+ optional `.asset`)
  and a Terrain GameObject.
- `unity_open_mcp_terrain_set_heights` — set a heightmap region from a 2D
  array of normalized 0-1 values.
- `unity_open_mcp_terrain_paint_layer` — paint one terrain layer's splat from
  a 2D alphamap; seeds a new `TerrainLayer` when the index is new.
- `unity_open_mcp_terrain_place_trees` — place tree instances (position + scale
  + rotation) against a prototype index; seeds a prototype from a prefab when
  the index is new.
- `unity_open_mcp_terrain_set_neighbors` — set the top / bottom / left / right
  neighbor Terrains for LOD stitching.

All five tools are mutating and accept the standard `paths_hint`; they run the
full gate path. There are no read-only members in this group — read terrain
state with `unity_open_mcp_component_get` / `unity_open_mcp_object_get_data`.

Every mutating tool requires a non-empty `paths_hint` scoped to the host scene
path — the gate has no whole-project fallback.

## Large arrays: tile, don't dump

Heightmap and splat arrays are **capped at 513×513 per call**. Larger arrays
return `invalid_heights_array` / `invalid_alphamap_array` with a tiling hint
pointing you at `x_offset` / `y_offset` region writes.

Work in tiles: a 129×129 default heightmap is a single call; a 513×513 region
is one call; a full 2049×2049 heightmap is ~16 calls (e.g. four 513-row bands
of four 513-column tiles each, advancing `x_offset` and `y_offset` per call).
This keeps each mutation fast, gate-cheap, and reviewable — the gate's
checkpoint → mutate → validate → delta flow rewards small, scoped writes over
one giant dump.

## Canonical workflow: a textured terrain with trees

1. **Create** — `unity_open_mcp_terrain_create` with `heightmap_resolution: 129`
   (default), `width` / `length` / `height` in world units, optional `position`
   and `asset_path` (when you want the TerrainData saved to disk). Returns the
   terrain's `instance_id` + `path` — capture them for the next calls.
2. **Sculpt** — `terrain_set_heights` with `x_offset` / `y_offset` and a 2D
   `heights` array of normalized 0-1 values. Tile large changes (see above).
3. **Texture** — `terrain_paint_layer` per layer. Pass `layer_index`, an
   `alphamap` (2D weights), and (for a new index) `layer_path` to seed a new
   `TerrainLayer` asset. Repeat per layer (up to 8 for the standard terrain
   shader).
4. **Plant** — `terrain_place_trees` with a `tree_prototype_index`, an
   `instances` array (`{position, height_scale, width_scale, rotation}`), and
   (for a new index) `prototype_prefab_path` to seed the prototype from a
   `.prefab`.
5. **Stitch** (multi-tile worlds) — `terrain_set_neighbors` to wire the four
   sides for LOD continuity across adjacent terrains.

## Coordinate systems

- **Heightmap values**: normalized 0-1 (0 = sea level, 1 = the TerrainData's
  `height` in world units). The 2D array is row-major
  `[[row0...],[row1...],...]`; row index is Y (heightmap Z), column index is
  X (heightmap X).
- **Alphamap values**: normalized 0-1 weights per layer. Each cell's weights
  across all layers should sum to 1 for correct blending (the tool writes one
  layer per call; re-read the slice with `component_get` if you need to blend
  multiple layers in one region).
- **Tree positions**: normalized 0-1 on the terrain surface — `position: "x,z"`
  where `(0,0)` is one corner and `(1,1)` the opposite. Y is sampled
  automatically.
- **Neighbors**: resolved by `instance_id` > `path` (a Terrain GameObject's
  hierarchy path). A side with no resolver clears it; a terrain cannot be its
  own neighbor.

## Asset vs scene mutations

- `terrain_create` with an `asset_path` mutates an asset (the TerrainData
  `.asset`) **and** the scene (it adds the Terrain GameObject). `paths_hint`
  must cover **both** the scene path and the `.asset` path so the gate
  validates both. When `asset_path` is omitted, the TerrainData is in-scene
  only and is **not saved to disk** — fine for prototyping, lost on scene
  close without a save.
- `terrain_paint_layer` with a new `layer_path` under `Assets/` creates a
  `.terrainlayer` asset — include it in `paths_hint` alongside the scene path.
- `terrain_set_heights`, `terrain_place_trees`, and `terrain_set_neighbors`
  mutate only the scene's TerrainData / Terrain — `paths_hint` is the host
  scene path.

## Common recipes

### A 129×129 terrain with a central hill

```text
1. terrain_create { terrain_name: "HillTerrain", heightmap_resolution: 129,
                    width: 500, length: 500, height: 200,
                    paths_hint: ["Assets/Scenes/Main.unity"] }
   → capture instance_id / path
2. terrain_set_heights { instance_id: <id>, x_offset: 48, y_offset: 48,
     heights: [[0.1,0.2,...33 values...],[0.2,0.4,...],...33 rows],
     paths_hint: ["Assets/Scenes/Main.unity"] }
```

A 33×33 region at offset (48,48) sits in the middle of a 129×129 heightmap.

### Two grass + rock layers

```text
1. terrain_paint_layer { instance_id: <id>, layer_index: 0,
     alphamap: [[1,...],[1,...],...], layer_path: "Assets/Terrain/Grass.terrainlayer",
     paths_hint: ["Assets/Scenes/Main.unity", "Assets/Terrain/Grass.terrainlayer"] }
2. terrain_paint_layer { instance_id: <id>, layer_index: 1,
     alphamap: [[0.2,...],[0.5,...],...], layer_path: "Assets/Terrain/Rock.terrainlayer",
     paths_hint: ["Assets/Scenes/Main.unity", "Assets/Terrain/Rock.terrainlayer"] }
```

Each `terrain_paint_layer` call writes one layer's weights into the region; the
existing layers keep their weights in the slice.

### Scatter trees from a prefab

```text
terrain_place_trees { instance_id: <id>, tree_prototype_index: 0,
  prototype_prefab_path: "Assets/Trees/Pine.prefab",
  instances: [
    { position: "0.5,0.5", height_scale: 1, width_scale: 1, rotation: 0 },
    { position: "0.6,0.5", height_scale: 0.9, width_scale: 1, rotation: 45 }
  ],
  paths_hint: ["Assets/Scenes/Main.unity"] }
```

The first call for a new prototype index seeds it from the prefab; subsequent
calls with the same index reuse it (omit `prototype_prefab_path`).

### Stitch a 2×2 terrain grid

```text
# Top-left tile's right neighbor = top-right tile, bottom neighbor = bottom-left... etc.
terrain_set_neighbors { instance_id: <TL_id>, right_instance_id: <TR_id>,
  bottom_instance_id: <BL_id>, paths_hint: ["Assets/Scenes/Main.unity"] }
```

Repeat for each tile's relevant sides. The Terrain component exposes
`topNeighbor` / `bottomNeighbor` / `leftNeighbor` / `rightNeighbor` — verify
with `component_get`.

## Agent-sense pairing

- `unity_open_mcp_component_get` (component_type `Terrain` or `TerrainCollider`)
  reads the raw fields after a mutate (complementary to the structured state
  returned by the terrain tools).
- `unity_open_mcp_object_get_data` on the TerrainData asset path reads
  heightmap / alphamap resolutions, layer count, tree counts.
- `unity_senses_screenshot` (view: `"scene"`) visually confirms the heightmap
  / splat / tree changes in the Scene view.

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `terrain_create` | yes | editor_settle | Allocates TerrainData + Terrain GameObject. Optional `.asset` save. |
| `terrain_set_heights` | yes | editor_settle | Region write; 513×513 per-call cap; tile large writes. |
| `terrain_paint_layer` | yes | editor_settle | One layer per call; seeds a new TerrainLayer when the index is new (≤8). |
| `terrain_place_trees` | yes | editor_settle | Append instances; seeds a prototype from a prefab when the index is new. |
| `terrain_set_neighbors` | yes | editor_settle | Idempotent; clears a side when no resolver is given. |

Address every terrain host by `instance_id` > `path` > `name` (same model as
`gameobject_*` / `component_*`). Every mutating tool requires a non-empty
`paths_hint` scoped to the host scene path (+ asset paths when assets are
written) — the gate has no whole-project fallback.
