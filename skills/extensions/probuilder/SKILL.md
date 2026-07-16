# Unity Open MCP — ProBuilder Extension

Skill for AI agents driving Unity ProBuilder in a project through the `unity-open-mcp` MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tools compile in
> only when the project has `com.unity.probuilder` installed (the bridge sets
> the `UNITY_OPEN_MCP_EXT_PROBUILDER` define automatically). Its tool group is
> **hidden** from `ListTools` until the connected session activates it.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The project has `com.unity.probuilder` installed. If `capabilities` reports
  the `probuilder` group as `available: false`, install the package and let the
  bridge recompile.
- The `probuilder` tool group is activated — call
  `unity_open_mcp_manage_tools(action="activate", group="probuilder")` before
  invoking any `probuilder_*` tool.
  Fresh sessions start with two default-on groups (`core` and `gate-and-verify`); activate the other groups you need on demand.
- The Unity project has `com.unity.probuilder` available.

## Tool prefix

All tools in this pack use `unity_open_mcp_probuilder_*`. Mutating tools target a scene GameObject — every mutator runs the full gate path with `paths_hint` scoped to the host's scene path; the read-only tool (`probuilder_get_mesh_info`) is gate-free.

## Face selection: indices vs direction

Every face-targeting tool (`extrude`, `delete_faces`, `set_face_material`) accepts **either**:

- `face_indices: [0, 2, 4]` — explicit indices. Pass them if you have called `get_mesh_info` and know exactly which faces you want.
- `face_direction: "Up"` — semantic selection by face normal. One of `Up` / `Down` / `Left` / `Right` / `Forward` / `Back`.

Passing **both** returns `conflicting_selection`. Passing **neither** returns `missing_parameter`. Out-of-range indices return `invalid_face_indices` (with the offending values and the valid range). Direction selection uses a ~45° tolerance — faces whose normal is close to the axis are included; faces that match no axis are bucketed under `other` in `get_mesh_info`.

> Prefer `face_direction` for the common cases (extrude the top, delete the bottom, paint the sides). Reach for `face_indices` only when you need surgical precision on a single face.

## Canonical workflow: a shaped building block

1. **Create a shape** — `unity_open_mcp_probuilder_create_shape` with `shape_type: "Cube"` (or `Cylinder`, `Sphere`, `Stair`, …). Capture the returned `instanceId` — it is how you address the mesh in subsequent calls.
2. **Inspect** — `unity_open_mcp_probuilder_get_mesh_info` to confirm face / vertex counts and the face-direction summary (which indices face each axis).
3. **Extrude** — `unity_open_mcp_probuilder_extrude` with `face_direction: "Up"`, `distance: 1.0`. New geometry is created along the face normal.
4. **Paint** — `unity_open_mcp_probuilder_set_face_material` with `face_direction: "Up"`, `material_path: "Assets/Materials/Roof.mat"`. The material is added to the renderer's material array if it is not already present.

### Always inspect before mutating

`get_mesh_info` is cheap (gate-free, read-only). Call it first when you are unsure about:

- whether the target actually has a `ProBuilderMesh` (returns `component_not_found` otherwise),
- which indices map to which direction,
- the current face count (so you do not delete every face — `delete_faces` refuses if you try).

## Shape types

`create_shape` accepts these `ShapeType` values (case-insensitive):

`Cube`, `Sphere`, `Prism`, `Cylinder`, `Plane`, `Pipe`, `Cone`, `Tetrahedron`, `Quad`, `Door`, `Stair`, `Arch`, `Sprite`, `Torus`.

## Common recipes

### A platform with a painted top

1. `probuilder_create_shape` → `Cube`, `name: "Platform"`, `scale: "4,0.5,4"`.
2. `probuilder_set_face_material` → `face_direction: "Up"`, `material_path: "Assets/Materials/Grass.mat"`.

### Hollow box (delete the top face)

1. `probuilder_create_shape` → `Cube`.
2. `probuilder_delete_faces` → `face_direction: "Up"`. **Destructive** — `editor_undo` recovers it if you overshoot.
3. `probuilder_get_mesh_info` confirms `faceCount: 5`.

### Two-tier ledge

1. `probuilder_create_shape` → `Cube`, `scale: "1,1,1"`.
2. `probuilder_extrude` → `face_direction: "Up"`, `distance: 1.0` (raises the top).
3. `probuilder_extrude` → `face_direction: "Up"`, `distance: 0.5` again to add a second tier.

## Error codes

| Code | Meaning |
|---|---|
| `paths_hint_required` | Mutating tool called with no `paths_hint`. |
| `target_not_found` | No GameObject resolved by `instance_id` / `path` / `name`. |
| `parent_not_found` | `create_shape` `parent_path` did not resolve. |
| `component_not_found` | Target has no `ProBuilderMesh` (or no `MeshRenderer` for `set_face_material`). |
| `invalid_shape_type` / `invalid_extrude_method` / `invalid_face_direction` | Unknown enum value. |
| `missing_parameter` | Missing `face_indices` AND `face_direction`, or empty `material_path`. |
| `conflicting_selection` | Both `face_indices` AND `face_direction` passed. |
| `invalid_face_indices` | Index out of range (response includes the valid range). |
| `no_faces_in_direction` | No faces match the requested `face_direction`. |
| `cannot_delete_all_faces` | `delete_faces` would remove every face — refused. |
| `material_not_found` | No Material at `material_path` (as asset path or by name search). |
| `extrude_failed` / `delete_failed` / `creation_failed` | ProBuilder API threw. |

## Tool reference

| Tool | Mutating | Destructive | Lifecycle | Notes |
|---|---|---|---|---|
| `probuilder_create_shape` | yes | no | editor_settle | Creates a new ProBuilderMesh GameObject. |
| `probuilder_get_mesh_info` | no | no | none | Face / vertex / edge counts + direction summary. |
| `probuilder_extrude` | yes | no | editor_settle | Extrudes faces by index or direction. |
| `probuilder_delete_faces` | yes | **yes** | editor_settle | Deletes faces (refuses to delete all). |
| `probuilder_set_face_material` | yes | no | editor_settle | Assigns a Material to faces. |

Address every target by `instance_id` > `path` > `name` (same model as `gameobject_*` / `component_*`). Every mutating tool requires a non-empty `paths_hint` scoped to the host's scene path — the gate has no whole-project fallback.
