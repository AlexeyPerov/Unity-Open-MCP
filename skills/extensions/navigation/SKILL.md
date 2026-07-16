# Unity Open MCP — Navigation (NavMesh) Extension

Skill for AI agents driving NavMesh / AI Navigation in a Unity project through the `unity-open-mcp` MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tools compile in
> only when the project has `com.unity.ai.navigation` installed (the bridge sets
> the `UNITY_OPEN_MCP_EXT_NAVIGATION` define automatically). Its tool group is
> **hidden** from `ListTools` until the connected session activates it.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The project has `com.unity.ai.navigation` installed. If `capabilities` reports
  the `navigation` group as `available: false`, install the package and let the
  bridge recompile.
- The `navigation` tool group is activated — call
  `unity_open_mcp_manage_tools(action="activate", group="navigation")` before
  invoking any `navigation_*` tool.
  Fresh sessions start with two default-on groups (`core` and `gate-and-verify`); activate the other groups you need on demand.

## Tool prefix

All tools in this pack use `unity_open_mcp_navigation_*`. Mutating tools accept the standard `paths_hint` (the host's scene path) and run the full gate path; read-only tools (`navigation_list`, `navigation_get`) are gate-free.

## Canonical workflow: walkable agent

1. **Discover** — `unity_open_mcp_navigation_list` to see existing surfaces / agents / links / modifiers.
2. **Add a surface** — `unity_open_mcp_navigation_surface_add` on a host GameObject. Set `agent_type: "Humanoid"` (default) and `collect_objects: "All"` to bake all scene geometry.
3. **(Optional) Configure** — `unity_open_mcp_navigation_set_bake_settings` for `collect_objects: "Volume"` + `collection_extent` to bake only a sub-region.
4. **Bake** — `unity_open_mcp_navigation_surface_bake`. This is the heavy op (EditorSettle lifecycle) — the dispatcher waits for asset refresh before returning. Returns `hasNavMeshData: true` + a `navMeshDataInstanceId`.
5. **Place an agent** — `unity_open_mcp_navigation_agent_add` on the GameObject that should walk. Configure `radius`, `height`, `speed`, `angular_speed`, `acceleration`, `stopping_distance`.
6. **Drive it** — `unity_open_mcp_navigation_agent_set_destination` with a world-space `x,y,z`. **Requires Play Mode** — the pathfinder only runs at runtime. Returns `pathStatus: Valid | Partial | Invalid` + `pathPending`.

### Verifying the bake

After bake, `navigation_get` on the surface host reports `hasNavMeshData: true`. If `hasNavMeshData: false`, the bake produced no geometry — check:

- The surface's `collect_objects` mode (Volume needs a valid `collection_extent` overlapping walkable geometry).
- The geometry has a collider and is on a layer included in the surface's `layerMask` (use `navigation_modify` on `NavMeshSurface` to set `layerMask`).
- The agent type is registered (open the Navigation window once to register the default "Humanoid" agent).

## Off-mesh traversal

For jumps, drops, gaps, or any traversal the bake cannot infer:

- `unity_open_mcp_navigation_link_add` on a GameObject between the two NavMesh positions. Set `start_pos` / `end_pos` (local space), `width` (0 = point-to-point), `bidirectional` (default true), `cost_modifier` (-1 = default).

For area-cost overrides on individual objects:

- `unity_open_mcp_navigation_modifier_add` — override the NavMesh area (e.g. mark a door as "Door" so it costs more to traverse) or `ignore: true` to skip the object during baking.
- `unity_open_mcp_navigation_modifier_volume_add` — re-tag the NavMesh inside a volume to a specific area (size + center in local space).

## Reflective mutation (niche fields)

When a typed mutator does not cover a field, use `unity_open_mcp_navigation_modify`:

```json
{
  "component_type": "NavMeshAgent",
  "fields_json": "[{\"field\":\"speed\",\"value\":5.5,\"type\":\"float\"},{\"field\":\"autoBraking\",\"value\":true,\"type\":\"bool\"}]",
  "paths_hint": ["Assets/Scenes/Game.unity"]
}
```

Each entry is `{ field, value, type? }` where `type` is `int | float | bool | string | vector` (default inferred from the field's current type). Per-field errors are accumulated — a single bad entry does not abort the batch. Prefer the typed tools for the common cases; reach for `modify` only for fields not covered by a typed mutator.

## Common recipes

### Bake a small playable level

1. `navigation_surface_add` on an empty GameObject named "NavMesh" with `collect_objects: "All"`.
2. `navigation_surface_bake` — wait for `hasNavMeshData: true`.
3. `navigation_agent_add` on the player GameObject with `radius: 0.5`, `speed: 3.5`.
4. Enter Play Mode (`unity_open_mcp_editor_set_state` with `state: "play"`).
5. `navigation_agent_set_destination` to a point inside the baked area.

### Two-tier level with a jump link

1. Bake two surfaces (one per tier) OR one surface collecting all geometry.
2. `navigation_link_add` on a GameObject positioned between the tiers with `start_pos: "0,0,0"` and `end_pos: "0,2,0"`.
3. Bake. The link is traversable in both directions by default.

### Avoid an obstacle without re-baking

1. `navigation_modifier_add` on the obstacle GameObject with `ignore: true`.
2. Re-bake the surface — the obstacle is skipped.

## Agent-sense pairing

- `unity_senses_spatial_query` (`action: "ground_check"`) finds the surface point below a target — useful for picking a valid destination before `navigation_agent_set_destination`.
- `unity_senses_screenshot` (view: "scene") visually confirms the bake (the NavMesh renders as a blue overlay in the Scene view).

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `navigation_surface_add` | yes | editor_settle | Idempotent — re-using reports `added:false`. |
| `navigation_set_bake_settings` | yes | editor_settle | Refuses if host has no NavMeshSurface. |
| `navigation_surface_bake` | yes | editor_settle | Heavy op — runs the bake synchronously. |
| `navigation_modifier_add` | yes | editor_settle | Area override or `ignore: true`. |
| `navigation_modifier_volume_add` | yes | editor_settle | Re-tags NavMesh inside a volume. |
| `navigation_link_add` | yes | editor_settle | Off-mesh traversal (jump / drop / gap). |
| `navigation_agent_add` | yes | editor_settle | Configure radius / speed / etc. |
| `navigation_agent_set_destination` | yes | none | **Play Mode only** — agent won't move in Edit Mode. |
| `navigation_list` | no | none | Lists every NavMesh component in the open scene(s). |
| `navigation_get` | no | none | Full detail for one target's NavMesh components. |
| `navigation_modify` | yes | editor_settle | Reflective field setter for niche fields. |

Address every target by `instance_id` > `path` > `name` (same model as `gameobject_*` / `component_*`). Every mutating tool requires a non-empty `paths_hint` scoped to the host's scene path — the gate has no whole-project fallback.
