import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 6 / T20.6.3 — Tilemap create_rule_tile. Compile-gated in the bridge
// by UNITY_OPEN_MCP_EXT_TILEMAP, with an inner UNITY_OPEN_MCP_EXT_TILEMAP_EXTRAS
// guard around the RuleTile body. When extras is absent, the tool compiles in
// (the outer gate passes) but returns a clear `tilemap_extras_required` install
// error — two defines, two guards.
export const tilemapCreateRuleTile: Tool = {
  name: "unity_open_mcp_tilemap_create_rule_tile",
  description:
    "Create a RuleTile asset at the given asset_path. RuleTile ships in " +
    "com.unity.2d.tilemap.extras — when extras is not installed, this tool " +
    "returns a clear install error (tilemap_extras_required). Optionally seed " +
    "the default sprite via default_sprite_asset_path. Mutating: runs the full " +
    "gate path; paths_hint includes the new asset path.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "Destination asset path. Must end with '.asset'; parent folder must exist.",
      },
      rule_tile_name: {
        type: "string",
        description: "Optional RuleTile name (defaults to the asset filename).",
      },
      default_sprite_asset_path: {
        type: "string",
        description: "Optional Sprite asset path to seed the RuleTile's default sprite.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — must include the new .asset path.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
