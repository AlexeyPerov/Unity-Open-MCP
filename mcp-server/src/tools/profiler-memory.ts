import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const profilerMemory: Tool = {
  name: "unity_agent_profiler_memory",
  description:
    "Snapshot live Unity memory allocator stats: total allocated, reserved, " +
    "unused reserved, temp allocator size, and the managed (GC) heap. Both " +
    "raw byte counts and human-readable strings are returned. Set " +
    "gc_collect=true to run a full GC first for a stable baseline. Requires " +
    "a live Unity Editor connection.",
  inputSchema: {
    type: "object",
    properties: {
      gc_collect: {
        type: "boolean",
        default: false,
        description:
          "Run a full GC.Collect (with finalizers) before sampling for a " +
          "stable baseline.",
      },
    },
    additionalProperties: false,
  },
};
