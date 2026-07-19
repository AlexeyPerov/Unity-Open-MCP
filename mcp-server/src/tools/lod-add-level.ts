import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 3 / T20.3.3 — Constraints & LOD domain tool. Add or replace a LOD
// entry on a LODGroup at an index, resolving renderers from GameObject paths.
// Built-in engine module. Mutating: runs the full gate path; paths_hint is the
// host scene path. The host must already carry a LODGroup (use
// lod_group_configure first).
const targetSchema = {
  instance_id: {
    type: ["string", "integer"],
    default: 0,
    description: "Host GameObject instance ID (the GameObject carrying the LODGroup).",
  },
  path: {
    type: "string",
    description: "Host hierarchy path \"Root/Child\".",
  },
  name: {
    type: "string",
    description: "Host GameObject name (first match). Lowest priority resolver.",
  },
  paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — the host scene path. No whole-project fallback." },
  gate: { ...GATE_PROP },
};

export const lodAddLevel = makeTool(
  "unity_open_mcp_lod_add_level",
  "Add or replace a LOD entry on a LODGroup at an index. Resolves the renderers " +
    "from an array of GameObject paths (each GameObject must carry a Renderer — " +
    "usually a MeshRenderer on a child mesh). When the index is within the existing " +
    "LOD array, the entry is replaced in place; when it equals the array length, a " +
    "new level is appended. The host must already carry a LODGroup (use " +
    "lod_group_configure first). Mutating: runs the full gate path; paths_hint is the " +
    "host scene path. Built-in engine module (no package dependency); the constraints " +
    "group is hidden until manage_tools activates it.",
  {
    required: ["paths_hint"],
        properties: {
          ...targetSchema,
          index: {
            type: "integer",
            default: 0,
            description:
              "LOD index. Within the existing array → replace; == lodCount → append.",
          },
          screen_relative_transition_height: {
            type: "number",
            default: 0.5,
            description: "Screen-relative transition height (0-1). Clamped.",
          },
          renderers: {
            type: "array",
            items: { type: "string" },
            description:
              "Renderer GameObject paths (or 'iid:<n>' instance-id hints). Each " +
              "GameObject must carry a Renderer (usually a MeshRenderer on a child mesh). " +
              "Entries that fail to resolve are reported in unresolvedRenderers.",
          },
        },
  },
);
