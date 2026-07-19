import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 4 — typed UPM package get_dependencies. Read-only (gate-free).
// Reads Packages/manifest.json directly (no UPM request) and returns the
// top-level dependency entries as { name, reference } pairs.
export const packageGetDependencies = makeTool(
  "unity_open_mcp_package_get_dependencies",
  "Read the top-level dependency list from Packages/manifest.json. Read-only (gate-free). " +
    "Does NOT resolve transitive dependencies or spin up a UPM request — it parses the manifest " +
    "file directly and returns each entry as { name, reference } where `reference` is the version " +
    "pin, Git URL, file path, or embedded marker exactly as written. Use this for a fast manifest " +
    "snapshot; use unity_open_mcp_package_list with include_indirect: true for the resolved graph.",
  {
    properties: {},
  },
);
