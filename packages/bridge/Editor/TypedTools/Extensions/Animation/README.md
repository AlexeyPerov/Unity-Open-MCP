# Animation — embedded domain tools

AnimationClip + AnimatorController typed tools (`unity_open_mcp_animation_*`
and `unity_open_mcp_animator_*`), embedded inside the bridge. Six tools for
authoring `.anim` and `.controller` assets: create, get_data, modify (per
asset kind).

Ported in M18 Plan 3 from the former standalone extension pack at
`packages/extensions/animation/` (now frozen). Logic, tool IDs, JSON schema,
and gate contracts are unchanged from the legacy pack — only the namespace
moved (`UnityOpenMcpExtensions.Animation` →
`UnityOpenMcpBridge.Extensions.Animation`).

## Compile gate

Two-layer gate (see `docs/extensions.md` §Embedded domain model):

1. The bridge root asmdef
   (`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
   sets `UNITY_OPEN_MCP_EXT_ANIMATION` via `versionDefines` when the built-in
   `com.unity.modules.animation` module is present.
2. This folder's sub-asmdef carries
   `defineConstraints: ["UNITY_OPEN_MCP_EXT_ANIMATION"]`. The animation API is
   a built-in engine module, so no separate package reference is needed.

Each source file additionally wraps its body in
`#if UNITY_OPEN_MCP_EXT_ANIMATION` as a belt-and-suspenders guard.

## Tool group

All six tools belong to the `animation` group (M18 Plan 2). Hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
