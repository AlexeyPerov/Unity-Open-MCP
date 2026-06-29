# VFX Graph — embedded domain tools

VFX Graph typed tools (`unity_open_mcp_vfx_*`), embedded inside the bridge. Three
tools for in-editor VFX Graph authoring: list VisualEffectGraph assets, open a
`.vfx` in the VFX Graph editor (with a structured context/block/property
summary), and patch a single property on a block.

## Compile gate

Two-layer gate (see `docs/extensions.md` §Embedded domain model):

1. The bridge root asmdef
   (`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
   sets `UNITY_OPEN_MCP_EXT_VFX` via `versionDefines` when
   `com.unity.visualeffectgraph` resolves.
2. This folder's sub-asmdef carries
   `defineConstraints: ["UNITY_OPEN_MCP_EXT_VFX"]` and references
   `Unity.VisualEffectGraph.Editor`. Unity only compiles it when the define is
   set, so the optional package reference never breaks a project that lacks it.

Each source file additionally wraps its body in `#if UNITY_OPEN_MCP_EXT_VFX` as a
belt-and-suspenders guard.

## Auto-activation (M20 Plan 7 / T20.7.0)

This domain ships with **auto-activation**: the `vfx` group activates
automatically for the session when `com.unity.visualeffectgraph` is installed —
no manual `manage_tools` call required. Auto-activation is ephemeral (per
session, resets on server restart) and is additive to the manual-activation
model. Deactivate via `unity_open_mcp_manage_tools(action="deactivate", group="vfx")`
to hide the tools.

## Reflection over the editing API

VFX Graph's editing API (`UnityEditor.VFX`: `VFXGraph`, `VFXContext`, `VFXBlock`,
`VFXSlot`) is **more internal/unstable** than Shader Graph's — even the
competitor ships only list/open for the same reason (no public headless edit
entry point). The read paths (`list`, `open`) work over the public runtime
`UnityEngine.VFX.VisualEffectAsset` type and the stable serialized file format,
so they are version-stable without editor-model reflection. The mutating path
(`block_edit`) reflects over the editor graph model and requires the VFX Graph
window to be open (there is no stable public headless entry point to load + edit
+ save a graph); when the model cannot be reached the tool returns a structured
`vfx_block_edit_requires_editor_window` error and the agent falls back to manual
editing. This matches the execution plan §T20.7.2 fallback note.

## Tool group

All three tools belong to the `vfx` group (M20 Plan 7). Auto-activated when
`com.unity.visualeffectgraph` is present; otherwise hidden from `ListTools` until
the session activates the group via `unity_open_mcp_manage_tools`. `list` and
`open` are read-only (`Gate = Off`); `block_edit` is mutating and runs the full
gate path with `paths_hint` scoped to the `.vfx` asset path (`EditorSettle`
lifecycle).
