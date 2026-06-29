import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 7.5 / T20.7.5.3 — forward + reverse dependency edges for a single
// asset, exposed as a typed tool. Complements `unity_open_mcp_find_references`
// (reverse-only) by returning BOTH edge directions in one call, plus the
// broken_forward_guids / cycles sets the `dependencies` verify rule computes.
// No second dependency graph is built on the bridge side — the handler reuses
// the same Dependencies.Scanner (forward) + ReferenceGraph (reverse) the rule
// and find_references already run against. Live-bridge-only (the underlying
// scanners call AssetDatabase APIs); no offline form.
export const dependencies: Tool = {
  name: "unity_open_mcp_dependencies",
  description:
    "Forward AND reverse dependency edges for an asset in one typed call. " +
    "Returns the assets this asset depends on (forward), the assets that " +
    "depend on this asset (reverse), plus broken forward-edge GUIDs and " +
    "dependency cycles. Reuses the same scanners as the `dependencies` " +
    "verify rule (forward) and `unity_open_mcp_find_references` (reverse) " +
    "— no second dependency graph. Use find_references for reverse-only " +
    "lookups (it also works offline); use this tool when you need both " +
    "directions or the broken-edge / cycle view. Live bridge only.",
  inputSchema: {
    type: "object",
    properties: {
      asset_path: {
        type: "string",
        description: "Asset path (e.g. Assets/Prefabs/Player.prefab). Either asset_path or guid is required.",
      },
      guid: {
        type: "string",
        pattern: "^[0-9a-fA-F]{32}$",
        description: "Asset GUID (32 hex chars). Either asset_path or guid is required.",
      },
      detail: {
        enum: ["summary", "normal"],
        default: "normal",
        description:
          "summary: counts only (forwardCount / reverseCount), edge rosters omitted. " +
          "normal (default): full forward + reverse edge rosters (capped by max_results on the reverse side).",
      },
      max_results: {
        type: "integer",
        default: 100,
        description: "Maximum number of reverse-dependency entries to return. Forward edges are not capped (a single asset's forward set is bounded).",
      },
    },
    oneOf: [
      { required: ["asset_path"] },
      { required: ["guid"] },
    ],
  },
};
