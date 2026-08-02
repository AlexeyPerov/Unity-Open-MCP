import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { BRIDGE_DEFAULT_TIMEOUT_MS, BRIDGE_MIN_TIMEOUT_MS } from "../constants.js";
import { makeTool } from "./schema-fragments.js";

export const editorStatus = makeTool(
  "unity_open_mcp_editor_status",
  "Returns the current Unity Editor state: play mode, compile state, current scene path, Unity version, " +
    "editor type, and the in-memory dirty-scene summary. The dirtySceneCount + dirtyScenes:[{name,path}] fields " +
    "list every loaded scene with unsaved in-memory changes — the memory-vs-disk signal that tells you a " +
    "unity_open_mcp_scene_save is pending before you reason against on-disk YAML (e.g. after a structural op " +
    "like gameobject_set_parent that marks a scene dirty without writing it).",
  {
    properties: {
          timeout_ms: {
            type: "integer",
            default: BRIDGE_DEFAULT_TIMEOUT_MS,
            minimum: BRIDGE_MIN_TIMEOUT_MS,
            maximum: 300000,
          },
        },
  },
);
