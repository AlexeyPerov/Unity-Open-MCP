import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// Operator-only bridge health snapshot (testsuite-tauri phase-3). A thin
// wrapper over the existing instance-lock classifier (classifyInstance in
// instance-discovery.ts) and one /ping probe. It exists so the Validation
// Suite app and operators have a single tool that returns a coarse
// `status` token ("running" | "stopped" | "compiling" | "dead_bridge" |
// "unreachable") alongside the underlying signals (lock classification +
// ping body), used to drive the manual bridge-offline scenario pattern
// (operator stops the bridge via the Unity toolbar; this tool confirms it;
// on restart `wait-for-ready` confirms readiness).
//
// Deliberately **not** an agent-surface tool:
//  - It carries no group assignment in `capabilities/tool-groups.ts`, so it
//    sits in the always-visible meta-tool bucket alongside `ping` /
//    `read_compile_errors` (operators reach it; agent skill does NOT
//    document it in mutate/gate sections).
//  - It is read-only, gate-free, and never spawns Unity (no batch form).
//
// The two follow-on admin tools — `bridge_stop` / `bridge_start` — are
// deferred: the bridge has no HTTP route for start/stop today (only the
// Unity toolbar toggles it), and `stop` has a self-disconnect hazard. See
// the deferred rationale in docs/api/mcp-tools.md and the testsuite-tauri
// execution plan.
export const bridgeStatus: Tool = {
  name: "unity_open_mcp_bridge_status",
  description:
    "Operator-only bridge health snapshot. Wraps the instance-lock " +
    "classifier (instance-discovery.ts#classifyInstance) plus a single " +
    "/ping probe and returns a coarse `status` token: " +
    "`running` (bridge connected, idle), `compiling` (bridge connected, " +
    "Unity compiling), `stopped` (Unity not running OR toolbar bridge " +
    "toggle off — no live listener), `unreachable` (Unity process alive " +
    "but the listener did not respond — usually a transient domain-reload " +
    "window; retry shortly), or `dead_bridge` (Unity process " +
    "alive but the bridge assembly failed to recompile, so /ping will " +
    "never recover; call unity_open_mcp_read_compile_errors). " +
    "Designed for the Validation Suite's manual bridge-offline scenario " +
    "pattern and operators confirming toolbar stop/start — not a " +
    "general agent health check (use unity_open_mcp_ping for that). " +
    "Read-only, gate-free, never spawns Unity. The /ping fetch uses the " +
    "bridge's standard 5s timeout; this tool takes no arguments.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
