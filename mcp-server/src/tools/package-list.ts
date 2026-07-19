import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 4 — typed UPM package list. Read-only (gate-free). Token-bounded
// by max_results. Wraps UnityEditor.PackageManager.Client.List and supports
// offline mode (cached resolution only), source filtering, name-substring
// filtering, and a direct-dependencies-only toggle (manifest.json entries).
export const packageList = makeTool(
  "unity_open_mcp_package_list",
  "List Unity Package Manager (UPM) packages installed in the project — name, displayName, " +
    "version, packageId, source, resolvedPath, description, category, versions (recommended + " +
    "latestCompatible), registry, and per-package dependencies. Optionally filter by `source` " +
    "(registry / embedded / local / git / builtin / localtarball), by `name_filter` substring " +
    "(name/displayName/description with exact-match priority), and direct-dependencies-only " +
    "(entries in Packages/manifest.json). Read-only (gate-free). Token-bounded by `max_results` " +
    "(remaining count is reported in `truncated`). Defaults to `offline: true` (cached resolution); " +
    "pass `offline: false` to refresh from the registry first.",
  {
    properties: {
          offline: {
            type: "boolean",
            default: true,
            description:
              "When true (default), use the cached resolution (no registry round-trip). Set false to " +
              "force a fresh resolution. Offline mode is faster and works without network.",
          },
          include_indirect: {
            type: "boolean",
            default: false,
            description:
              "When true, include transitive (indirect) dependencies. Default false returns only the " +
              "packages listed in Packages/manifest.json.",
          },
          source: {
            type: "string",
            enum: ["registry", "embedded", "local", "git", "builtin", "localtarball"],
            description: "Restrict the result to packages from a single PackageSource.",
          },
          name_filter: {
            type: "string",
            description:
              "Case-insensitive substring filter over name/displayName/description. Results are " +
              "prioritized: exact name → exact displayName → name substring → displayName substring " +
              "→ description substring.",
          },
          direct_dependencies_only: {
            type: "boolean",
            default: false,
            description:
              "When true, only return packages whose name appears in Packages/manifest.json (direct " +
              "dependencies). Implies a manifest read to compute the directDependency flag.",
          },
          max_results: {
            type: "integer",
            minimum: 1,
            default: 200,
            description: "Max packages returned; remaining count is reported in 'truncated'.",
          },
        },
  },
);
