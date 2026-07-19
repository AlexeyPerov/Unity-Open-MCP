import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 4 — typed UPM package remove. Mutating: writes Packages/manifest.json
// and triggers resolution (may domain-reload). Runs the full gate path;
// paths_hint must be scoped to "Packages/manifest.json" (packages-lock.json
// is touched implicitly). Refuses packages that are not installed.
export const packageRemove = makeTool(
  "unity_open_mcp_package_remove",
  "Remove (uninstall) a UPM package from the project. Mutating: rewrites Packages/manifest.json " +
    "and triggers package resolution (may remove assemblies and force a domain reload; the bridge " +
    "blocks on the post-remove compile via its restart_then_settle lifecycle). Runs the full gate " +
    "path; `paths_hint` must be `[\"Packages/manifest.json\"]` (packages-lock.json is touched " +
    "implicitly). Refuses packages that are not installed (use unity_open_mcp_package_list to " +
    "enumerate first). Built-in packages and packages depended on by others cannot be removed. " +
    "A trailing '@<version>' on the input is stripped automatically.",
  {
    required: ["package_id", "paths_hint"],
        properties: {
          package_id: {
            type: "string",
            description:
              "Package name to remove, e.g. 'com.unity.textmeshpro'. A trailing '@<version>' is " +
              "stripped before being passed to Client.Remove. Use the bare package name (not the " +
              "versioned form).",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — pass [\"Packages/manifest.json\"]. The gate validates against this " + "scope; packages-lock.json is touched implicitly by UPM resolution and does not need " + "to be listed. There is no whole-project fallback." },
          gate: { ...GATE_PROP, description: "Gate mode. Default 'enforce' — fails the call if the remove surfaces new errors." },
        },
  },
);
