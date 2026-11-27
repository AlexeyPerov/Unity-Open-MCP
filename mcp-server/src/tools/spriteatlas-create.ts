import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 9 / T20.9.1 — SpriteAtlas domain tool. Built-in 2D module (no
// extra UPM); the `2d` tool group is hidden until manage_tools activates it.
// Mutating: runs the full gate path (EditorSettle — the atlas asset is written
// + imported); paths_hint is the new asset path. The extension picks the
// format: .spriteatlasv2 (Sprite Atlas V2) or .spriteatlas (V1).
export const spriteatlasCreate = makeTool(
  "unity_open_mcp_spriteatlas_create",
  "Create a new SpriteAtlas asset at an Assets/-rooted path. The extension " +
    "picks the format: .spriteatlasv2 (Sprite Atlas V2, imported by " +
    "SpriteAtlasImporter) or .spriteatlas (Sprite Atlas V1). Use the one your " +
    "Sprite Packer Mode packs (Sprite Atlas V2 modes: .spriteatlasv2); the answer " +
    "reports format, packerMode and a warning on a mismatch. Intermediate folders " +
    "are created if missing; an existing asset is refused (asset_exists). " +
    "include_in_build defaults to true (the atlas ships with the player build). " +
    "Mutating: runs the full gate path (editor_settle lifecycle — only the new " +
    "file is imported); paths_hint is the new asset path. Built-in 2D module (no " +
    "package dependency); the sprite2d group is hidden until manage_tools activates it.",
  {
    required: ["asset_path", "paths_hint"],
        properties: {
          asset_path: {
            type: "string",
            description:
              "Destination asset path. Must start with 'Assets/' and end with " +
              "'.spriteatlasv2' (Sprite Atlas V2) or '.spriteatlas' (V1). " +
              "Intermediate folders are created if missing.",
          },
          include_in_build: {
            type: "boolean",
            default: true,
            description:
              "Whether the atlas ships with the player build (SpriteAtlas " +
              "includeInBuild). Defaults to true.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the new atlas asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
