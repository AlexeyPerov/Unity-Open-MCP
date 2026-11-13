import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

export const readConsole = makeTool(
  "unity_senses_read_console",
  "Read the Unity Editor console log entries. Filter by type (error/warning/log/all) or several `types`, " +
    "by message text (`contains` substring or `pattern` regex), and by position (`since_index` — pass the " +
    "previous response's `nextSinceIndex` to read only newer entries), " +
    "optionally clear the console, and get structured entries with stack traces. " +
    "Unity-internal stack frames are stripped by default for readability. " +
    "Useful after execute_csharp or mutations to check for compile errors, " +
    "runtime exceptions, or debug logs. Requires a live Unity Editor connection. " +
    "Token-bounded: `detail` controls stack verbosity, `truncated` always reports " +
    "how many entries were dropped by `max_entries`.",
  {
    properties: {
          type: {
            type: "string",
            enum: ["error", "warning", "log", "all"],
            default: "all",
            description: "Filter entries by log type.",
          },
          types: {
            type: "array",
            items: { type: "string", enum: ["error", "exception", "assert", "warning", "log"] },
            minItems: 1,
            description: "Several log types at once (exception/assert count as error); overrides type.",
          },
          contains: {
            type: "string",
            minLength: 1,
            description: "Keep entries whose message contains this text (case-insensitive).",
          },
          pattern: {
            type: "string",
            minLength: 1,
            description:
              "Keep entries whose message matches this .NET regex (same syntax as playtest_wait log.pattern: " +
              "case-sensitive, prefix (?i) to ignore case). Combines with contains (both must match).",
          },
          since_index: {
            type: "integer",
            minimum: 0,
            description:
              "Keep entries at console row index >= this. Each response reports `nextSinceIndex` (the row count " +
              "at read time); pass it back to read only what was logged since. Clearing the console resets rows to 0.",
          },
          limit: { type: "integer", minimum: 1, maximum: 1000, deprecated: true, "x-alias-for": "max_entries", description: "Deprecated alias; use max_entries." },
          filter: { type: "string", minLength: 1, deprecated: true, "x-alias-for": "contains", description: "Deprecated alias; use contains." },
          clear: {
            type: "boolean",
            default: false,
            description: "Clear the console after reading entries.",
          },
          max_entries: {
            type: "integer",
            default: 100,
            minimum: 1,
            maximum: 1000,
            description:
              "Maximum number of entries to return (most recent). " +
              "Dropped entries are reported in `truncated`.",
          },
          max_stack_frames: {
            type: "integer",
            default: 20,
            minimum: 0,
            maximum: 100,
            description: "Maximum stack-trace frames per entry (ignored when detail: verbose).",
          },
          include_unity_frames: {
            type: "boolean",
            default: false,
            description:
              "Include Unity-internal stack frames (UnityEngine, UnityEditor, System). " +
              "Off by default for cleaner output. Forced on when detail: verbose.",
          },
          detail: {
            type: "string",
            enum: ["summary", "normal", "verbose"],
            default: "normal",
            description:
              "Token control. 'summary': message only, no stack traces. " +
              "'normal' (default): capped stack with Unity-internal frames stripped. " +
              "'verbose': full stack including Unity frames, ignoring max_stack_frames.",
          },
        },
  },
);
