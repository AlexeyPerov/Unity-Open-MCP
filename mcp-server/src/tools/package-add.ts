import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 4 — typed UPM package add. Mutating: writes Packages/manifest.json
// and triggers package resolution (may domain-reload). Runs the full gate
// path; paths_hint must be scoped to "Packages/manifest.json" (the lock file
// packages-lock.json is touched implicitly — no need to list it separately).
export const packageAdd: Tool = {
  name: "unity_open_mcp_package_add",
  description:
    "Install a Unity package from the registry, a Git URL, or a local path. Mutating: rewrites " +
    "Packages/manifest.json and triggers package resolution (may install assemblies and force a " +
    "domain reload; the bridge blocks on the post-add compile via its restart_then_settle " +
    "lifecycle so the response reflects post-reload state). Runs the full gate path; " +
    "`paths_hint` must be `[\"Packages/manifest.json\"]` (packages-lock.json is touched implicitly). " +
    "Returns the resolved package (name/version/source/dependencies/etc.). Use " +
    "unity_open_mcp_package_search first to discover the right id.",
  inputSchema: {
    type: "object",
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
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — pass [\"Packages/manifest.json\"]. The gate validates against this " +
          "scope; packages-lock.json is touched implicitly by UPM resolution and does not need " +
          "to be listed. There is no whole-project fallback.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description: "Gate mode. Default 'enforce' — fails the call if the add surfaces new errors.",
      },
    },
    additionalProperties: false,
  },
};
