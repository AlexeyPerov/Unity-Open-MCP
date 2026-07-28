import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const checkpointCreate = makeTool(
  "unity_open_mcp_checkpoint_create",
  "Create a manual checkpoint for later delta comparison.",
  {
    properties: {
          paths: {
            type: "array",
            items: { type: "string" },
            description:
              "Scope; empty = whole-project summary restricted to the cheap " +
              "project_health rule (orphan .meta, duplicate_guid, invalid_layer). " +
              "Pass explicit folders/files to fingerprint the per-asset rules " +
              "(missing_references, scene_prefab_health, …); a literal empty " +
              "scope does not load or open every asset.",
          },
          label: { type: "string" },
        },
  },
);
