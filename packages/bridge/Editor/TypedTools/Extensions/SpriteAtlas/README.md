# SpriteAtlas — embedded domain tools

SpriteAtlas typed tools (`unity_open_mcp_spriteatlas_*`), embedded inside the
bridge. Seven tools cover the SpriteAtlas authoring surface — the 2D art
pipeline's atlas-packing layer:

- `spriteatlas_create` — create a new SpriteAtlas asset (`Assets/.../*.spriteatlas`).
- `spriteatlas_get` — read packables + packing/texture/platform settings (read-only).
- `spriteatlas_add_packable` — add sprites/textures/folders by asset path.
- `spriteatlas_remove_packable` — remove a packable by asset path.
- `spriteatlas_modify` — patch `include_in_build` + packing + texture settings.
- `spriteatlas_delete` — delete the `.spriteatlas` asset.
- `spriteatlas_list` — list SpriteAtlas assets under a folder (read-only).

Added in M20 Plan 9 / T20.9.1 to cover the 2D art-pipeline parity gap. The
gate + `paths_hint` contract on every mutating member (and the
`EditorSettle` lifecycle on the asset-write/pack paths) is the documented
advantage — an ungated atlas tool touches the asset and returns before the
reimport settles; ours routes every mutation through the gate and waits for
the settle window.

## Compile gate

**None.** The `SpriteAtlas` / `SpriteAtlasAsset` /
`SpriteAtlasPackingSettings` / `SpriteAtlasTextureSettings` types live in the
built-in 2D module (`UnityEngine.U2D` / `UnityEditor.U2D` in `CoreModule`)
and are present in every Unity install, so this domain ships ungated — no
`UNITY_OPEN_MCP_EXT_2D` define and no sub-asmdef `defineConstraints`. The
owning sub-asmdef only references the bridge Editor asmdef.

## Authoring model

A `.spriteatlas` file stores a `SpriteAtlasAsset` (the authoring object); the
runtime `SpriteAtlas` is the packed artifact produced from it. These tools
author via `SpriteAtlasAsset.Load` / `Save` and mutate packables via
`Add` / `Remove`. Packables are `Object` references (Sprite / Texture /
`DefaultAsset` folder); they are resolved by `Assets/`-rooted path and
reported back by path + type so agents can round-trip without instance ids.

## Tool group

All seven tools belong to the `sprite2d` group (shared with the Texture import
family — the two halves of the 2D art pipeline activate together). Hidden
from `ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
