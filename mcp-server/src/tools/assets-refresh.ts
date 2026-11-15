import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M16 Plan 1 — AssetDatabase.Refresh wrapper. Counts as a light mutation
// (it may trigger an import/compile), so it is marked mutating with gate
// Enforce, but its `paths_hint` may be the whole project when `whole_project`
// is true (Refresh itself is whole-project by nature). Per the plan, this
// is the only tool allowed to bind a whole-project scope because the
// refresh IS the whole-project operation.
export const assetsRefresh = makeTool(
  "unity_open_mcp_assets_refresh",
  "Refresh the AssetDatabase to pick up files added/removed/changed on disk outside " +
    "Unity. Use after direct filesystem edits (e.g. creating folders, dropping textures). " +
    "When the refresh imports scripts and starts a compile, the call waits for that compile " +
    "and its domain reload and the compile is the answer: `status` `compiled` | `compile_failed` | " +
    "`compiling` (past ~40 s the wait continues as a unity_open_mcp_jobs job) and " +
    "`compile: {generationAtStart, generation, status, errorCount, errors}`; a failed compile " +
    "fails the call (mutation.error.code `compile_failed`; the import itself is kept). A refresh " +
    "that starts no compile answers at once, unchanged. An open scene whose file changed on disk " +
    "(git checkout, a generator) is reloaded in place around the refresh and listed in " +
    "`reloadedScenes`, so Unity's native \"Scene(s) Have Been Modified\" dialog never blocks the " +
    "Editor; when that would lose unsaved edits (or play mode is on, or a prefab stage with unsaved " +
    "changes has its prefab changed) the refresh is refused with `open_scene_changed_on_disk`, naming " +
    "the scenes and the choices (scene_save keeps the edits, scene_open takes the disk version). Light mutation: " +
    "runs the gate path. Pass `whole_project: true` (default) " +
    "when you changed files in arbitrary places; pass it `false` with a scoped `paths_hint` " +
    "for a known small change set.",
  {
    properties: {
          whole_project: {
            type: "boolean",
            default: true,
            description:
              "When true (default), runs a full AssetDatabase.Refresh() and paths_hint may be omitted " +
              "(refresh is whole-project by nature). When false, paths_hint is required and bounds the gate scope.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Optional. Required when whole_project is false. Omitted/ignored when whole_project is true." },
          gate: { ...GATE_PROP },
        },
  },
);
