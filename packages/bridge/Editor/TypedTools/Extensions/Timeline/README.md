# Timeline — embedded domain tools

Timeline typed tools (`unity_open_mcp_timeline_*`), embedded inside the bridge.
Five tools for in-editor cutscene / sequence authoring: create asset, add
track, add clip, bind director, modify fields.

## Compile gate

Two-layer gate (see `docs/contributing/extensions.md` §Embedded domain model):

1. The bridge root asmdef
   (`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
   sets `UNITY_OPEN_MCP_EXT_TIMELINE` via `versionDefines` when
   `com.unity.timeline` resolves.
2. This folder's sub-asmdef carries
   `defineConstraints: ["UNITY_OPEN_MCP_EXT_TIMELINE"]` and references
   `Unity.Timeline` (and `UnityEngine.PlayablesModule` for PlayableDirector).
   Unity only compiles it when the define is set, so the optional package
   reference never breaks a project that lacks it.

Each source file additionally wraps its body in
`#if UNITY_OPEN_MCP_EXT_TIMELINE` as a belt-and-suspenders guard.

Timeline has a single stable public API across 1.x — compile-gate-only, no
reflection / version-detection layer (Cinemachine is the only reflection-gated
pack).

## Tool group

All five tools belong to the `timeline` group (M18 Plan 2). Hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
