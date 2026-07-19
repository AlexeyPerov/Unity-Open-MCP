import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — typed asset move / rename. Wraps AssetDatabase.MoveAsset.
// Mutating: runs the full gate path; `paths_hint` must enumerate BOTH the
// source (now-missing) and destination (now-present) paths so the gate can
// track references that broke as the asset moved. Per-entry errors are
// collected, not abortive.
export const assetsMove = makeTool(
  "unity_open_mcp_assets_move",
  "Move or rename asset(s) within the project. Each entry pairs an existing source " +
    "asset path with a destination path (must not already exist). Intermediate destination " +
    "folders must already exist. Per-entry errors are collected, not abortive. Mutating: " +
    "runs the full gate path (checkpoint -> move -> validate -> delta). Because a move " +
    "breaks references to the source path, `paths_hint` should include BOTH source and " +
    "destination paths so the gate can flag dangling references.",
  {
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
                  description: "Existing asset path to move from (Assets/-rooted).",
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
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — include BOTH source and destination paths so the gate can detect dangling references. No whole-project fallback." },
          gate: { ...GATE_PROP },
        },
  },
);
