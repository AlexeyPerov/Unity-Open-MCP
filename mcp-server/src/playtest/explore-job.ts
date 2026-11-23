// Glue between the router and the playtest crawler / detectors:
//   - parseExploreOptions: the tool's arguments → engine options (defaults, deny-list);
//   - exploreOperation: the `jobs` operation that runs one crawl for one owner and
//     closes it with a session mark and an `explore` block for the report;
//   - runCheck: playtest_session {action: "check"} — the current screen's findings,
//     recorded as one call of kind `check` plus a `check` block.
// Tool calls go through the ToolCaller the router builds (normal dispatch as the
// session owner), never straight to the bridge.
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { JobOperation } from "../jobs/job-manager.js";
import { toolOutput, toolResultBody } from "../jobs/adapters.js";
import { PLAYTEST_EXPLORE_TOOL } from "../tools/playtest-explore.js";
import {
  DEFAULT_MIN_TOUCH_DP, DEFAULT_PLACEHOLDER_PATTERN, compilePolicy, findingsFromSnapshot, minTouchPx, sortFindings,
  truncatedDiagnostics,
} from "./detectors.js";
import { EXPLORE_DEFAULTS, EXPLORE_LIMITS, ExploreError, Explorer, type ExploreOptions, type ExploreRecorder, type RestartMode, type ToolCaller } from "./explore.js";
import { PlaytestError, type PlaytestRecorder } from "./recorder.js";
import type { SessionOwner } from "./session-store.js";

const num = (value: unknown, fallback: number) => (typeof value === "number" && Number.isFinite(value) ? value : fallback);

/** The tool's arguments as engine options; invalid combinations throw invalid_arguments. */
export function parseExploreOptions(args: Record<string, unknown>): ExploreOptions {
  const restart = (typeof args.restart === "string" ? args.restart : EXPLORE_DEFAULTS.restart) as RestartMode;
  const restartCommand = typeof args.restart_command === "string" ? args.restart_command : undefined;
  if (restart === "command" && !restartCommand)
    throw new ExploreError("invalid_arguments", "restart: \"command\" needs restart_command (a synchronous project command such as project.<owner>.playtest.boot).");
  if (restart !== "command" && (restartCommand || args.restart_args !== undefined))
    throw new ExploreError("invalid_arguments", "restart_command / restart_args are used only with restart: \"command\".");
  let policy;
  try { policy = compilePolicy(args.deny, args.allow, args.default_deny !== false); }
  catch (error) { throw new ExploreError("invalid_arguments", error instanceof Error ? error.message : String(error)); }
  const settleMs = num(args.settle_ms, EXPLORE_DEFAULTS.settle_ms);
  const minTouch = num(args.min_touch_dp, EXPLORE_DEFAULTS.min_touch_dp);
  return {
    maxSteps: Math.min(num(args.max_steps, EXPLORE_DEFAULTS.max_steps), EXPLORE_LIMITS.max_steps),
    maxMinutes: Math.min(num(args.max_minutes, EXPLORE_DEFAULTS.max_minutes), EXPLORE_LIMITS.max_minutes),
    maxScreens: Math.min(num(args.max_screens, EXPLORE_DEFAULTS.max_screens), EXPLORE_LIMITS.max_screens),
    sameScreen: num(args.same_screen, EXPLORE_DEFAULTS.same_screen),
    settleMs,
    maxSettleMs: Math.max(num(args.max_settle_ms, EXPLORE_DEFAULTS.max_settle_ms), 0),
    restart,
    ...(restartCommand ? { restartCommand } : {}),
    ...(args.restart_args && typeof args.restart_args === "object" ? { restartArgs: args.restart_args as Record<string, unknown> } : {}),
    maxRestarts: Math.min(num(args.max_restarts, EXPLORE_DEFAULTS.max_restarts), EXPLORE_LIMITS.max_restarts),
    policy,
    routing: args.routing === "raycast_real" ? "raycast_real" : "dispatch",
    placeholderPattern: typeof args.placeholder_pattern === "string" ? (args.placeholder_pattern || null) : DEFAULT_PLACEHOLDER_PATTERN,
    minTouchDp: minTouch > 0 ? minTouch : null,
    capture: args.capture !== false,
  };
}

