# InputSystem — embedded domain tools

Input System typed tools (`unity_open_mcp_inputsystem_*`), embedded inside the
bridge. Eight tools for authoring `.inputactions` asset graphs: asset create +
action-map/action/binding/composite/control-scheme add + get.

Ported in M18 Plan 3 from the former standalone extension pack at
`packages/extensions/inputsystem/` (now frozen). Logic, tool IDs, JSON schema,
and gate contracts are unchanged from the legacy pack — only the namespace
moved (`UnityOpenMcpExtensions.InputSystemExt` →
`UnityOpenMcpBridge.Extensions.InputSystemExt`). The `Ext` suffix avoids
colliding with the `UnityEngine.InputSystem` namespace in IDE autocomplete.

## Compile gate

Two-layer gate (see `docs/contributing/extensions.md` §Embedded domain model):

1. The bridge root asmdef
   (`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
   sets `UNITY_OPEN_MCP_EXT_INPUTSYSTEM` via `versionDefines` when
   `com.unity.inputsystem` resolves.
2. This folder's sub-asmdef carries
   `defineConstraints: ["UNITY_OPEN_MCP_EXT_INPUTSYSTEM"]` and references
   `Unity.InputSystem`. Unity only compiles it when the define is set, so the
   optional package reference never breaks a project that lacks it.

Each source file additionally wraps its body in
`#if UNITY_OPEN_MCP_EXT_INPUTSYSTEM` as a belt-and-suspenders guard.

## Tool group

All eight tools belong to the `input-system` group (M18 Plan 2). Hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
