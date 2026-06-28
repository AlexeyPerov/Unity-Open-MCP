import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 5 / T20.5.2 — typed Assembly Definition list. Read-only and gate-
// free. Enumerates every .asmdef asset under `folder` (default Assets) via
// AssetDatabase.FindAssets("t:AssemblyDefinitionAsset"), parses each into a
// summary model (name, reference count, include/exclude platform counts, define
// constraint count, autoReferenced). Package asmdefs (under Packages/) are
// read-only and noisy, so they are excluded unless `include_packages` is true.
// Offline-routeable in principle (.asmdef is plain JSON, the offline index can
// parse it without a live Editor). Use asmdef_get for the full parsed model.
export const asmdefList: Tool = {
  name: "unity_open_mcp_asmdef_list",
  description:
    "List all Assembly Definition (.asmdef) assets under a folder. Read-only and " +
    "gate-free. Returns a summary per asmdef — name, asset path, reference count, " +
    "include/exclude platform counts, define-constraint count, autoReferenced. " +
    "Package asmdefs (under Packages/) are excluded unless include_packages is true " +
    "(they are read-only and noisy). Offline-routeable in principle (.asmdef is plain " +
    "JSON). Use asmdef_get for the full parsed model of one asmdef.",
  inputSchema: {
    type: "object",
    properties: {
      folder: {
        type: "string",
        default: "Assets",
        description: "Folder to search under (default Assets). Must start with 'Assets/'.",
      },
      include_packages: {
        type: "boolean",
        default: false,
        description:
          "Also list assembly definitions under Packages/ (default false). Package " +
          "asmdefs are read-only — exclude them unless you need to inspect them.",
      },
      max_results: {
        type: "integer",
        default: 200,
        minimum: 1,
        description: "Max results returned (default 200). Extra matches are counted in `truncated`.",
      },
    },
    additionalProperties: false,
  },
};
