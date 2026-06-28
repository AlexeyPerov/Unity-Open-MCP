# Unity Open MCP — Lighting Extension

Skill for AI agents driving Unity lighting (per-Light / ReflectionProbe / skybox)
in a project through the `unity-open-mcp` MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tools use the
> built-in lighting module (`Light` / `ReflectionProbe` / `RenderSettings` /
> `Lightmapping`) — no Unity package install is required, and they compile into
> every bridge build. Its tool group is **hidden** from `ListTools` until the
> connected session activates it.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The `lighting` tool group is activated — call
  `unity_open_mcp_manage_tools(action="activate", group="lighting")` before
  invoking any lighting tool. Fresh sessions start with only `core` visible.
  Because lighting is built-in, `capabilities` always reports the `lighting`
  group as `available: true` (no `domainDefine`).

## Tool prefixes

Three prefixes share the `lighting` group:

- `unity_open_mcp_light_*` — per-Light manipulation (add / set / modify).
- `unity_open_mcp_reflection_probe_*` — probe bake (long mutation) + read.
- `unity_open_mcp_skybox_*` — skybox assignment + read.

Mutating tools accept the standard `paths_hint` and run the full gate path;
read-only tools (`reflection_probe_get`, `skybox_get`) are gate-free.

## Layered with project-settings lighting

The `lighting` group covers **per-object** lighting (a Light component on a
GameObject, a probe, the scene skybox). It sits on top of the
**project-settings** lighting surface already in the `build-settings` group:

- `settings_get_lighting` / `settings_set_lighting` — `RenderSettings` ambient
  mode/color, fog (mode/density/color/distance). Scene-scoped project settings.
- `light_*` / `reflection_probe_*` / `skybox_*` (this group) — per-Light fields,
  probe bake, skybox material assignment.

Use the project-settings tools for ambient/fog; use this group for individual
lights, probes, and the skybox.

## Canonical workflow: a lit scene

1. **Discover** — `unity_open_mcp_component_list_all` (filter `Light`) or
   `unity_open_mcp_gameobject_find` to locate existing lights. Address any host
   by `instance_id` > `path` > `name`.
2. **Add a light** — `unity_open_mcp_light_add` on a GameObject. Set
   `light_type` (`Spot` | `Point` | `Directional` | `Area` | `Rectangle`,
   default `Directional`), and optionally `color` (`"r,g,b,a"` 0-1),
   `intensity`, `range` (Point/Spot), `spot_angle` (Spot). Returns the Light
   state. Idempotent — re-using an existing Light reports `added:false`.
3. **Tune it** — `unity_open_mcp_light_set` for the common fields (`color`,
   `intensity`, `range`, `spot_angle`, `shadows`, `render_mode`,
   `culling_mask`). Each field is optional — omit to leave unchanged.
4. **Niche fields** — `unity_open_mcp_light_modify` when `light_set` does not
   cover a field (reflective field patch, typed to `Light` with enum-name
   support for `LightType` / `LightShadows` / `RenderMode`).

## Reflection probe bake (long mutation)

`unity_open_mcp_reflection_probe_bake` bakes a `ReflectionProbe`. `bake_mode`:

- `realtime` — `ReflectionProbe.Bake()` into the probe's runtime texture.
- `baked` — `Lightmapping.BakeAsync()` (a full lightmap bake including baked
  reflection probes).
- `custom` — `Lightmapping.BakeReflectionProbeSnapshot()` into a named cubemap
  asset. Pass `target_path` (an `Assets/`-rooted `.cubemap` path); the asset is
  created if absent.

The bake can take seconds. The tool routes through the gate with the
`editor_settle` lifecycle — the dispatcher waits for the bake + asset refresh to
complete before returning, so the next mutation sees the finished bake. This is
the documented advantage over ungated bake tools (no settle window). For
`custom` mode, `paths_hint` must include the probe's scene path **and** the
output cubemap asset path.

