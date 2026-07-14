# Unity Open MCP — Texture Extension

Skill for AI agents driving Unity texture import settings in a project through
the `unity-open-mcp` MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tools use the
> built-in `TextureImporter` (`UnityEditor.CoreModule`) — no Unity package
> install is required, and they compile into every bridge build. Its tool
> group is **hidden** from `ListTools` until the connected session activates
> it.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The `sprite2d` tool group is activated — call
  `unity_open_mcp_manage_tools(action="activate", group="sprite2d")` before
  invoking any Texture (or SpriteAtlas) tool.
  Fresh sessions start with five default-on groups: `core`, `gate-and-verify`,
  `asset-intelligence`, `typed-editor`, and `diagnostics`.
  Because `TextureImporter` is built-in, `capabilities`
  always reports the `sprite2d` group as `available: true` (no
  `domainDefine`).

## Tool prefix

`unity_open_mcp_texture_*` — Texture import. The `sprite2d` group is shared
with the SpriteAtlas family (`unity_open_mcp_spriteatlas_*`); see the
SpriteAtlas extension skill for the atlas-packing half of the 2D art pipeline.

Mutating tools (`texture_set_import`, `texture_reimport`) accept the standard
`paths_hint` and run the full gate path (`editor_settle` lifecycle — a
reimport can take seconds and may trigger a platform-switch domain reload);
read-only tools (`texture_get_importer`, `texture_get`) are gate-free.

## Two read paths

- `texture_get_importer` — the **import-pipeline config** (`TextureImporter`
  settings: `textureType`, `npotScale`, `maxTextureSize`, `textureCompression`,
  `spriteImportMode`, `filterMode`, …). What the asset *will be* when imported.
- `texture_get` — the **runtime Texture info** (`width`, `height`, `format`,
  `mipmapCount`, `filterMode`). What the asset *currently is* in memory.

Use `get_importer` to read/edit import settings; use `get` to confirm the
imported result. They are complementary.

## Canonical workflow: configure a texture

1. **Read the current config** — `unity_open_mcp_texture_get_importer` with
   `asset_path: "Assets/Textures/Icon.png"` (gate-free).
2. **Patch + reimport** — `unity_open_mcp_texture_set_import` with a
   `settings_json` patch (see below). The tool writes the `.meta` and
   triggers the import pipeline; the `editor_settle` lifecycle makes the
   dispatcher wait for the import to settle before returning.
3. **Verify** — `texture_get_importer` to confirm the new settings, and
   `texture_get` to confirm the imported result (e.g. `format` changed).

## Settings patch (texture_set_import)

`settings_json` is a JSON object string with optional snake_case keys. Each
key is optional — omit to leave unchanged. Unknown keys are reported in
`unknownFields`, not fatal.

```json
{
  "texture_type": "Sprite",
  "sprite_mode": "Single",
  "sprite_pixels_per_unit": 100,
  "max_texture_size": 1024,
  "compression": "Compressed",
  "filter_mode": "Bilinear",
  "aniso_level": 1,
  "srgb": true,
  "readable": false,
  "mipmap_enabled": false
}
```

Supported keys (each optional):

| Key | Values |
|---|---|
| `texture_type` | `Default` \| `NormalMap` \| `Sprite` \| `Cursor` \| `Cookie` \| `Lightmap` \| `SingleChannel` \| … |
| `texture_shape` | `Texture2D` \| `TextureCube` |
| `npot_scale` | `None` \| `ToNearest` \| `ToLarger` \| `ToSmaller` |
| `max_texture_size` | `32` \| `64` \| … \| `8192` |
| `compression` | `None` \| `Uncompressed` \| `Compressed` \| `CompressedHQ` \| `CompressedLQ` |
| `compression_quality` | `0`–`100` |
| `crunched` | bool |
| `srgb` | bool |
| `readable` | bool |
| `mipmap_enabled` | bool |
| `filter_mode` | `Point` \| `Bilinear` \| `Trilinear` |
| `aniso_level` | `0`–`16` |
| `wrap_mode` | `Repeat` \| `Clamp` \| `Mirror` \| `MirrorOnce` |
| `alpha_is_transparency` | bool |
| `sprite_mode` | `None` \| `Single` \| `Multiple` \| `Polygon` |
| `sprite_pixels_per_unit` | float |
| `normalmap` | bool |

## Sprite + normal-map presets

`texture_set_import` folds the sprite-preset and normal-map-preset flows into
one typed call (rather than separate `set_sprite` / `set_normalmap` tools):

- **Sprite preset** — pass `sprite_mode` (`Single` | `Multiple` | `Polygon`).
  The tool switches `textureType` to `Sprite` and sets `spriteImportMode`.
  Optionally pass `sprite_pixels_per_unit`.
- **Normal-map preset** — pass `normalmap: true`. The tool switches
  `textureType` to `NormalMap` and enables the normal-map pipeline.

## Force reimport (texture_reimport)

`unity_open_mcp_texture_reimport` forces a reimport **without changing
settings** — useful after an external build pipeline overwrites the source
file on disk. Runs through the gate with `editor_settle` so the next mutation
sees the settled reimport.

## Layered with SpriteAtlas

The `sprite2d` group covers **texture import** (this skill). It pairs with
the **SpriteAtlas** family (`unity_open_mcp_spriteatlas_*`) — mark a texture
as a Sprite here (`sprite_mode: "Single"`), then add it to an atlas there.

## Common recipes

### Compress a UI sprite

1. `texture_set_import` with
   `settings_json: '{"texture_type":"Sprite","sprite_mode":"Single","max_texture_size":1024,"compression":"Compressed","filter_mode":"Bilinear"}'`.
2. `texture_get` to confirm the new `format` (e.g. `ASTC_RGBA_6x6`).

### Convert a texture to a normal map

1. `texture_set_import` with `settings_json: '{"normalmap":true}'`.
2. `texture_get_importer` to confirm `textureType: NormalMap`.

### Reimport after an external file swap

1. (External pipeline overwrites `Assets/Textures/Icon.png`.)
2. `texture_reimport` with `asset_path: "Assets/Textures/Icon.png"`.

## Agent-sense pairing

- `unity_senses_screenshot` (view: `"game"`) visually confirms the texture
  renders correctly after a compression change.
- `unity_open_mcp_spriteatlas_add_packable` consumes a Sprite-configured
  texture as a packable.

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `texture_get_importer` | no | none | Import-pipeline config (read-only). |
| `texture_set_import` | yes | editor_settle | `settings_json` patch; sprite + normalmap presets folded in. |
| `texture_reimport` | yes | editor_settle | Force reimport without changing settings. |
| `texture_get` | no | none | Runtime Texture info (read-only). |

Every mutating tool requires a non-empty `paths_hint` scoped to the texture
asset path — the gate has no whole-project fallback.
