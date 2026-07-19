import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const listRules = makeTool(
  "unity_open_mcp_list_rules",
  "List every verify rule (implemented + planned) with applicable asset kinds, default severity, available fixIds, " +
    "and issue codes. Use this before scan_paths to discover which rules apply to a given asset type — no trial-and-error. " +
    "Routes locally from the versioned rule catalog; never hits the live bridge. Filter by asset_kind / extension / " +
    "implemented_only. Planned rules surface with implemented=false and actionable guidance.",
  {
    properties: {
          asset_kind: {
            type: "string",
            description:
              "Filter to rules that declare this asset kind (e.g. \"prefab\", \"scene\", \"material\", \"animation\").",
          },
          extension: {
            type: "string",
            description:
              "Filter to rules that declare this file extension (e.g. \".prefab\" or \"prefab\"). Case-insensitive.",
          },
          implemented_only: {
            type: "boolean",
            default: false,
            description: "Omit planned/unimplemented rules.",
          },
        },
  },
);
