import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 5 — typed editor add layer. Mutating: assigns a layer name to a
// slot in the TagManager (ProjectSettings/TagManager.asset) and saves it. Runs
// the full gate path with paths_hint scoped to that asset. Folds UCP
// settings/add-layer. Layers are index-keyed (0–31); this tool finds the first
// empty slot (or uses an explicit slot), assigns the name, and saves.
export const editorAddLayer = makeTool(
  "unity_open_mcp_editor_add_layer",
  "Add a user-defined layer to the project's TagManager. Mutating: assigns " +
    "the layer name to a slot, writes ProjectSettings/TagManager.asset, and " +
    "refreshes the asset database. Runs the full gate path; paths_hint should " +
    "be [\"ProjectSettings/TagManager.asset\"]. By default picks the first " +
    "empty slot (slots 0–7 are reserved for built-ins, so user layers land in " +
    "8–31); pass slot to assign a specific index. Refuses reserved built-in " +
    "layer names and slots, refuses when no free slot remains, and is " +
    "idempotent (re-assigning the same name to the same slot is a no-op). " +
    "Prefer this over raw execute_csharp TagManager manipulation — schema-" +
    "validated, undo-recorded, and the gate surfaces any fallout from the " +
    "TagManager rewrite. Pair with editor_get_layers to confirm the addition.",
  {
    required: ["layer", "paths_hint"],
        properties: {
          layer: {
            type: "string",
            description:
              "The layer name to add. Must be non-empty and not a reserved " +
              "built-in layer name (Default, TransparentFX, IgnoreRaycast, Water, " +
              "UI). Leading/trailing whitespace is trimmed.",
          },
          slot: {
            type: "integer",
            minimum: 8,
            maximum: 31,
            description:
              "Explicit slot index (8–31) to assign. Slots 0–7 are reserved for " +
              "Unity built-ins. When omitted, the first empty slot in 8–31 is " +
              "used. Refuses when the chosen slot is occupied by a different name.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — [\"ProjectSettings/TagManager.asset\"] (the asset " + "this tool rewrites)." },
          gate: { ...GATE_PROP },
        },
  },
);
