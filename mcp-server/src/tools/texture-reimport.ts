import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.1 — Texture domain tool. Mutating: force reimport of a
// texture without changing settings (useful after external file replacement).
// Runs the full gate path (EditorSettle); paths_hint is the texture asset path.
export const textureReimport: Tool = {
  name: "unity_open_mcp_texture_reimport",
  description:
    "Mutating: force a reimport of a texture asset without changing its import " +
    "settings. Useful after an external build pipeline overwrites the source " +
    "file. Runs through the gate (editor_settle — the reimport can take seconds " +
    "and may trigger a platform-switch domain reload). Mutating: paths_hint is " +
    "the asset path. Built-in 2D module; the 2d group is hidden until " +
    "manage_tools activates it.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "Assets/-rooted texture asset path.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the texture asset path.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
