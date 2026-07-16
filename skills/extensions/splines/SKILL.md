# Unity Open MCP — Splines Extension

Skill for AI agents driving the Unity Splines package (`com.unity.splines`) in a
Unity project through the `unity-open-mcp` MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tools compile in
> only when the project has `com.unity.splines` installed (the bridge sets the
> `UNITY_OPEN_MCP_EXT_SPLINES` define automatically — no manual scripting-define
> write). Its tool group is **hidden** from `ListTools` until the connected
> session activates it.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The project has `com.unity.splines` installed. If `capabilities` reports the
  `splines` group as `available: false`, install the package and let the bridge
  recompile.
- The `splines` tool group is activated — call
  `unity_open_mcp_manage_tools(action="activate", group="splines")` before
  invoking any `splines_*` tool.
  Fresh sessions start with two default-on groups (`core` and `gate-and-verify`); activate the other groups you need on demand.

## Tool prefix

All tools in this pack use `unity_open_mcp_splines_*`. Mutating tools accept the
standard `paths_hint` (the host's scene path) and run the full gate path;
read-only tools (`splines_evaluate`, `splines_get_knots`) are gate-free.

## Vocabulary

A **SplineContainer** is a `MonoBehaviour` component that holds one or more
**Spline** objects. Each spline is an ordered list of **BezierKnot**s — every
knot has a **Position**, **Rotation**, **TangentIn**, **TangentOut**, and a
**TangentMode** that controls how the tangents are computed:

- **AutoSmooth** — tangents auto-computed for a smooth curve through the knot
  (the most common choice).
- **BezierSmooth** — smooth, with adjustable tangent length.
- **Broken** — the two tangents are independent.
- **Mirrored** — the two tangents mirror each other across the knot.
- **Linear** — sharp corners (straight segments).

## Canonical workflow: a path through points

1. **Create a container** — `unity_open_mcp_splines_container_create` adds a new
   GameObject with a `SplineContainer` to the active scene. It is initialized
   with one empty primary spline (`spline_index` 0). Returns the new
   GameObject's `instanceId` + `path` — use the `instanceId` to address it next.
2. **Add knots** — `unity_open_mcp_splines_add_knot` appends a `BezierKnot`.
   `position` (`"x,y,z"`) is required; `tangent_mode` is optional
   (`"AutoSmooth"` is the friendliest default). Each call returns the new knot
   `index`. A path needs at least two knots to be evaluable.
3. **Inspect** — `unity_open_mcp_splines_get_knots` lists every knot's position,
   rotation, tangents, and tangent mode. Use it to discover valid `knot_index`
   values before calling `set_knot` / `set_tangent_mode`.
4. **Sample** — `unity_open_mcp_splines_evaluate` at `t` (0..1) returns the
   world-space position, normalized tangent (direction), and up vector — pair it
   with `gameobject_create` / `execute_csharp` to place objects along the path.

### Configuring the curve shape

- `unity_open_mcp_splines_set_knot` replaces a knot (by `knot_index`). Omitted
  fields keep the current knot's value — call it to move a knot, change its
  rotation, or hand-set its tangents.
- `unity_open_mcp_splines_set_tangent_mode` sets the mode for one knot
  (`knot_index`) or the whole spline (`knot_index: -1`). For a closed loop, set
  `closed: true` at `container_create` time.

## Reflective mutation (niche container fields)

When a typed mutator does not cover a serialized `SplineContainer` field, use
`unity_open_mcp_splines_modify`:

```json
{
  "instance_id": 12345,
  "fields_json": "[{\"field\":\"someField\",\"value\":true,\"type\":\"bool\"}]",
  "paths_hint": ["Assets/Scenes/Game.unity"]
}
```

Each entry is `{ field, value, type? }` where `type` is
`int | float | bool | string | vector` (default inferred from the field's current
type). Per-field errors are accumulated — a single bad entry does not abort the
batch. **Do not use `modify` for knot fields** — use `set_knot` /
`set_tangent_mode` instead; `modify` targets the container component, not the
spline knot collection.

## Common recipes

### A smooth camera path

1. `splines_container_create` with `name: "CameraPath"`. Note the `instanceId`.
2. `splines_add_knot` four times with positions and `tangent_mode: "AutoSmooth"`.
3. `splines_get_knots` to confirm the knot order.
4. Loop `splines_evaluate` at `t = 0, 0.25, 0.5, 0.75, 1.0` to place keyframed
   camera positions, or drive a dolly at runtime by sampling `t` over time.

### Close a loop

1. `splines_container_create` with `closed: true`, then add ≥3 knots.
2. The spline connects the last knot back to the first automatically.

## Agent-sense pairing

- `unity_senses_screenshot` (view: "scene") visually confirms the spline shape —
  the editor renders splines as a colored curve with knot gizmos.
- `unity_senses_spatial_query` can supply candidate world positions to feed into
  `splines_add_knot`.

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `splines_container_create` | yes | editor_settle | New GameObject + SplineContainer. |
| `splines_add_knot` | yes | editor_settle | Append a BezierKnot; `position` required. |
| `splines_set_knot` | yes | editor_settle | Replace a knot by `knot_index`. |
| `splines_set_tangent_mode` | yes | editor_settle | One knot (`knot_index`) or whole spline (`-1`). |
| `splines_evaluate` | no | none | Position + tangent + up at ratio `t`. |
| `splines_get_knots` | no | none | List every knot's full detail. |
| `splines_modify` | yes | editor_settle | Reflective field setter for container fields. |

Address every target by `instance_id` > `path` > `name` (same model as
`gameobject_*` / `component_*`). Every mutating tool requires a non-empty
`paths_hint` scoped to the host's scene path — the gate has no whole-project
fallback.