/** A synthetic tool result the recorder reads like a real one: the step summary, logs, frame path. */
function stepResult(output: Record<string, unknown>, logs: unknown[], isError: boolean): CallToolResult {
  return { content: [{ type: "text", text: JSON.stringify({ ...output, logs }) }], isError };
}

/** The recorder seen by the engine: each step is a call of kind `explore` in the owner's session. */
function exploreRecorder(recorder: PlaytestRecorder, owner: SessionOwner): ExploreRecorder {
  return {
    begin(label, args) {
      const handle = recorder.beginOwn(owner, "explore", PLAYTEST_EXPLORE_TOOL, { step: label, ...args });
      if (!handle) {
        // Over the session's call limit the crawl goes on unrecorded; a closed session stops it.
        if (!recorder.isRecording(owner)) return null;
        return { seq: -1, finish() {} };
      }
      return { seq: handle.seq, finish: (output, logs, failed) => recorder.finish(handle, {}, stepResult(output, logs, failed === true)) };
    },
  };
}

function verdictOf(counts: { bug: number; suspicious: number }): "ok" | "bug" | "suspicious" {
  return counts.bug > 0 ? "bug" : counts.suspicious > 0 ? "suspicious" : "ok";
}

export interface ExploreJobInput {
  options: ExploreOptions;
  call: ToolCaller;
  recorder: PlaytestRecorder;
  owner: SessionOwner;
  session: string;
}

/** One crawl as a `jobs` operation (registered per call: it closes over the owner's route). */
export function exploreOperation(input: ExploreJobInput): JobOperation {
  return {
    mutating: false,
    cancellable: true,
    validate() { /* parsed before the job starts */ },
    async run(_args, context) {
      const explorer = new Explorer(input.options, {
        call: input.call,
        recorder: exploreRecorder(input.recorder, input.owner),
        signal: context.signal,
        progress: (phase, fraction, detail) => context.report({ phase, fraction, detail }),
      });
      let result: Record<string, unknown>;
      try {
        result = await explorer.run();
      } catch (error) {
        if (error instanceof ExploreError) return { state: "failed", error: { code: error.code, message: error.message } };
        return { state: "failed", error: { code: "explore_failed", message: error instanceof Error ? error.message : String(error) } };
      }
      const counts = result.finding_counts as { bug: number; suspicious: number };
      const coverage = result.coverage as Record<string, number>;
      const recorded = input.recorder.note(input.owner, { k: "explore", ...result });
      if (recorded) {
        try {
          input.recorder.mark(input.owner, {
            label: `Explore: ${result.steps} taps, ${coverage.screens} screens (${result.stop_reason})`,
            verdict: verdictOf(counts),
            note: counts.bug + counts.suspicious > 0
              ? `${counts.bug} bug / ${counts.suspicious} suspicious finding(s) — listed under Findings and in the Exploration section with each screen's frame.`
              : "No findings.",
          });
        } catch { /* the mark limit: the explore block still carries everything */ }
      }
      const final = {
        ...result,
        session: input.session,
        agentNextSteps: [
          counts.bug + counts.suspicious > 0
            ? `Triage the ${counts.bug} bug / ${counts.suspicious} suspicious finding(s): open the screens' frames (screens[].frame) and confirm each one before you report it.`
            : "No findings: say which screens were covered (coverage, screens[].title) and what was not tried (denied, untried).",
          ...(Array.isArray(result.untried) && result.untried.length ? ["Some controls stayed untried (untried[]): explore them by hand or run again with a larger budget."] : []),
          recorded
            ? "End the session with playtest_session {\"action\":\"end\"}: the report's Exploration section lists the screens with their frames and every finding."
            : "The playtest session ended during the crawl, so its report does not include the crawl's outcome; this result is the record.",
        ],
      };
      if (context.signal.aborted && result.stop_reason === "cancelled") return { state: "cancelled", result: final };
      return { state: "succeeded", result: final };
    },
  };
}

