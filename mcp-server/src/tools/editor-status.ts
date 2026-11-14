import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { BRIDGE_MIN_TIMEOUT_MS } from "../constants.js";
import { makeTool } from "./schema-fragments.js";
import { WAIT_DEFAULT_TIMEOUT_MS, WAIT_INLINE_LIMIT_MS, WAIT_MAX_TIMEOUT_MS } from "../editor-state.js";

export const editorStatus = makeTool(
  "unity_open_mcp_editor_status",
  "The Editor's state, and the one way to wait for it. Always answers: play / pause / compile state, the " +
    "active scene and the in-memory dirty-scene summary (dirtySceneCount + dirtyScenes:[{name,path}] — the " +
    "memory-vs-disk signal that a unity_open_mcp_scene_save is pending), plus `state`, `transition` " +
    "(compiling / reloading / entering_playmode / exiting_playmode, or null), `reloading`, `compileGeneration` " +
    "and `compilePending`. While the Editor compiles, reloads or changes play mode the answer is read from the " +
    "instance lock and /ping (`source: \"lock\"`, with `retryAfterMs`) instead of failing. " +
    "With `wait_until` it BLOCKS until the Editor is settled — no compile, domain reload or play-mode change in " +
    "progress, no requested compile still to start — and in the requested mode, then returns the final state with " +
    "the compile generation, status and error count (the same count read_compile_errors reports). Use it after " +
    "editor_set_state, assets_refresh, recompile_scripts, script_write, asmdef or package edits, instead of " +
    "polling. Pass `after_generation` (the `compile.generationAtStart` a call returned) to require that the " +
    "compile the call caused has run. The wait survives the domain reload it waits for; past " +
    `~${WAIT_INLINE_LIMIT_MS / 1000} s it continues as a unity_open_mcp_jobs job and returns its job_id.`,
  {
    properties: {
      wait_until: {
        type: "string",
        enum: ["idle", "playing", "edit"],
        description:
          "Block until the Editor is settled: \"idle\" (either mode), \"playing\" (in play mode) or \"edit\" " +
          "(in edit mode). A requested mode that does not come (e.g. play mode refused because scripts do not " +
          "compile) returns wait_not_reached once the Editor has settled in the other mode for a few seconds.",
      },
      after_generation: {
        type: "integer",
        minimum: 0,
        description:
          "With wait_until: also require a compile that started after this compile generation (the " +
          "`compile.generationAtStart` of the call that changed scripts) to have finished. If the Editor reports " +
          "no compile due, the wait returns after a short grace with compile.ranAfterGeneration: false.",
      },
      timeout_ms: {
        type: "integer",
        minimum: BRIDGE_MIN_TIMEOUT_MS,
        maximum: WAIT_MAX_TIMEOUT_MS,
        description:
          `With wait_until: the wait's budget (default ${WAIT_DEFAULT_TIMEOUT_MS}); up to ${WAIT_INLINE_LIMIT_MS} ms ` +
          "is waited inline, the rest as a jobs wait. Without wait_until: how long the bridge may take to " +
          "answer (capped at 5000; the lock answers otherwise).",
      },
    },
  },
);
