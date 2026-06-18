import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 1 — typed asset copy. Wraps AssetDatabase.CopyAsset so an agent
// can duplicate assets without composing execute_csharp. Mutating: runs the
// full gate path; `paths_hint` must enumerate the destination paths (the new
// assets are what the gate validates). Each {source, destination} pair is
// processed independently; per-entry errors are collected so a single bad
// input does not abort the batch.
export const assetsCopy: Tool = {
  name: "unity_open_mcp_assets_copy",
  description:
    "Copy asset(s) within the project. Each entry pairs an existing source asset path " +
    "with a destination path (must not already exist). Intermediate destination folders " +
    "must already exist. Per-entry errors are collected, not abortive. Mutating: runs " +
    "the full gate path (checkpoint -> copy -> validate -> delta). `paths_hint` must list " +
    "each destination path so the gate scopes validation correctly.",
  inputSchema: {
    type: "object",
    required: ["entries", "paths_hint"],
    properties: {
      entries: {
        type: "array",
        items: {
          type: "object",
          required: ["source", "destination"],
          properties: {
            source: {
              type: "string",
              description: "Existing asset path to copy from (Assets/-rooted).",
            },
            destination: {
              type: "string",
              description: "Destination asset path (Assets/-rooted, must not already exist).",
            },
          },
          additionalProperties: false,
        },
        minItems: 1,
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Destination asset paths (the gate's validation scope). No whole-project fallback.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
