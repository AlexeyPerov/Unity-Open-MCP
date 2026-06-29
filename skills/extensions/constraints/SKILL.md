# Unity Open MCP — Constraints & LOD Extension

Skill for AI agents driving Unity animation constraints (`PositionConstraint` /
`RotationConstraint` / `AimConstraint` / `ParentConstraint` / `ScaleConstraint`)
and Level-of-Detail groups (`LODGroup`) in a project through the `unity-open-mcp`
MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tools use the
> built-in engine modules (`UnityEngine.AnimationModule` for the constraint
> components, `UnityEngine.CoreModule` for `LODGroup`) — no Unity package
> install is required, and they compile into every bridge build. Its tool
> group is **hidden** from `ListTools` until the connected session activates
> it.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The `constraints` tool group is activated — call
  `unity_open_mcp_manage_tools(action="activate", group="constraints")` before
  invoking any Constraints & LOD tool. Fresh sessions start with only `core`
  visible. Because these types are built-in, `capabilities` always reports the
  `constraints` group as `available: true` (no `domainDefine`).

## Tool prefixes

Two prefixes share the `constraints` group (one group covers both concerns —
they are small and closely related):

- `unity_open_mcp_constraint_add` — add an animation constraint component to a
  host GameObject.
- `unity_open_mcp_lod_group_configure` — configure a `LODGroup` on a host.
- `unity_open_mcp_lod_add_level` — add or replace a `LOD` entry at an index.

All three tools are mutating and accept the standard `paths_hint`; they run
the full gate path. There are no read-only members in this group — read
constraint / LODGroup state with `unity_open_mcp_component_get`.

Every mutating tool requires a non-empty `paths_hint` scoped to the host scene
path — the gate has no whole-project fallback.

## Canonical workflow: an aim constraint

1. **Discover** — `unity_open_mcp_component_list_all` (filter `AimConstraint`)
   or `unity_open_mcp_gameobject_find` to locate existing hosts. Address any
   host by `instance_id` > `path` > `name`.
2. **Add the constraint** — `unity_open_mcp_constraint_add` on the host with
   `constraint_type: "AimConstraint"`, `source_path` (or
   `source_instance_id` / `source_name`) pointing at the target to aim at,
   `weight` (0-1, default 1), `constraint_active` (default true). Returns the
   added constraint's state. Idempotent — re-using an existing constraint of
   the same type reports `added:false` (source / weight / activation are still
   applied).
3. **Tune it** — `unity_open_mcp_component_modify` for fields this pack does
   not surface directly (e.g. `aimVector` / `upVector` on `AimConstraint`,
   `rotationAxes` on `RotationConstraint`, per-source weights via the sources
   array).

## Constraint types

`constraint_add` supports these `constraint_type` values:

| `constraint_type` | Constrained property | Notes |
|---|---|---|
| `PositionConstraint` | position | Tracks the source's position. |
| `RotationConstraint` | rotation | Tracks the source's rotation. |
| `AimConstraint` | rotation (aim) | Aims at the source; set `aimVector` / `upVector` via `component_modify`. |
| `ParentConstraint` | position + rotation | Parents to the source (no actual reparent). |
| `ScaleConstraint` | local scale | Scales toward the source's scale. |

`LookAtConstraint` is the legacy non-`IConstraint` variant and is intentionally
omitted — only the five `IConstraint`-derived types above are supported. The
tool returns `invalid_constraint_type` for any other name.

## Source + weight + activation

- `source_path` / `source_instance_id` / `source_name` — the constrained-to
  target. Resolved to a GameObject; its `Transform` is taken (constraints
  source off Transforms). Optional — you can add the component first and wire
  the source later via `component_modify`.
- `weight` — the source weight (0-1, clamped). Applied as the per-source
  weight on the seeded `ConstraintSource`. Ignored when no source is provided.
- `constraint_active` — whether the constraint is active after add (default
  true). A constraint can be added inactive (`constraint_active: false`) so you
  can finish wiring before it takes effect.

