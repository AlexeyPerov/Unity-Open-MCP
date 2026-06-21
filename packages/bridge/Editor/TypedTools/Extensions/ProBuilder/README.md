# ProBuilder — embedded domain tools

ProBuilder typed tools (`unity_open_mcp_probuilder_*`), embedded inside the
bridge. Five tools for in-editor mesh editing: create shape, get mesh info,
extrude faces, delete faces, set face material.

Ported in M18 Plan 3 from the former standalone extension pack at
`packages/extensions/probuilder/` (now frozen). Logic, tool IDs, JSON schema,
and gate contracts are unchanged from the legacy pack — only the namespace
moved (`UnityOpenMcpExtensions.ProBuilder` →
`UnityOpenMcpBridge.Extensions.ProBuilder`).

## Compile gate

Two-layer gate (see `docs/extensions.md` §Embedded domain model):

1. The bridge root asmdef
   (`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
   sets `UNITY_OPEN_MCP_EXT_PROBUILDER` via `versionDefines` when
   `com.unity.probuilder` resolves.
2. This folder's sub-asmdef carries
   `defineConstraints: ["UNITY_OPEN_MCP_EXT_PROBUILDER"]` and references
   `Unity.ProBuilder`. Unity only compiles it when the define is set, so the
   optional package reference never breaks a project that lacks it.

Each source file additionally wraps its body in
`#if UNITY_OPEN_MCP_EXT_PROBUILDER` as a belt-and-suspenders guard.

## Tool group

All five tools belong to the `probuilder` group (M18 Plan 2). Hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
