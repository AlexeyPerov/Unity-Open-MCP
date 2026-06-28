import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 5 / T20.5.1 — typed ScriptableObject create. Mutating: runs the
// full gate path with `paths_hint` scoped to the asset path. Resolves the type
// via the same resolver type_schema / invoke_method use (full name preferred,
// class-name fallback), instantiates it via ScriptableObject.CreateInstance,
// applies optional initial field patches (same value shape + conversion path as
// object_modify — no reimplementation), and writes the .asset via
// AssetDatabase.CreateAsset. The advantage over assembling a SO via
// execute_csharp is the clean, gate-integrated create path: the caller scopes
// paths_hint, the gate validates asset-reference fallout, and the optional
// field patches share object_modify's value vocabulary. Read / info access
// stays on object_get_data; arbitrary field edits stay on object_modify — this
// tool is the create-only convenience.
export const scriptableObjectCreate: Tool = {
  name: "unity_open_mcp_scriptableobject_create",
  description:
    "Create a ScriptableObject asset of a given type. Mutating: runs the full gate " +
    "path; `paths_hint` must be scoped to the new asset path. `type_name` is resolved " +
    "via the same resolver type_schema / invoke_method use (full name preferred, " +
    "class-name fallback) — the type must be a compiled ScriptableObject subclass. " +
    "Optional `fields` applies initial field patches at create time using the same " +
    "value shape as object_modify (scalars/strings/bools as JSON primitives; " +
    "vectors/colors as [x,y,(z,(w))]; object references as {\"path\": \"...\"} or null). " +
    "Per-entry field errors are accumulated and do not abort the create — the asset is " +
    "still written with whatever fields succeeded. To read a ScriptableObject use " +
    "object_get_data; to edit fields on an existing one use object_modify; to enumerate " +
    "SO assets by type use list_assets_of_type.",
  inputSchema: {
    type: "object",
    required: ["type_name", "asset_path", "paths_hint"],
    properties: {
      type_name: {
        type: "string",
        description:
          "Type to instantiate (full name preferred, class-name fallback). Must be a " +
          "ScriptableObject subclass that is already compiled. Use find_members / " +
          "type_schema to discover available types.",
      },
      asset_path: {
        type: "string",
        description:
          "Destination asset path. Must start with 'Assets/' and end with '.asset'. " +
          "Must not already exist — use object_modify to edit an existing asset.",
      },
      assembly_name: {
        type: "string",
        description:
          "Optional assembly simple name to disambiguate the type when multiple " +
          "assemblies define a type with the same name.",
      },
      fields: {
        type: "array",
        description:
          "Optional initial field patches applied at create time. Same shape as " +
          "object_modify's `fields` array: [{ name, value }]. Per-entry errors are " +
          "accumulated and reported in `fieldErrors`; they do not abort the create.",
        items: {
          type: "object",
          required: ["name", "value"],
          properties: {
            name: {
              type: "string",
              description: "Public field or property name on the ScriptableObject type.",
            },
            value: {
              description:
                "New value. Scalars/strings/bools as JSON primitives; vectors/colors as " +
                "[x,y,(z,(w))]; object references as {\"path\": \"...\"} or null.",
            },
          },
          additionalProperties: false,
        },
      },
      allow_static: {
        type: "boolean",
        default: false,
        description:
          "When false (default) the tool refuses to write static or init-only fields. " +
          "Set true to permit static-field writes (same contract as object_modify).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the new asset path.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
