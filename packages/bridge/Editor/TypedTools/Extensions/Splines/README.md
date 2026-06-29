# Splines — embedded domain tools

Splines typed tools (`unity_open_mcp_splines_*`), embedded inside the bridge.
Seven tools for in-editor spline authoring: create container, add/set knots,
set tangent mode, evaluate, get knots, and a reflective modify escape hatch.

First backlog domain shipped under M18 Plan 7 — proof that the embedded +
grouped model extends to compile-gated domain packs. Cinemachine (the
recommended first domain) was swapped for Splines per the plan's fallback
path: Splines is compile-gate-only with a single stable API across
`com.unity.splines` 1.x/2.x, so it avoids the Cinemachine 2.x/3.x reflection
layer. See the M18 changelog for the swap record.

## Compile gate

Two-layer gate (see `docs/extensions.md` §Embedded domain model):

1. The bridge root asmdef
   (`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
   sets `UNITY_OPEN_MCP_EXT_SPLINES` via `versionDefines` when
   `com.unity.splines` resolves.
2. This folder's sub-asmdef carries
   `defineConstraints: ["UNITY_OPEN_MCP_EXT_SPLINES"]` and references
   `Unity.Splines` (and `Unity.Mathematics`, a Splines dependency). Unity only
   compiles it when the define is set, so the optional package reference never
   breaks a project that lacks it.

Each source file additionally wraps its body in
`#if UNITY_OPEN_MCP_EXT_SPLINES` as a belt-and-suspenders guard.

## Tool group

All seven tools belong to the `splines` group (M18 Plan 2). Hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
