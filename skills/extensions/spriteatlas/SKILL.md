# Unity Open MCP — SpriteAtlas Extension

Skill for AI agents driving Unity SpriteAtlas assets in a project through the
`unity-open-mcp` MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tools use the
> built-in 2D module (`SpriteAtlas` / `SpriteAtlasAsset` /
> `SpriteAtlasPackingSettings` / `SpriteAtlasTextureSettings` in
> `UnityEngine.U2D` / `UnityEditor.U2D`) — no Unity package install is
> required, and they compile into every bridge build. Its tool group is
> **hidden** from `ListTools` until the connected session activates it.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The `sprite2d` tool group is activated — call
  `unity_open_mcp_manage_tools(action="activate", group="sprite2d")` before
  invoking any SpriteAtlas (or Texture) tool.
  Fresh sessions start with five default-on groups: `core`, `gate-and-verify`,
  `asset-intelligence`, `typed-editor`, and `diagnostics`.
  Because the 2D module is built-in, `capabilities` always
  reports the `sprite2d` group as `available: true` (no `domainDefine`).

## Tool prefix

`unity_open_mcp_spriteatlas_*` — SpriteAtlas authoring. The `sprite2d` group
is shared with the Texture import family (`unity_open_mcp_texture_*`); see the
Texture extension skill for the import half of the 2D art pipeline.

Mutating tools accept the standard `paths_hint` and run the full gate path
(`editor_settle` lifecycle — the `.spriteatlas` asset is written and
reimported); read-only tools (`spriteatlas_get`, `spriteatlas_list`) are
gate-free.

## Authoring model

A `.spriteatlas` file stores a `SpriteAtlasAsset` (the authoring object); the
runtime `SpriteAtlas` is the packed artifact produced from it. These tools
author via `SpriteAtlasAsset.Load` / `Save` and mutate packables via `Add` /
`Remove`. Packables are `Object` references (Sprite / Texture /
`DefaultAsset` folder); they are resolved by `Assets/`-rooted path and
reported back by path + type so you can round-trip without instance ids.

## Canonical workflow: build an atlas

1. **Create** — `unity_open_mcp_spriteatlas_create` with
   `asset_path: "Assets/Atlases/UI.spriteatlas"` (intermediate folders are
   created). `include_in_build` defaults to true.
2. **Add packables** — `unity_open_mcp_spriteatlas_add_packable` with
   `packable_paths: ["Assets/Sprites/Icons", "Assets/Textures/Icon.png"]`.
   Folders add every sprite inside; individual textures add themselves.
   Per-path errors are accumulated (a bad path does not abort the batch).
3. **Tune settings** — `unity_open_mcp_spriteatlas_modify` with a
   `settings_json` patch (see below).
4. **Verify** — `unity_open_mcp_spriteatlas_get` (gate-free) reads the
   packables + packing/texture/platform settings back.
5. **Iterate** — `unity_open_mcp_spriteatlas_remove_packable` to drop a
   packable; `spriteatlas_modify` to retune.

## Settings patch (spriteatlas_modify)

`settings_json` is a JSON object string with three optional sub-objects:

```json
{
  "include_in_build": false,
  "packing": {
    "padding": 4,
    "enableRotation": true,
    "enableTightPacking": false,
    "enableAlphaDilation": false,
    "blockOffset": 0
  },
  "texture": {
    "anisoLevel": 1,
    "filterMode": "Bilinear",
    "generateMipMaps": false,
    "readable": false,
    "sRGB": true
  }
}
```

Unknown fields are reported in `unknownFields` and do not abort the patch.
`texture.maxTextureSize` is not settable here (no setter on the settings
struct; controlled via platform settings).

> **Persistence note:** in this Unity version the packing and texture settings
> are applied to the in-memory `SpriteAtlasAsset` (they take effect for the
> next pack) but are **not** written to the `.spriteatlas` file's serialized
> form — Unity manages them via the internal Sprite Atlas packing pipeline,
> not the public `Save` path. `include_in_build` likewise does not round-trip
> through the serialized file. The settings are session-scoped; for
> permanent settings, use the Unity Editor's Sprite Atlas panel.

## Listing atlases

`unity_open_mcp_spriteatlas_list` (gate-free) lists every `.spriteatlas`
under a folder (omit `folder` to search the whole project). Each entry
reports `path` + `name`. Cap 200; `truncated` reports the overflow.

## Layered with Texture import

The `sprite2d` group covers **atlas packing** (this skill). It pairs with the
**Texture import** family (`unity_open_mcp_texture_*`) for the import side —
use `texture_set_import` to mark a texture as a Sprite (`sprite_mode:
"Single"`) before adding it to an atlas.

## Common recipes

### UI icon atlas

1. `texture_set_import` on each icon PNG with
   `settings_json: '{"sprite_mode":"Single","sprite_pixels_per_unit":100}'`
   (switches `textureType` to Sprite).
2. `spriteatlas_create` at `Assets/Atlases/UIIcons.spriteatlas`.
3. `spriteatlas_add_packable` with `packable_paths: ["Assets/Sprites/UI"]`
   (the folder).
4. `spriteatlas_modify` with
   `settings_json: '{"packing":{"padding":4}}'`.
5. `spriteatlas_get` to confirm.

### Swap an atlas's contents

1. `spriteatlas_get` to read the current packables.
2. `spriteatlas_remove_packable` with the old paths.
3. `spriteatlas_add_packable` with the new paths.

## Agent-sense pairing

- `unity_senses_screenshot` (view: `"game"`) visually confirms the packed
  atlas renders correctly in a UI scene.
- `unity_open_mcp_texture_get_importer` reads a packable texture's import
  config (e.g. confirm `spriteImportMode` before adding to the atlas).

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `spriteatlas_create` | yes | editor_settle | Creates intermediate folders. |
| `spriteatlas_get` | no | none | Packables + settings (read-only). |
| `spriteatlas_add_packable` | yes | editor_settle | Per-path errors accumulated. |
| `spriteatlas_remove_packable` | yes | editor_settle | Matched by asset path. |
| `spriteatlas_modify` | yes | editor_settle | `settings_json` patch; unknown fields reported. |
| `spriteatlas_delete` | no | editor_settle | Destructive — no undo across restart. |
| `spriteatlas_list` | no | none | Cap 200 (read-only). |

Every mutating tool requires a non-empty `paths_hint` scoped to the
`.spriteatlas` asset path — the gate has no whole-project fallback.
