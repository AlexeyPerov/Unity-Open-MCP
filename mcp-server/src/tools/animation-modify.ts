import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.10 — Animation extension tool. Requires the animation
// extension pack. Mutating + DESTRUCTIVE: runs the full gate path.
export const animationModify: Tool = {
  name: "unity_open_mcp_animation_modify",
  description:
    "Apply a batch of modifications to an AnimationClip asset (.anim). " +
    "modifications_json is a JSON array of entries dispatched by `type`: " +
    "SetCurve / RemoveCurve / ClearCurves / SetFrameRate / SetWrapMode / " +
    "SetLegacy / AddEvent / ClearEvents. Per-entry errors are accumulated in " +
    "`errors` and do not abort the batch. Use animation_get_data first to " +
    "discover valid (componentType, propertyName) tuples. DESTRUCTIVE — some " +
    "modifications (ClearCurves / ClearEvents / RemoveCurve) are irreversible " +
    "without undo. Mutating: runs the full gate path; paths_hint is the .anim " +
    "asset path. Requires the animation extension pack installed in the project.",
  inputSchema: {
    type: "object",
    required: ["asset_path", "modifications_json", "paths_hint"],
    properties: {
      asset_path: {
        type: "string",
        description: "'Assets/'-rooted path to the existing '.anim' asset.",
      },
      modifications_json: {
        type: "string",
        description:
          "JSON array of modification entries. Each entry has a `type` and " +
          "type-specific fields:\n" +
          "  SetCurve    { type, componentType, propertyName, relativePath?, keyframes: [{time, value, inTangent?, outTangent?}] }\n" +
          "  RemoveCurve { type, componentType, propertyName, relativePath? }\n" +
          "  ClearCurves { type }\n" +
          "  SetFrameRate { type, frameRate }\n" +
          "  SetWrapMode  { type, wrapMode (Default/Once/Loop/PingPong/ClampForever) }\n" +
          "  SetLegacy    { type, legacy (bool) }\n" +
          "  AddEvent     { type, time, functionName, floatParameter?, intParameter?, stringParameter? }\n" +
          "  ClearEvents  { type }",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the .anim asset path.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
