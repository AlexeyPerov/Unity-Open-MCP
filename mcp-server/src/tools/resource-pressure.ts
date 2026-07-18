import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M31 Plan 3 / T31.4 — Editor fd-exhaustion prediction (proactive fd-usage
// monitoring).
//
// Companion to `restart_editor` (the reactive kill half) and `read_compile_errors`
// (the diagnosis half). This tool samples the live Unity process's
// file-descriptor count BEFORE exhaustion so an agent can warn the operator to
// save and restart while the bridge is still healthy. The bridge is the thing
// that dies on fd-exhaustion, so the probe runs server-side against the OS —
// it does NOT depend on the bridge being reachable.
//
// Operator surface: no group assignment in `capabilities/tool-groups.ts`, so
// it sits in the always-visible meta-tool bucket alongside `bridge_status` /
// `read_compile_errors` / `restart_editor`. Read-only, gate-free, local-routed.
//
// Surface decision (the plan's "where to surface" choice): the standalone tool
// is the lower-risk first cut — no status-payload churn, no recurring lsof/proc
// cost on the frequently-called status path. The signal can be upgraded to an
// inline `bridge_status` field once thresholds are validated against real leak
// rates.
export const resourcePressure: Tool = {
  name: "unity_open_mcp_resource_pressure",
  description:
    "Sample the live Unity process's file-descriptor usage and report headroom " +
    "against Mono's internal ~1024 fd ceiling — the real trip point for the " +
    "Bee build-driver fd-exhaustion hang ('Could not register to wait for file " +
    "descriptor N'). Proactive counterpart to `restart_editor` (reactive kill) " +
    "and `read_compile_errors` (diagnosis): lets an agent warn the operator to " +
    "save and restart BEFORE the Editor hangs, while the bridge is still " +
    "healthy. The probe runs server-side against the OS (macOS: `lsof -p " +
    "<pid>`; Linux: `/proc/<pid>/fd`; Windows: `Get-Process -Id <pid>." +
    "HandleCount` — approximate) and does NOT require the bridge to be " +
    "reachable — the bridge is what dies on fd-exhaustion. Resolves the live " +
    "Unity PID via the process scan (same as `bridge_status` cold-Safe-Mode " +
    "detection). Response fields: `fdCount` (null when the probe failed), " +
    "`fdMethod`, `approximate` (Windows handle count), `ceiling` (fixed 1024), " +
    "`headroom`, `pressureRatio`, `state` (`ok` | `warn` at ≥80% | `critical` " +
    "at ≥90% | `unknown` when the probe failed), `trend` (`stable` | `rising` " +
    "| `leaking` — monotonic climb across successive samples = leak in " +
    "progress; absolute count alone is not enough), and `samples[]` (the " +
    "session-scoped in-memory ring of recent samples for this PID — no disk " +
    "cache). When `state` is warn/critical or `trend.state` is leaking, the " +
    "agent should surface the risk to the operator and recommend saving scene " +
    "work + restarting via the Hub before the next domain reload trips the " +
    "ceiling. Use this after heavy automation (many recompiles / domain " +
    "reloads) to catch fd growth across reloads. No disk cache — samples live " +
    "in the session store and are cleared on MCP-server restart.",
  inputSchema: {
    type: "object",
    properties: {
      pid: {
        type: "integer",
        minimum: 1,
        description:
          "Optional explicit Unity PID to probe. When omitted, the tool " +
          "resolves the live Unity process for this project via the process " +
          "scan (same as `bridge_status` cold-Safe-Mode detection). Pass an " +
          "explicit PID only when you have one from a prior call and want to " +
          "skip the scan.",
      },
    },
    additionalProperties: false,
  },
};
