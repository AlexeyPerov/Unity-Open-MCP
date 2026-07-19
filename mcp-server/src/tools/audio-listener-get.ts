import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 3 / T20.3.1 — Audio domain tool. Reads AudioListener state across
// the open scene(s). Built-in audio module. Read-only, gate-free.
export const audioListenerGet = makeTool(
  "unity_open_mcp_audio_listener_get",
  "Read AudioListener state across the open scene(s). Reports each listener's host, " +
    "enabled flag, instance id, and hierarchy path, plus an enabled count. Unity allows " +
    "at most one enabled AudioListener at runtime — when more than one is enabled, a " +
    "`duplicateWarning` field is set so an agent can disable the extra listener before " +
    "entering Play Mode. Read-only, gate-free.",
  {
    properties: {},
  },
);
