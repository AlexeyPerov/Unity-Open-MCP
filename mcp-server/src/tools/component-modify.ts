import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 2 — typed component modify. Mutating: runs the full gate path.
// Per-path serialized patches via SerializedObject.
export const componentModify = makeTool(
  "unity_open_mcp_component_modify",
  "Modify serialized fields on a Component by path via SerializedObject. Undo-recorded. " +
    "Mutating: runs the full gate path; `paths_hint` is the scene path that contains the host. " +
    "`fields` is an array of {path, value, type?} patches where `path` is the SerializedProperty " +
    "path (e.g. \"m_Color\", \"m_Mass\", \"m_Colors.Array.data[0]\"). Per-entry errors are " +
    "accumulated, so a single bad patch does not abort the batch. Use component_get first to " +
    "discover valid paths and value types. Address the host by instance_id > path > name; " +
    "identify the component by component_instance_id or type_name.",
  {
    required: ["fields", "paths_hint"],
        properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Host GameObject instance ID. Highest priority resolver.",
          },
          path: {
            type: "string",
            description: "Host hierarchy path \"Root/Child\".",
          },
          name: {
            type: "string",
            description: "Host GameObject name (first match). Lowest priority resolver.",
          },
          type_name: {
            type: "string",
            description:
              "Component type to modify (full name preferred, class-name fallback). Use this OR " +
              "component_instance_id. Ignored when component_instance_id is set.",
          },
          component_instance_id: {
            type: ["string", "integer"],
            default: 0,
            description:
              "Specific Component instance ID. Takes precedence over type_name when set.",
          },
          fields: {
            type: "array",
            description:
              "Serialized-property patches to apply in order. Each entry: { path: string, value: any, type?: \"name\" | \"int\" }. " +
              "value shape depends on the property's SerializedPropertyType — see component_get output. " +
              "For enum properties, set type=\"name\" to set by enum name (default is int index). " +
              "For object references, value is {\"path\": \"Assets/...\"} or {\"instance_id\": N} or null.",
            items: {
              type: "object",
              required: ["path", "value"],
              properties: {
                path: {
                  type: "string",
                  description: "SerializedProperty.propertyPath (e.g. \"m_Color\", \"m_Mass\").",
                },
                value: {
                  description:
                    "New value. Scalars/strings/bools as JSON primitives; vectors/colors as [x,y,(z,(w))]; " +
                    "object references as {\"path\": \"...\"} or null; enums as int (default) or name " +
                    "(when type=\"name\").",
                },
                type: {
                  type: "string",
                  enum: ["name", "int"],
                  description:
                    "Optional hint. \"name\" = interpret value as an enum NAME (default is int index).",
                },
              },
              additionalProperties: false,
            },
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path that contains the host." },
          gate: { ...GATE_PROP },
        },
  },
);
