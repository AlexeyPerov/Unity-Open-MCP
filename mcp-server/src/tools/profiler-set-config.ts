import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M16 Plan 7 — typed profiler config mutator. Mutates editor state but writes
// NO assets; gate-free direct-response tool. Only the knobs Unity's runtime
// API exposes for in-place writes are honored; the rest are reported as
// warnings (the request is recorded, the value is not applied). Returns the
// post-write config (same shape as profiler_get_config) plus a warnings[]
// list of any non-applicable knobs.
export const profilerSetConfig = makeTool(
  "unity_open_mcp_profiler_set_config",
  "Update one or more profiler runtime knobs in a single call: mode (\"play\" or \"edit\" — " +
    "edit-mode targeting requires a supported Unity version), deepProfile (deep profiling — high " +
    "overhead), allocationCallstacks (capture GC.Alloc callstacks — high overhead), binaryLog " +
    "(Editor runtime usually ignores this; reported as a warning when not applied), output (the " +
    "Profiler.logFile path; parent dirs are created), maxUsedMemory (bytes; clamped to a safe " +
    "editor budget), enableCategories[] / disableCategories[] (ProfilerCategory name toggles). " +
    "Mutates editor state only (no asset writes); gate-free. Returns the post-write config plus " +
    "a warnings[] list of any knobs that were recorded but not applied on this Unity version. " +
    "Use profiler_start / profiler_stop to flip the enabled flag itself.",
  {
    properties: {
          mode: {
            type: "string",
            enum: ["play", "edit"],
            description:
              "Edit-vs-play targeting. \"play\" enters play mode if not already there; \"edit\" " +
              "exits play mode. The matching Editor ProfilerDriver.profileEditor knob is also flipped " +
              "on supported Unity versions. Reported as a warning when profileEditor automation is " +
              "unavailable.",
          },
          deep_profile: {
            type: "boolean",
            description:
              "Toggle deep profiling (every method call instrumented). High runtime overhead; " +
              "toggling at runtime only takes effect on supported Unity versions.",
          },
          allocation_callstacks: {
            type: "boolean",
            description:
              "Toggle GC.Alloc callstack capture. Adds noticeable profiler overhead and can trigger " +
              "frame drops / heavy editor memory pressure during longer captures.",
          },
          binary_log: {
            type: "boolean",
            description:
              "Toggle Profiler.enableBinaryLog. The Unity Editor keeps this disabled at runtime — a " +
              "true request is recorded but reported as a warning (raw binary capture requires a " +
              "player build or manual Profiler window export).",
          },
          output: {
            type: "string",
            description:
              "Profiler.logFile path. Parent directories are created. Used together with binary_log " +
              "(when the Editor honors it).",
          },
          max_used_memory: {
            type: "integer",
            minimum: 0,
            description:
              "Profiler.maxUsedMemory in bytes. Clamped to a safe editor budget (minimum 16 MiB, " +
              "absolute cap depends on whether deep_profile / allocation_callstacks are on).",
          },
          enable_categories: {
            type: "array",
            items: { type: "string" },
            description:
              "ProfilerCategory names to enable (e.g. \"Render\", \"Memory\", \"Physics\"). Unknown " +
              "names are reported as warnings; the open Profiler window can override category " +
              "settings to match active charts.",
          },
          disable_categories: {
            type: "array",
            items: { type: "string" },
            description:
              "ProfilerCategory names to disable. Same behavior as enable_categories for unknown names.",
          },
        },
  },
);
