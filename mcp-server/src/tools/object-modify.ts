import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 6 — typed object modify. Mutating: sets public fields/properties on
// any live UnityEngine.Object (scene instance or asset) via reflection. Runs
// the full gate path with paths_hint scoped to the asset path (for assets) or
// the scene path (for scene objects). Explicit per-field scope; safe defaults
// (no static/readonly writes by default, no method invocation). Folds UMCP
// object-modify. Complements component_modify (which uses SerializedObject and
// is the canonical path for Inspector fields on Components).
export const objectModify: Tool = {
  name: "unity_open_mcp_object_modify",
  description:
    "Modify public fields and properties on a live UnityEngine.Object (scene GameObject, " +
    "ScriptableObject, Material, or any asset) by name. Mutating: runs the full gate path; " +
    "`paths_hint` must be scoped to the asset path (for assets) or the scene path (for " +
    "scene objects). `fields` is an array of {name, value} patches applied in order; per- " +
    "entry errors are accumulated, so a single bad patch does not abort the batch. Safe by " +
    "default: refuses to write static or init-only/readonly fields unless `allow_static: " +
    "true` and refuses to invoke methods. For Component Inspector fields prefer " +
    "component_modify (it uses SerializedObject and round-trips the same way as the " +
    "Inspector); use this tool for ScriptableObjects, Materials, or any non-Component Object.",
  inputSchema: {
    type: "object",
    required: ["fields", "paths_hint"],
    properties: {
      instance_id: {
        type: ["string", "integer"],
        default: 0,
        description: "Instance ID of a live UnityEngine.Object. Highest priority resolver.",
      },
      asset_path: {
        type: "string",
        description: "Asset path. Used when instance_id is not set.",
      },
      fields: {
        type: "array",
        description:
          "Patches to apply in order. Each entry: { name: string, value: any }. `name` is " +
          "the public field or property name. Scalars/strings/bools as JSON primitives; " +
          "vectors/colors as [x,y,(z,(w))]; object references as {\"path\": \"...\"}, " +
          "{\"instance_id\": N}, or null.",
        items: {
          type: "object",
          required: ["name", "value"],
          properties: {
            name: {
              type: "string",
              description: "Public field or property name on the target object.",
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
          "When false (default) the tool refuses to write static or init-only fields. Set " +
          "true to permit static-field writes.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the asset path (for assets) or scene path (for scene objects).",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
