import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 4 — typed UPM package add. Mutating: writes Packages/manifest.json
// and triggers package resolution (may domain-reload). Runs the full gate
// path; paths_hint must be scoped to "Packages/manifest.json" (the lock file
// packages-lock.json is touched implicitly — no need to list it separately).
export const packageAdd = makeTool(
  "unity_open_mcp_package_add",
  "Install a Unity package from the registry, a Git URL, or a local path. Mutating: rewrites " +
    "Packages/manifest.json and triggers package resolution (may install assemblies and force a " +
    "domain reload; the bridge blocks on the post-add compile via its restart_then_settle " +
    "lifecycle so the response reflects post-reload state). Runs the full gate path; " +
    "`paths_hint` must be `[\"Packages/manifest.json\"]` (packages-lock.json is touched implicitly). " +
    "Returns the resolved package (name/version/source/dependencies/etc.). Use " +
    "unity_open_mcp_package_search first to discover the right id.",
  {
    required: ["package_id", "paths_hint"],
        properties: {
          package_id: {
            type: "string",
            description:
              "Package specifier. Accepted formats: plain package id 'com.unity.textmeshpro' (latest " +
              "compatible version); pinned version 'com.unity.textmeshpro@3.0.6'; Git URL " +
              "'https://github.com/user/repo.git'; Git URL with branch/tag " +
              "'https://github.com/user/repo.git#v1.0.0'; local path 'file:../MyPackage'; local " +
              "tarball 'file:../MyPackage.tgz'.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — pass [\"Packages/manifest.json\"]. The gate validates against this " + "scope; packages-lock.json is touched implicitly by UPM resolution and does not need " + "to be listed. There is no whole-project fallback." },
          gate: { ...GATE_PROP, description: "Gate mode. Default 'enforce' — fails the call if the add surfaces new errors." },
        },
  },
);
