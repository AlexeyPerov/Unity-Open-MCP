# Constraints & LOD — embedded domain tools

Constraints & LOD typed tools (`unity_open_mcp_constraint_add` /
`unity_open_mcp_lod_*`), embedded inside the bridge. Three tools cover the
animation-constraint + LODGroup layer:

- `constraint_add` — add an animation constraint component
  (`PositionConstraint` / `RotationConstraint` / `AimConstraint` /
  `ParentConstraint` / `ScaleConstraint`) to a host GameObject, with an
  optional source Transform + weight + activation.
- `lod_group_configure` — configure a `LODGroup` on a host (fade mode,
  animate cross-fading, allocate the LOD array with N levels).
- `lod_add_level` — add or replace a `LOD` entry on a `LODGroup` at an
  index, resolving the renderers from an array of GameObject paths.

Added in M20 Plan 3 to close the Constraints & LOD parity gap with the
competitor (AnkleBreaker ships a Constraints & LOD category).

## Compile gate

**None.** The `PositionConstraint` / `RotationConstraint` / `AimConstraint` /
`ParentConstraint` / `ScaleConstraint` types live in the built-in
`UnityEngine.AnimationModule` (`UnityEngine.Animations` namespace) and
`LODGroup` lives in `UnityEngine.CoreModule`. Both are present in every Unity
install, so this domain ships ungated — no `UNITY_OPEN_MCP_EXT_CONSTRAINTS`
define and no sub-asmdef `defineConstraints`. The owning sub-asmdef only
references the bridge Editor asmdef.

## Tool group

All three tools belong to the `constraints` group (M20 Plan 3 / T20.3.3). One
group covers both concerns because they are small and closely related.
Hidden from `ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
