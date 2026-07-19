import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 6 — typed type-schema read. Read-only (gate-free). Returns a JSON
// schema describing any loadable C# type's public fields + properties, plus
// constructors and enum values when applicable. Complements find_members: the
// latter lists members by substring, this one emits a structured schema for a
// single resolved type so an agent can plan invoke_method / object_modify
// calls without trial-and-error. NOT a second reflection engine — it only
// surfaces reflection metadata in a stable shape.
export const typeSchema = makeTool(
  "unity_open_mcp_type_schema",
  "Generate a JSON schema for any loadable C# type's public fields and properties " +
    "(plus constructors and enum values when applicable). Read-only and gate-free. " +
    "Use this to plan invoke_method / object_modify calls without trial-and-error: it " +
    "returns each member's name, CLR type, declaring type, static/readonly flags, and " +
    "(for enums) the value names. Resolve the type by full name (preferred) or class " +
    "name fallback; pass an assembly_name to disambiguate. Complements find_members — " +
    "find_members lists members by substring across many types, this tool emits a " +
    "structured schema for one resolved type. Not a second reflection engine.",
  {
    required: ["type_name"],
        properties: {
          type_name: {
            type: "string",
            description:
              "Type to introspect. Full name preferred (e.g. \"UnityEngine.Rigidbody\"); " +
              "class-name fallback (e.g. \"Rigidbody\") resolves the first match.",
          },
          assembly_name: {
            type: "string",
            description: "Optional assembly simple name to disambiguate ambiguous type names.",
          },
          include_fields: {
            type: "boolean",
            default: true,
            description: "Include public fields.",
          },
          include_properties: {
            type: "boolean",
            default: true,
            description: "Include public properties.",
          },
          include_methods: {
            type: "boolean",
            default: false,
            description:
              "Include public method signatures (name + parameters + return type). Off by " +
              "default — use find_members when you need a method search across types.",
          },
          include_constructors: {
            type: "boolean",
            default: false,
            description: "Include public constructor signatures.",
          },
          max_members: {
            type: "integer",
            default: 100,
            minimum: 1,
            description: "Max members per section returned; remaining are reported in 'truncated'.",
          },
        },
  },
);
