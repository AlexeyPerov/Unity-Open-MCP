import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 5 — typed console clear. Mutates editor console state but touches
// no assets, so it routes as a gate-free direct-response tool (same footing as
// unity_senses_read_console, which already clears via `clear: true`).
// Complements the read side — do NOT duplicate it.
export const consoleClear: Tool = {
  name: "unity_open_mcp_console_clear",
  description:
    "Clear the Unity Editor console. Mutates console state only (no asset " +
    "writes), so it is gate-free and returns directly without the gate " +
    "envelope — same footing as unity_senses_read_console with clear: true. " +
    "Complements read_console (the read side). Prefer this over raw " +
    "execute_csharp LogEntries.Clear() — schema-validated and surfaced as a " +
    "first-class tool so a failed clear is visible as a structured error.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
