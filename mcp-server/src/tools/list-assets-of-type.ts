import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 5 / T20.5.1 — typed asset listing by type. Read-only and gate-free.
// Resolves the type via the same resolver type_schema / invoke_method use (full
// name preferred, class-name fallback), then enumerates assets of that type
// under `folder` (default Assets) via AssetDatabase.FindAssets("t:<Type>").
// Offline-routeable in principle — the offline YAML/GUID index can answer
// t:<Type> filter queries without a live Editor (the live path here is the
// authoritative implementation). Complements the broader search_assets (which
// filters by name/component/guid/kind) — this tool is the typed shortcut for
// "give me every asset of this C# type under this folder".
export const listAssetsOfType: Tool = {
  name: "unity_open_mcp_list_assets_of_type",
  description:
    "List all assets of a given C# type under a folder. Read-only and gate-free. " +
    "`type_name` is resolved via the same resolver type_schema / invoke_method use " +
    "(full name preferred, class-name fallback); the tool enumerates assets of that " +
    "type under `folder` (default Assets) via AssetDatabase.FindAssets(\"t:<Type>\"). " +
    "Each result carries the asset path, name, resolved type, and instance id. " +
    "Offline-routeable in principle — the offline YAML/GUID index can answer " +
    "t:<Type> filter queries without a live Editor. Prefer this over search_assets " +
    "when you want every asset of one specific type.",
  inputSchema: {
    type: "object",
    required: ["type_name"],
    properties: {
      type_name: {
        type: "string",
        description:
          "Type to enumerate (full name preferred, class-name fallback). When the type " +
          "cannot be resolved the tool falls back to a name-based t:<Name> filter so a " +
          "class-name-only query still works.",
      },
      assembly_name: {
        type: "string",
        description: "Optional assembly simple name to disambiguate the type.",
      },
      folder: {
        type: "string",
        default: "Assets",
        description: "Folder to search under (default Assets). Must start with 'Assets/'.",
      },
      include_indirect: {
        type: "boolean",
        default: false,
        description: "Reserved for schema parity with list_assets. Default false.",
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
