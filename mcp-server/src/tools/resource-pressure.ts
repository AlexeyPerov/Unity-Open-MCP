import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

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
export const resourcePressure = makeTool(
  "unity_open_mcp_resource_pressure",
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
    "`fdMethod`, `approximate` (Windows handle count), `ceiling` (the resolved " +
    "fd ceiling — default 1024, configurable per project), `ceilingSource` " +
    "(`\"default\"` | `\"config\"` — whether `.unity-open-mcp/settings.json` " +
    "`resourcePressure.fdCeiling` overrode the default), `headroom`, " +
    "`pressureRatio`, `state` (`ok` | `warn` at ≥80% | `critical` at ≥90% | " +
    "`over_ceiling` when the count is at/above the ceiling | " +
    "`unknown` when the probe failed), `trend` (`stable` | `rising` | " +
    "`leaking` — monotonic climb across successive samples = leak in progress), " +
    "and `samples[]` (the session-scoped in-memory ring of recent samples for " +
    "this PID — no disk cache). IMPORTANT: with a real fd count (`fdMethod` " +
    "lsof/proc — only true numbered descriptors are counted, never mmap/txt " +
    "rows), `over_ceiling` IS an alarm: POSIX allocates the lowest free " +
    "descriptor number, so at/past the ceiling every NEW descriptor lands " +
    "above it and the next Mono IOSelector registration (e.g. the Bee build " +
    "driver's pipes) can hang the Editor — the response carries a " +
    "critical-level `warning`. A fresh healthy Unity 6 editor sits at ~160 " +
    "fds; real counts near the ceiling are never legitimate. The one soft " +
    "case is Windows: HandleCount covers kernel/GDI/user objects and " +
    "routinely exceeds 1024 on a healthy process, so an over-ceiling handle " +
    "count carries an informational `pressureNote` instead and only a " +
    "`leaking` trend alarms there. When a `warning` is present, surface it to " +
    "the operator (recommend saving scene work + restarting via the Hub) — do " +
    "NOT call `restart_editor` yet (the Editor is still healthy). Use this " +
    "after heavy automation (many recompiles / domain reloads) to catch fd " +
    "growth across reloads; execute_csharp responses also carry an in-band " +
    "fd-pressure advisory in agentNextSteps once the Editor passes 80% of the " +
    "ceiling. No disk cache — samples live in the session store and are " +
    "cleared on MCP-server restart.",
  {
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
  },
);
