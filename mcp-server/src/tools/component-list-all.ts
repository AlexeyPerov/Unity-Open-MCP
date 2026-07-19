import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 2 — read-only component catalog. Gate-free, token-bounded.
export const componentListAll = makeTool(
  "unity_open_mcp_component_list_all",
  "Enumerate component types that can be attached via AddComponent — built-in + project " +
    "MonoBehaviours from every loaded assembly. Read-only (gate-free). Token-bounded by " +
    "max_results. Use the optional `query` substring to narrow by namespace/type-name (case-" +
    "insensitive, matched against both the full name and the bare class name). Each entry " +
    "returns fullName (use this in component_add / type_name), name (short class name), " +
    "namespace, assembly, and builtin flag. Use this before component_add to discover valid type names.",
  {
    properties: {
          query: {
            type: "string",
            description:
              "Optional case-insensitive substring filter on the type's full name OR class name. " +
              "Example: \"Rigidbody\" matches UnityEngine.Rigidbody and MyNamespace.RigidbodyController.",
          },
          max_results: {
            type: "integer",
            default: 200,
            minimum: 1,
            description: "Max types returned; remaining count is reported in 'truncated'.",
          },
          include_builtin: {
            type: "boolean",
            default: true,
            description:
              "Include Unity built-in components (UnityEngine.* / UnityEditor.* assemblies). Default true.",
          },
          include_project: {
            type: "boolean",
            default: true,
            description:
              "Include project + package MonoBehaviours from loaded assemblies. Default true.",
          },
        },
  },
);
