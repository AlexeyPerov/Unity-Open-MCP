import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 10 / T6.6.10 — Animation extension tool. Requires the animation
// extension pack. Mutating + DESTRUCTIVE: runs the full gate path.
export const animatorModify = makeTool(
  "unity_open_mcp_animator_modify",
  "Apply a batch of modifications to an AnimatorController asset (.controller). " +
    "modifications_json is a JSON array of entries dispatched by `type`: " +
    "AddParameter / RemoveParameter / AddLayer / RemoveLayer / AddState / " +
    "RemoveState / SetDefaultState / AddTransition / RemoveTransition / " +
    "AddAnyStateTransition / SetStateMotion / SetStateSpeed. Per-entry errors " +
    "are accumulated in `errors` and do not abort the batch. Use " +
    "animator_get_data first to discover valid layer / state / parameter names. " +
    "DESTRUCTIVE — some modifications (RemoveParameter / RemoveLayer / " +
    "RemoveState / RemoveTransition) are irreversible without undo. Mutating: " +
    "runs the full gate path; paths_hint is the .controller asset path. " +
    "Requires the animation extension pack installed in the project.",
  {
    required: ["asset_path", "modifications_json", "paths_hint"],
        properties: {
          asset_path: {
            type: "string",
            description: "'Assets/'-rooted path to the existing '.controller' asset.",
          },
          modifications_json: {
            type: "string",
            description:
              "JSON array of modification entries. Each entry has a `type` and " +
              "type-specific fields:\n" +
              "  AddParameter          { type, parameterName, parameterType (Float/Int/Bool/Trigger), defaultFloat?, defaultInt?, defaultBool? }\n" +
              "  RemoveParameter       { type, parameterName }\n" +
              "  AddLayer              { type, layerName }\n" +
              "  RemoveLayer           { type, layerName }\n" +
              "  AddState              { type, layerName, stateName, motionAssetPath? }\n" +
              "  RemoveState           { type, layerName, stateName }\n" +
              "  SetDefaultState       { type, layerName, stateName }\n" +
              "  AddTransition         { type, layerName, sourceStateName, destinationStateName, hasExitTime?, exitTime?, duration?, hasFixedDuration?, conditions?: [{parameter, mode (If/IfNot/Greater/Less/Equals/NotEqual), threshold?}] }\n" +
              "  RemoveTransition      { type, layerName, sourceStateName, destinationStateName }\n" +
              "  AddAnyStateTransition { type, layerName, destinationStateName, hasExitTime?, duration?, conditions?: [...] }\n" +
              "  SetStateMotion        { type, layerName, stateName, motionAssetPath }\n" +
              "  SetStateSpeed         { type, layerName, stateName, speed }",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the .controller asset path." },
          gate: { ...GATE_PROP },
        },
  },
);
