// unity_open_mcp_game_view routing: turn a play-mode set_* into "returns once
// the game reports the new size".
//
// The bridge applies set_resolution / set_device at once. In edit mode the
// change is synchronous and the answer is final. In play mode the game observes
// the new size only on a later frame, so the bridge starts a short settle watch
// on its shared play-mode scheduler (Screen.width/height inside the player loop
// plus the view's render target, two consecutive frames) and answers `settle:
// {state: "pending", operation}`. This module polls that operation to a
// terminal state, then re-reads `get` so the response describes the settled
// view — or answers `not_settled` with what the game last reported.
import type { LiveClient } from "./live-client.js";
import { pollPlaymodeOperation, toolOutput, toolResultBody } from "./jobs/adapters.js";
import { GAME_VIEW_DEFAULT_SETTLE_MS } from "./tools/game-view.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

export const GAME_VIEW_TOOL = "unity_open_mcp_game_view";

export type GameViewRouteOutcome =
  | { kind: "passthrough"; result: CallToolResult }
  | { kind: "body"; body: Record<string, unknown>; isError: boolean };

export interface GameViewSettleOptions {
  pollIntervalMs?: number;
  /** Extra time after the settle timeout before the poll gives up (cancel + grace). */
  slackMs?: number;
  unreachableMs?: number;
}

const SET_ACTIONS = new Set(["set_resolution", "set_device"]);

function notSettled(out: Record<string, any>, message: string, extra: Record<string, unknown>, logs: unknown): GameViewRouteOutcome {
  const { status: _status, settle, ...state } = out;
  return {
    kind: "body",
    isError: true,
    body: {
      error: { code: "not_settled", message, ...extra },
      ...state,
      settled: false,
      settle: { ...(settle && typeof settle === "object" ? settle : {}), ...extra, operation: undefined },
      agentNextSteps: [
        "The change was applied to the Editor; the game has not reported the new size yet. Make sure play mode is " +
          "running (not paused) and the Game view / Simulator tab is visible, then call game_view get and compare screen with expected.",
      ],
      ...(logs === undefined ? {} : { logs }),
    },
  };
}

export async function routeGameView(
  live: LiveClient,
  args: Record<string, unknown>,
  options: GameViewSettleOptions = {},
): Promise<GameViewRouteOutcome> {
  const start = await live.route(GAME_VIEW_TOOL, args);
  const out = toolOutput(start);
  if (start.isError || out.error || !SET_ACTIONS.has(String(args.action))) return { kind: "passthrough", result: start };
  const logs = toolResultBody(start).logs;
  const settle = (out.settle ?? {}) as Record<string, any>;

  switch (settle.state) {
    case "settled":
      return { kind: "passthrough", result: start };
    case "paused":
    case "clamped":
    case "failed":
      return notSettled(out, String(settle.reason ?? settle.error?.message ?? `Settle ${settle.state}.`), { state: settle.state }, logs);
    case "pending":
      break;
    default:
      return { kind: "passthrough", result: start };
  }

  const operationId = settle.operation?.operation_id;
  if (typeof operationId !== "string")
    return notSettled(out, "The bridge started no settle watch.", { state: "unknown" }, logs);
  const timeoutMs = Number(settle.timeout_ms) > 0 ? Number(settle.timeout_ms) : GAME_VIEW_DEFAULT_SETTLE_MS;
  const outcome = await pollPlaymodeOperation(live, operationId, {
    deadlineMs: timeoutMs + (options.slackMs ?? 2_000),
    pollIntervalMs: options.pollIntervalMs ?? 50,
    unreachableMs: options.unreachableMs ?? 10_000,
  });

  switch (outcome.kind) {
    case "lost":
      return notSettled(out, "The bridge lost the settle watch (domain reload or Editor restart).", { state: "lost" }, logs);
    case "unreachable":
      return notSettled(out, `Settle status unavailable: ${outcome.error}`, { state: "unreachable" }, logs);
    case "deadline":
      return notSettled(out, "The game did not report the new size before the settle timeout (paused Editor or no frames).",
        { state: "timeout", observed: outcome.snapshot?.result?.observed ?? null }, logs);
  }

  const snapshot = outcome.snapshot;
  const result = (snapshot.result ?? {}) as Record<string, any>;
  const frames = snapshot.progress?.ticks;
  if (snapshot.state !== "succeeded" || result.settled !== true) {
    return notSettled(out, String(result.reason ?? `Settle watch ended ${snapshot.reason ?? snapshot.state}.`),
      { state: snapshot.state === "succeeded" ? "timeout" : String(snapshot.reason ?? snapshot.state), observed: result.observed ?? null, frames }, logs);
  }

  // Settled: describe the view as it is now.
  const fresh = toolOutput(await live.route(GAME_VIEW_TOOL, { action: "get" }));
  const { status: _s, action: _a, ...freshState } = fresh.error ? {} as Record<string, any> : fresh;
  return {
    kind: "body",
    isError: false,
    body: {
      ...out,
      ...freshState,
      status: "ok",
      action: out.action,
      settled: true,
      settle: { state: "settled", frames, observed: result.observed ?? null },
      ...(logs === undefined ? {} : { logs }),
    },
  };
}
