# Texture — embedded domain tools

Texture typed tools (`unity_open_mcp_texture_*`), embedded inside the bridge.
Four tools cover the TextureImporter surface — the 2D art pipeline's
texture-import layer:

- `texture_get_importer` — read the `TextureImporter` settings (the import-
  pipeline config) for a texture asset (read-only).
- `texture_set_import` — mutate the `TextureImporter` and reimport via a
  structured `settings_json` patch (compression / max size / filter mode /
  sprite mode / normal-map preset / …). Folds sprite + normal-map presets into
  one typed call instead of separate tools.
- `texture_reimport` — force a reimport of a texture without changing settings
  (useful after external file replacement).
- `texture_get` — read the runtime `Texture` info (width / height / format /
  filterMode) for a loaded asset (complementary to `get_importer`).

Added in M20 Plan 9 / T20.9.1 to cover the 2D art-pipeline parity gap. The
reimport runs through the gate with the `EditorSettle` lifecycle (a reimport
can take seconds and may trigger a platform-switch domain reload) — the
documented advantage over an ungated import mutator.

## Compile gate

**None.** `TextureImporter` is built-in (`UnityEditor.CoreModule`) and present
in every Unity install, so this domain ships ungated — no
`UNITY_OPEN_MCP_EXT_2D` define and no sub-asmdef `defineConstraints`. The
owning sub-asmdef only references the bridge Editor asmdef.

## Tool group

All four tools belong to the `sprite2d` group (shared with the SpriteAtlas family —
the two halves of the 2D art pipeline activate together). Hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.

## Sprite + normal-map presets

`texture_set_import` folds the sprite-preset and normal-map-preset flows into
the one typed call (rather than shipping separate `set_sprite` /
`set_normalmap` tools):

- **Sprite preset** — pass `sprite_mode` (`Single` | `Multiple` | `Polygon`)
  in `settings_json`; the tool switches `textureType` to `Sprite` and sets
  `spriteImportMode`. Optionally pass `sprite_pixels_per_unit`.
- **Normal-map preset** — pass `normalmap: true` in `settings_json`; the tool
  switches `textureType` to `NormalMap` and enables the normal-map pipeline.

Unknown keys are reported in `unknownFields` and do not abort the patch.
