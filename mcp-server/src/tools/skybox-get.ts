import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 2 / T20.2.3 — Lighting domain read. Built-in lighting module.
// Read-only, gate-free.
export const skyboxGet: Tool = {
  name: "unity_open_mcp_skybox_get",
  description:
    "Read the current RenderSettings.skybox material asset path (or null when " +
    "no skybox is assigned). Read-only, gate-free.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
