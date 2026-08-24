import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const agentCapabilities = makeTool(
  "unity_open_mcp_capabilities",
  "Discover the full capability surface in one call: every tool with its input schema and route policy, " +
    "every verify rule with applicable asset kinds and issue severities, and every available fix. " +
    "Each capability carries an `implemented` boolean; planned-but-unbuilt items return with " +
    "`status: \"planned\"` and actionable guidance instead of failing. Call this first to learn what is " +
    "available before using execute_csharp or scan_paths blindly.\n\n" +
    "Cheap by default: `profile` defaults to `compact` (no per-tool `inputSchema`, no descriptions), " +
    "which is ~5x smaller than the unfolded catalog. Narrow with `kind` and page the `tools[]` list " +
    "with `page_size` + `cursor`, then re-call `profile: \"full\"` for the one tool whose schema you " +
    "actually need. The response echoes `profile` and a `profileHint` naming what was folded away.",
  {
    properties: {
          kind: {
            enum: ["tools", "rules", "fixes"],
            description:
              "Filter to a single surface. Omit to return tools + rules + fixes together.",
          },
          include_planned: {
            type: "boolean",
            default: true,
            description:
              "Include planned-but-unbuilt capabilities (status \"planned\"). Set false to see only implemented items.",
          },
          profile: {
            enum: ["compact", "balanced", "full"],
            default: "compact",
            description:
              "Token-budget output profile, matching the heavy read tools. 'compact' (default) = " +
              "tool identity + group + routePolicy + batchCapable + lifecycle, with per-tool " +
              "inputSchema/description and per-rule description omitted and each group's tools[] " +
              "folded to toolCount (every tool still reports its own `group`). 'balanced' = adds " +
              "descriptions back, still no inputSchema. 'full' = every field including inputSchema " +
              "(~450 KB across the whole catalog — page it).",
          },
          page_size: {
            type: "integer",
            minimum: 1,
            description:
              "Page the `tools[]` list (uniform paging). When set, the response carries a " +
              "`pagination` block with a `next_cursor` to resume. Omit to receive every tool in one " +
              "response. Recommended ~40 with profile 'compact', ~10 with 'full'.",
          },
          cursor: {
            type: "string",
            description:
              "Opaque continuation token from a previous response's `pagination.next_cursor`. " +
              "Pages the `tools[]` list.",
          },
        },
  },
);
