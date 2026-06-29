# Tilemap — embedded domain tools

Tilemap typed tools (`unity_open_mcp_tilemap_*`), embedded inside the bridge.
Five tools for in-editor 2D level design: create grid + tilemap, set tile,
box-fill, create tile asset, create rule tile.

## Compile gate (two layers + an inner guard)

Three layers (see `docs/extensions.md` §Embedded domain model):

1. The bridge root asmdef
   (`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
   sets **two** `versionDefines` entries:
   - `UNITY_OPEN_MCP_EXT_TILEMAP` when `com.unity.2d.tilemap` resolves.
   - `UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS` when `com.unity.2d.tilemap.extras`
     resolves.
2. This folder's sub-asmdef carries
   `defineConstraints: ["UNITY_OPEN_MCP_EXT_TILEMAP"]` and references
   `UnityEngine.TileMapModule`. Unity only compiles it when the core tilemap
   package is present.
3. Each source file wraps its body in `#if UNITY_OPEN_MCP_EXT_TILEMAP`, and the
   `create_rule_tile` tool adds an **inner**
   `#if UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS` guard around its RuleTile body. When
   extras is absent the tool compiles in (the outer gate passes) but returns a
   clean `tilemap_extras_required` install error — two defines, two guards, no
   broken reference.

Tilemap has a single stable public API (UnityEngine.Tilemaps namespace);
RuleTile lives in com.unity.2d.tilemap.extras. When the core package is absent
the tools are not compiled in and the capability surface reports the domain as
`available: false (dependency missing: com.unity.2d.tilemap)`.

## Tool group

All five tools belong to the `tilemap` group (M18 Plan 2). Hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
