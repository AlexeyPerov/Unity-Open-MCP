import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 4 — typed UPM package search. Read-only (gate-free). Token-bounded
// by max_results. Wraps UnityEditor.PackageManager.Client.Search /
// Client.SearchAll. Online mode fetches exact matches from the live registry
// first; cached registry data backs the substring matches in both modes.
// Each result reports install status + installed version.
export const packageSearch = makeTool(
  "unity_open_mcp_package_search",
  "Search the Unity Package Manager registry plus installed local packages (Git, local, " +
    "embedded sources) by query string. Returns name, displayName, latest version, truncated " +
    "description, source, install status, installed version (if any), and top-5 compatible " +
    "versions per result. Read-only (gate-free). Token-bounded by `max_results` (remaining count " +
    "reported in `truncated`). Results are prioritized: exact name → exact displayName → name " +
    "substring → displayName substring → description substring. Defaults to `offline: true` " +
    "(cached registry only); pass `offline: false` to hit the live registry for exact matches " +
    "before supplementing with cached substring matches.",
  {
    required: ["query"],
        properties: {
          query: {
            type: "string",
            description:
              "Package id, name, displayName, or description keyword (case-insensitive). Examples: " +
              "'com.unity.textmeshpro', 'TextMesh Pro', 'TextMesh', 'rendering'.",
          },
          offline: {
            type: "boolean",
            default: true,
            description:
              "When true (default), search cached registry data only (substring match). Set false to " +
              "first fetch exact matches from the live registry, then supplement with cached " +
              "substring matches.",
          },
          max_results: {
            type: "integer",
            minimum: 1,
            default: 20,
            description: "Max results returned; remaining count is reported in 'truncated'.",
          },
        },
  },
);
