# Animation — embedded domain tools (scaffold)

M18 Plan 1 created this folder as the future home for the Animation domain
tools. The folder exists so the compile-gated layout is in place; the actual
tool code migrates here in **M18 Plan 3** (migrate existing extensions),
following the Navigation reference template in `../Navigation/`.

## Compile gate

The Animation tools will be gated by the `UNITY_OPEN_MCP_EXT_ANIMATION`
define, which the bridge root asmdef
(`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
sets via `versionDefines` when the built-in `com.unity.modules.animation`
module is present.

See `docs/extensions.md` §Embedded domain model for the gating policy.