## Canonical workflow: a LOD group

1. **Add the LODGroup** — `unity_open_mcp_lod_group_configure` on the host
   GameObject (the root that owns the LOD children). Optional: `fade_mode`
   (`None` | `SpeedTree` | `CrossFade` — omit to leave the existing value),
   `animate_cross_fading` (default false), `lod_count` (allocates the LOD
   array with that many levels; renderers start empty). Idempotent — re-using
   an existing LODGroup reports `added:false` (configuration still applied).
2. **Wire each level** — `unity_open_mcp_lod_add_level` per level. Pass
   `index` (within the array → replace; == `lodCount` → append),
   `screen_relative_transition_height` (0-1, clamped), and `renderers` (an
   array of GameObject paths — each must carry a `Renderer`, usually a
   `MeshRenderer` on a child mesh). Entries that fail to resolve are reported
   in `unresolvedRenderers`.
3. **Verify** — `unity_open_mcp_component_get` on the LODGroup, or
   `unity_senses_screenshot` (view: `"scene"`) to see the LOD fade in the
   Scene view.

### LOD array shape

Unity's LOD array must be **sorted descending** by
`screenRelativeTransitionHeight`, and the last level is implicitly the "culled"
band (height 0). When you allocate via `lod_count`, the tool seeds descending
placeholder heights (e.g. `lod_count: 3` → 0.67, 0.33, 0.0) so `SetLODs`
accepts the array. Replace each level's height via `lod_add_level` to match
your art-authored thresholds.

`lod_count` is capped at 8 (Unity's LODGroup maximum). The tool returns
`invalid_lod_count` for 0 or > 8.

## Common recipes

### Aim a turret at a target

1. `constraint_add` on the turret GameObject with
   `constraint_type: "AimConstraint"`, `source_path` pointing at the target,
   `weight: 1`, `constraint_active: true`.
2. `component_modify` on the turret with `component_type: "AimConstraint"`,
   `fields_json: "[{\"field\":\"aimVector\",\"value\":\"0,1,0\",\"type\":\"vector\"}]"`.

### Pin a prop to a bone (no reparent)

1. `constraint_add` on the prop with `constraint_type: "ParentConstraint"`,
   `source_path` pointing at the bone. The prop stays in its own hierarchy
   slot but follows the bone's position + rotation.

### Three-tier LOD on a character

1. `lod_group_configure` on the character root with `lod_count: 3`,
   `fade_mode: "CrossFade"`, `animate_cross_fading: true`.
2. `lod_add_level` with `index: 0`, `screen_relative_transition_height: 0.6`,
   `renderers: ["Character/LOD0"]` (the high-poly mesh).
3. `lod_add_level` with `index: 1`, `screen_relative_transition_height: 0.2`,
   `renderers: ["Character/LOD1"]` (the mid-poly mesh).
4. `lod_add_level` with `index: 2`, `screen_relative_transition_height: 0.0`,
   `renderers: ["Character/LOD2"]` (the low-poly mesh — culled below 0).

## Agent-sense pairing

- `unity_open_mcp_component_get` reads the raw constraint / LODGroup fields
  after a mutate (complementary to the structured state returned by the
  constraint tools).
- `unity_senses_screenshot` (view: `"scene"`) visually confirms the LOD fade
  in the Scene view (the LOD gizmo draws the transition spheres).

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `constraint_add` | yes | editor_settle | Idempotent per type — re-using reports `added:false`. Optional source + weight + activation. |
| `lod_group_configure` | yes | editor_settle | Idempotent — re-using reports `added:false`. Allocates the LOD array when `lod_count` is set. |
| `lod_add_level` | yes | editor_settle | Replace-in-place (index < lodCount) or append (index == lodCount). |

Address every target by `instance_id` > `path` > `name` (same model as
`gameobject_*` / `component_*`). Every mutating tool requires a non-empty
`paths_hint` scoped to the host scene path — the gate has no whole-project
fallback.
