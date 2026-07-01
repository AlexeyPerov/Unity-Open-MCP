// Per-request routing + agent identity for multi-agent scheduling.
//
// When multiple agents share one MCP stdio process (or multiple MCP processes
// target one bridge), each tool call must carry (a) which bridge instance it
// is aimed at and (b) which agent made it, so the bridge can route fairly and
// the MCP server can bypass shared session state for a per-call port override.
//
// The model:
//   - PROCESS_AGENT_ID — one id per MCP stdio process (pid + 3-byte hex). All
//     calls from this process carry it unless an explicit override is supplied.
//   - _meta.port / top-level `port` — a per-call bridge port override. When
//     present, the MCP server routes the call to a LiveClient aimed at that
//     port instead of the default, bypassing shared state. The key is stripped
//     from args before forwarding so the bridge never sees an unknown field.
//   - _meta.agentId — optional per-call agent-id override (rare; the process
//     id is the right identity for the common case).
//
// The agent id is sent to the bridge as the `X-Agent-Id` header on every POST.
// The bridge's fair round-robin queue (packages/bridge/Editor/Bridge/
// BridgeRequestQueue.cs) uses it as the fairness key: when ≥2 distinct ids
// share one bridge, requests are scheduled read-batch(N)/write-serialize(1)
// per frame so a write-heavy agent cannot starve read-heavy agents.
//
// This module is pure aside from the PROCESS_AGENT_ID computation (random +
// pid, computed once at module load). The extraction helpers take args as an
// argument so they are unit-testable.

import { randomBytes } from "node:crypto";

/**
 * The agent id for THIS MCP stdio process. `agent-<pid>-<6 hex chars>` — pid
 * for cross-process disambiguation, hex suffix for uniqueness within a pid.
 * Computed once at module load and reused for every call that does not supply
 * an explicit `_meta.agentId` override.
 */
export const PROCESS_AGENT_ID = `agent-${process.pid}-${randomBytes(3).toString("hex")}`;

/** Minimum/maximum valid TCP port for an override. */
export const MIN_PORT = 1;
export const MAX_PORT = 65535;

/**
 * A top-level arg key that some clients use instead of `_meta.port`. We accept
 * both and strip both so the bridge body stays clean. (The bridge has no
 * `port` field on any tool; leaving it would be harmless but noisy.)
 */
const TOP_LEVEL_PORT_KEYS = ["port"] as const;

export interface RequestRouting {
  /**
   * The bridge port to route this call to. When undefined, the caller uses its
   * default LiveClient (no override).
   */
  portOverride: number | undefined;
  /**
   * The agent id to send as `X-Agent-Id`. Falls back to PROCESS_AGENT_ID when
   * no per-call override is supplied. Always defined.
   */
  agentId: string;
  /**
   * A shallow copy of `args` with `_meta`, top-level `port`, and any agent-id
   * override stripped, safe to forward to the bridge.
   */
  strippedArgs: Record<string, unknown>;
}

/**
 * Extract per-request routing + agent identity from a tool call's arguments.
 *
 * Precedence for port:
 *   1. `args._meta.port` (the MCP-standard meta envelope)
 *   2. `args.port` (top-level convenience key)
 *   3. undefined (use the default LiveClient)
 *
 * Precedence for agentId:
 *   1. `args._meta.agentId` (explicit per-call override)
 *   2. PROCESS_AGENT_ID (the process-wide default)
 *
 * Invalid ports (non-integer, out of range) are ignored — the call falls back
 * to the default port rather than failing. This matches the philosophy that a
 * bad override should degrade gracefully, not break the agent's workflow.
 *
 * `_meta` is stripped entirely (it may carry other meta fields the bridge
 * should not see); top-level `port` is also stripped.
 */
export function extractRouting(
  args: Record<string, unknown>,
): RequestRouting {
  const meta = args._meta;
  const metaPort = isPlainObject(meta) ? meta.port : undefined;
  const metaAgentId = isPlainObject(meta) ? meta.agentId : undefined;

  // Port precedence: _meta.port → top-level port.
  let portOverride = parsePort(metaPort);
  if (portOverride === undefined) {
    for (const key of TOP_LEVEL_PORT_KEYS) {
      if (key in args) {
        portOverride = parsePort(args[key]);
        if (portOverride !== undefined) break;
      }
    }
  }

  // Agent-id precedence: _meta.agentId → PROCESS_AGENT_ID.
  const agentId =
    typeof metaAgentId === "string" && metaAgentId.length > 0
      ? metaAgentId
      : PROCESS_AGENT_ID;

  // Strip _meta + top-level port keys. Shallow copy so the caller's object is
  // untouched.
  const strippedArgs: Record<string, unknown> = { ...args };
  delete strippedArgs._meta;
  for (const key of TOP_LEVEL_PORT_KEYS) {
    delete strippedArgs[key];
  }

  return { portOverride, agentId, strippedArgs };
}

/**
 * Parse a port value that may arrive as a number or a numeric string. Returns
 * undefined for anything that is not a valid integer in [MIN_PORT, MAX_PORT].
 */
export function parsePort(value: unknown): number | undefined {
  if (typeof value === "number" && Number.isInteger(value)) {
    return value >= MIN_PORT && value <= MAX_PORT ? value : undefined;
  }
  if (typeof value === "string" && value.length > 0) {
    const n = parseInt(value, 10);
    if (Number.isInteger(n) && n >= MIN_PORT && n <= MAX_PORT) {
      // Reject things like parseInt("12abc") === 12 — require the whole string
      // to be the number.
      if (String(n) === value.trim()) return n;
    }
  }
  return undefined;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return (
    typeof value === "object" &&
    value !== null &&
    !Array.isArray(value)
  );
}
