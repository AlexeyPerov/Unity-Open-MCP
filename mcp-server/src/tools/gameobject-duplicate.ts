import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 2 — typed GameObject duplicate. Mutating: runs the full gate path.
// Scene side-effect — scope paths_hint to the scene that contains the target.
export const gameobjectDuplicate = makeTool(
  "unity_open_mcp_gameobject_duplicate",
  "Duplicate a GameObject (and its children) as the Editor's Duplicate (Ctrl+D) does: the copy sits right " +
    "after the source under the same parent, in the same scene, prefab stage or prefab asset, and keeps the " +
    "source's name. One undo step (an in_prefab edit is not undoable). Mutating: runs the full gate path; " +
    "`paths_hint` is the scene path (or the prefab asset path) that contains the source. Returns the clone's " +
    "instanceId, name, path, container, transform, component list, and `prefabConnection`: \"preserved\" — " +
    "the source is a prefab instance's root (outermost or nested) and the copy is a connected instance of the " +
    "same prefab with all the source's overrides (property values, added / removed components, added " +
    "children); \"nested_to_plain\" — the source is another part of a prefab instance and the copy is a plain " +
    "GameObject added to that instance; \"plain\" — the source is not part of a prefab instance. Address the " +
    "source by instance_id > path > name.",
  {
    required: ["paths_hint"],
        properties: {
          instance_id: {
            type: ["string", "integer"],
            default: 0,
            description: "Source GameObject instance ID. Highest priority resolver.",
          },
          path: {
            type: "string",
            description: "Source hierarchy path \"Root/Child\". Middle-priority resolver.",
          },
          name: {
            type: "string",
            description: "Source GameObject name (first match). Lowest priority resolver.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the scene path that contains the source and clone." },
          gate: { ...GATE_PROP },
        },
  },
);
