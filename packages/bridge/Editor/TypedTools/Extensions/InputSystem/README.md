# InputSystem — embedded domain tools (scaffold)

M18 Plan 1 created this folder as the future home for the Input System domain
tools. The folder exists so the compile-gated layout is in place; the actual
tool code migrates here in **M18 Plan 3** (migrate existing extensions),
following the Navigation reference template in `../Navigation/`.

## Compile gate

The Input System tools will be gated by the `UNITY_OPEN_MCP_EXT_INPUTSYSTEM`
define, which the bridge root asmdef
(`packages/bridge/Editor/com.com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
sets via `versionDefines` when `com.unity.inputsystem` is present.

See `docs/extensions.md` §Embedded domain model for the gating policy.
