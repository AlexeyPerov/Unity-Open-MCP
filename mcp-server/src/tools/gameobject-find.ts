import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 2 — read-only GameObject find. Gate-free, token-bounded.
// Two modes: (a) targeted lookup by instance_id/path/name → single-object
// result; (b) list mode (no target) → enumerate with optional filters.
export const gameobjectFind = makeTool(
  "unity_open_mcp_gameobject_find",
  "Find GameObjects in loaded scenes. Read-only (gate-free). Two modes: (a) targeted lookup by " +
    "instance_id / path / name — returns a single-object result (empty list when not found, with " +
    "notFound=true); (b) list mode (omit all three) — enumerates loaded-scene GameObjects with " +
    "optional filters (name_contains, tag, component, root_only), bounded by max_results. Each " +
    "result includes instanceId, name, path, active, tag, layer, scene, transform, and component " +
    "list so the agent can chain add/modify/set_parent without an extra call. Prefer this over " +
    "raw invoke_method GameObject.Find* — structured output + addressing parity with spatial_query.",
  {
    properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description:
              "Targeted mode: GameObject instance ID. When set (or path/name set), returns a single " +
              "object. Highest priority resolver.",
          },
          path: {
            type: "string",
            description: "Targeted mode: hierarchy path \"Root/Child\".",
          },
          name: {
            type: "string",
            description: "Targeted mode: GameObject name (first match).",
          },
          name_contains: {
            type: "string",
            description: "List mode: substring filter on GameObject name.",
          },
          tag: {
            type: "string",
            description: "List mode: filter by tag (must be a defined tag).",
          },
          component: {
            type: "string",
            description:
              "List mode: filter by component type name (class or full name). The GameObject must " +
              "have a component whose type name matches.",
          },
          root_only: {
            type: "boolean",
            default: false,
            description:
              "List mode: only return root-level GameObjects (skip children). Default false = full " +
              "hierarchy walk.",
          },
          max_results: {
            type: "integer",
            default: 50,
            minimum: 1,
            description: "Max objects returned; remaining count is reported in 'truncated'.",
          },
        },
  },
);
