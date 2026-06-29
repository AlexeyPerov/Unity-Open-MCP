# Lighting — embedded domain tools

Lighting typed tools (`unity_open_mcp_light_*` / `unity_open_mcp_reflection_probe_*` /
`unity_open_mcp_skybox_*`), embedded inside the bridge. Seven tools cover the
per-light / per-probe / skybox layer that sits on top of the existing
project-settings `settings_get_lighting` / `settings_set_lighting` surface:

- `light_add` — add a `Light` component.
- `light_set` — set typed Light fields (type / color / intensity / range / spot
  angle / shadows / render mode / culling mask).
- `light_modify` — reflective field patch on a `Light` (mirrors
  `component_modify` but typed to `Light`, with enum-name support).
- `reflection_probe_bake` — bake a `ReflectionProbe` (realtime / baked /
  custom). Long mutation routed through the gate (`EditorSettle` lifecycle).
- `reflection_probe_get` — read probe settings (read-only).
- `skybox_set` — assign `RenderSettings.skybox` from a material asset path.
- `skybox_get` — read the current skybox material path (read-only).

Added in M20 Plan 2 to cover the per-light / per-probe / skybox layer. The
bake trigger is the documented advantage — a reflection-probe tool can set
`mode: Baked` without triggering a gate; ours routes the bake through the gate
(`EditorSettle` lifecycle, `Gate = Enforce`) so agents wait for the bake to
complete before the next mutation.

## Compile gate

**None.** The `Light`, `ReflectionProbe`, `RenderSettings`, and
`Lightmapping` types live in the built-in engine modules and are present in
every Unity install, so this domain ships ungated — no
`UNITY_OPEN_MCP_EXT_LIGHTING` define and no sub-asmdef `defineConstraints`.
The owning sub-asmdef only references the bridge Editor asmdef. (See the M20
Plan 2 execution plan §Unity dependency for the rationale: built-in module
gating is optional here precisely because lighting types are always present.)

## Tool group

All seven tools belong to the `lighting` group (M18 Plan 2). Hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
