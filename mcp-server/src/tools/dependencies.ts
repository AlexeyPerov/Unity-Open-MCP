import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 7.5 / T20.7.5.3 — forward + reverse dependency edges for a single
// asset, exposed as a typed tool. Complements `unity_open_mcp_find_references`
// (reverse-only) by returning BOTH edge directions in one call, plus the
// broken_forward_guids / cycles sets the `dependencies` verify rule computes.
// No second dependency graph is built — the live handler reuses the same
// Dependencies.Scanner (forward) + ReferenceGraph (reverse) the rule and
// find_references already run against; the offline handler reuses the same
// GUID→path index + raw-text reference scan find_references runs against.
//
// M24 Plan 2 / T24.2 — the tool is now OFFLINE-ROUTEABLE. When no bridge is
// connected, forward edges come from parsing the queried asset's YAML on disk,
// reverse edges from the offline reference scan, and broken/cycle/impact sets
// are computed offline too (impact = transitive reverse closure, an offline-
// only addition the live tool does not yet expose). The offline path covers
// text-serialized YAML assets; JSON/binary kinds surface a `forwardSkipped`
// reason and empty forward arrays (reverse edges are unaffected).
export const dependencies: Tool = {
  name: "unity_open_mcp_dependencies",
  description:
    "Forward AND reverse dependency edges for an asset in one typed call. " +
    "Returns the assets this asset depends on (forward), the assets that " +
    "depend on this asset (reverse), plus broken forward-edge GUIDs and " +
    "dependency cycles. Reuses the same scanners as the `dependencies` " +
    "verify rule (forward) and `unity_open_mcp_find_references` (reverse) " +
    "— no second dependency graph. Set include_impact=true for the transitive " +
    "reverse closure ('what breaks if I delete/move this?'). " +
    "Works offline (parsing YAML on disk) when no Editor is running, or via " +
    "the live bridge when connected. Use find_references for reverse-only " +
    "lookups.",
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
      include_impact: {
        type: "boolean",
        default: false,
        description:
          "Include the transitive reverse closure — every asset that " +
          "(transitively) depends on the queried asset, with hop depth. This " +
          "is the 'what breaks if I delete/move this?' answer. Off by default " +
          "(the multi-hop BFS is the expensive part of the call). Offline only.",
      },
      max_impact_depth: {
        type: "integer",
        default: 5,
        minimum: 1,
        maximum: 20,
        description:
          "Max hop depth for the include_impact BFS (default 5). Bounds the " +
          "walk on large graphs; the response reports `truncated` when the " +
          "closure hit the depth bound before exhausting the graph. Offline only.",
      },
    },
    oneOf: [
      { required: ["asset_path"] },
      { required: ["guid"] },
    ],
  },
};