/** playtest_session {action: "check"}: the current screen's detector findings, recorded with a frame. */
export async function runCheck(recorder: PlaytestRecorder, owner: SessionOwner, args: Record<string, unknown>, call: ToolCaller): Promise<Record<string, unknown>> {
  if (!recorder.isRecording(owner))
    throw new PlaytestError("no_open_session", "This agent has no open playtest session; start one first.");
  const label = typeof args.label === "string" && args.label.trim() ? args.label.trim().slice(0, 120) : "Check the current screen";
  const pattern = typeof args.placeholder_pattern === "string" ? (args.placeholder_pattern || null) : DEFAULT_PLACEHOLDER_PATTERN;
  const dp = num(args.min_touch_dp, DEFAULT_MIN_TOUCH_DP);
  const handle = recorder.beginOwn(owner, "check", "unity_open_mcp_playtest_session check", { label });
  const logs: unknown[] = [];
  const keep = (result: CallToolResult) => {
    const body = toolResultBody(result);
    if (Array.isArray(body.logs)) logs.push(...body.logs);
    return toolOutput(result);
  };
  const finish = (output: Record<string, unknown>, failed: boolean) => { if (handle) recorder.finish(handle, {}, stepResult(output, logs, failed)); };
  const probe = await call("unity_open_mcp_ui_snapshot", { profile: "compact", page_size: 1, visible_only: true });
  const probeOut = keep(probe);
  if (probe.isError || probeOut.error) {
    finish({ step: { label, outcome: "snapshot_failed", error: probeOut.error ?? null } }, true);
    throw new PlaytestError(String((probeOut.error as { code?: string } | undefined)?.code ?? "snapshot_failed"),
      `ui_snapshot failed: ${String((probeOut.error as { message?: string } | undefined)?.message ?? "no detail")}`);
  }
  const px = minTouchPx(dp > 0 ? dp : null, probeOut.screen?.dpi);
  const snapshot = await call("unity_open_mcp_ui_snapshot", {
    profile: "compact", page_size: 1, visible_only: true,
    ...(pattern ? { placeholder_pattern: pattern } : {}), ...(px !== null ? { min_touch_size: px } : {}),
  });
  const out = keep(snapshot);
  if (snapshot.isError || out.error) {
    finish({ step: { label, outcome: "snapshot_failed", error: out.error ?? null } }, true);
    throw new PlaytestError(String((out.error as { code?: string } | undefined)?.code ?? "snapshot_failed"),
      `ui_snapshot failed: ${String((out.error as { message?: string } | undefined)?.message ?? "no detail")}`);
  }
  const findings = sortFindings(findingsFromSnapshot(out));
  let frame: string | undefined;
  if (args.capture !== false) {
    const shot = await call("unity_senses_screenshot", { view: out.mode === "play" ? "composed" : "game", max_edge: 960 });
    const shotOut = keep(shot);
    if (!shot.isError && !shotOut.error && typeof shotOut.filePath === "string") frame = shotOut.filePath;
  }
  const counts = { bug: findings.filter(f => f.severity === "bug").length, suspicious: findings.filter(f => f.severity === "suspicious").length };
  const truncated = truncatedDiagnostics(out);
  const notes: string[] = [];
  if (dp > 0 && px === null) notes.push("The touch-size check was skipped: the game reports no screen dpi.");
  if (Object.keys(truncated).length) notes.push(`Diagnostics listed only their first 20 items (${Object.entries(truncated).map(([k, v]) => `${k} +${v}`).join(", ")}).`);
  finish({ step: { label, mode: out.mode, findings: findings.slice(0, 20), counts }, ...(frame ? { filePath: frame } : {}) }, false);
  recorder.note(owner, { k: "check", label, mode: out.mode, findings, counts, ...(handle ? { call: handle.seq } : {}), ...(notes.length ? { notes } : {}) });
  return {
    status: "ok", action: "check", label, mode: out.mode, counts, findings,
    ...(frame ? { frame } : {}), ...(notes.length ? { notes } : {}),
    ...(px !== null ? { min_touch_px: px } : {}),
    agentNextSteps: findings.length
      ? ["Confirm each finding on the frame before reporting it; mark the step bug / suspicious with playtest_session {\"action\":\"mark\"} when it is real."]
      : ["No findings on this screen."],
  };
}