Read probe settings first with `unity_open_mcp_reflection_probe_get`
(gate-free): `mode`, `resolution`, `hdr`, `clearFlags`, `importance`, `size`,
near/far clip, and the baked cubemap path.

## Skybox assignment

`unity_open_mcp_skybox_set` assigns `RenderSettings.skybox` from a material
asset path (`Assets/.../*.mat`), or clears it when `material_path` is null. The
skybox is a scene-environment setting — the active scene is marked dirty so the
write persists (call `scene_save` to commit), and `DynamicGI.UpdateEnvironment`
is invoked to refresh ambient/indirect lighting. `paths_hint` covers the active
scene path and the material asset path.

Read the current skybox with `unity_open_mcp_skybox_get` (gate-free): path,
name, and shader.

## Common recipes

### Daylight directional + ambient

1. `light_add` on an empty GameObject named "Sun" with
   `light_type: "Directional"`, `intensity: 1.2`.
2. `light_set` for `shadows: "soft"` on the sun.
3. Ambient: `settings_set_lighting` (`build-settings` group) with
   `ambientMode`, `ambientIntensity`, `ambientColor`.

### Point lights with range

1. `light_add` on each lamp GameObject with `light_type: "Point"`,
   `intensity: 2.0`, `range: 8.0`, `color: "1,0.85,0.6,1"` (warm).
2. `light_set` to toggle `shadows: "soft"` per lamp.

### Reflection probe for a glossy surface

1. Add a `ReflectionProbe` with `unity_open_mcp_component_add` on a GameObject
   positioned at the surface.
2. `reflection_probe_get` to confirm the settings (`resolution`, `hdr`).
3. `reflection_probe_bake` with `bake_mode: "custom"`,
   `target_path: "Assets/Lighting/SurfaceProbe.cubemap"`. Wait for the settle
   window — the tool returns `baked: true` + `cubemapPath` when the bake
   finishes.

### Swap the skybox

1. `skybox_set` with `material_path: "Assets/Materials/NightSky.mat"`. The
   active scene is dirtied and the ambient environment refreshed.
2. `scene_save` to commit the scene-level change.

## Reflective mutation (niche Light fields)

When `light_set` does not cover a field, use `light_modify`:

```json
{
  "fields_json": "[{\"field\":\"bounceIntensity\",\"value\":1.5,\"type\":\"float\"},{\"field\":\"shadows\",\"value\":\"Soft\",\"type\":\"string\"}]",
  "paths_hint": ["Assets/Scenes/Game.unity"]
}
```

Each entry is `{ field, value, type? }` where `type` is
`int | float | bool | string | vector | color` (default inferred from the
field's current type). Enum fields (`LightType` / `LightShadows` / `RenderMode`)
accept a name or an int index. Per-field errors are accumulated — a single bad
entry does not abort the batch. Prefer `light_set` for the common cases.

## Agent-sense pairing

- `unity_senses_screenshot` (view: `"game"`) visually confirms the lighting
  after a set / bake.
- `unity_open_mcp_settings_get_lighting` reads the ambient/fog layer
  (complementary to this group's per-object lighting).

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `light_add` | yes | editor_settle | Idempotent — re-using reports `added:false`. |
| `light_set` | yes | editor_settle | Typed fields; each optional. |
| `light_modify` | yes | editor_settle | Reflective field patch for niche fields. |
| `reflection_probe_bake` | yes | editor_settle | Long mutation — realtime / baked / custom. |
| `reflection_probe_get` | no | none | Probe settings (read-only). |
| `skybox_set` | yes | editor_settle | Assign/clear `RenderSettings.skybox`; dirties active scene. |
| `skybox_get` | no | none | Current skybox path (read-only). |

Address every target by `instance_id` > `path` > `name` (same model as
`gameobject_*` / `component_*`). Every mutating tool requires a non-empty
`paths_hint` scoped to the host's scene path (and, for `skybox_set`, the
material asset path; for `reflection_probe_bake` custom mode, the cubemap path)
— the gate has no whole-project fallback.
