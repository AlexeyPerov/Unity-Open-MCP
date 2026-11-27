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
import { EXPLORE_DEFAULTS, EXPLORE_LIMITS, ExploreError, Explorer, shownResult, type ExploreOptions, type ExploreRecorder, type RestartMode, type ToolCaller } from "./explore.js";
import { DEVICE_LIMITS, DEVICE_PRESETS, DeviceMatrix, normalizeOrientations, type DeviceMatrixOptions, type DevicePreset } from "./devices.js";
import { PLAYTEST_DEVICES_TOOL } from "../tools/playtest-devices.js";
import { PlaytestError, type PlaytestRecorder } from "./recorder.js";
import type { SessionOwner } from "./session-store.js";
import {
  NO_MEMORY, PROFILE_FILE, detectorArgsWithProfile, knownEntry, memoryOptions, type MemoryOptions, type PlaytestProfile,
} from "./profile.js";

const num = (value: unknown, fallback: number) => (typeof value === "number" && Number.isFinite(value) ? value : fallback);

/** The tool's arguments as engine options; invalid combinations throw invalid_arguments. */
export function parseExploreOptions(args: Record<string, unknown>, memory: MemoryOptions = NO_MEMORY): ExploreOptions {
  const restart = (typeof args.restart === "string" ? args.restart : EXPLORE_DEFAULTS.restart) as RestartMode;
  const restartCommand = typeof args.restart_command === "string" ? args.restart_command : undefined;
  if (restart === "command" && !restartCommand)
    throw new ExploreError("invalid_arguments", "restart: \"command\" needs restart_command (a synchronous project command such as project.<owner>.playtest.boot).");
  if (restart === "none" && (restartCommand || args.restart_args !== undefined || args.restart_wait !== undefined))
    throw new ExploreError("invalid_arguments", "restart_command / restart_args / restart_wait need a restart (play_mode or command), not restart: \"none\".");
  if (!restartCommand && args.restart_args !== undefined)
    throw new ExploreError("invalid_arguments", "restart_args are the arguments of restart_command; pass restart_command too.");
  const restartWait = args.restart_wait;
  if (restartWait !== undefined && !(restartWait && typeof restartWait === "object" && (restartWait as Record<string, unknown>).condition && typeof (restartWait as Record<string, unknown>).condition === "object"))
    throw new ExploreError("invalid_arguments", "restart_wait is playtest_wait arguments with a condition, e.g. {\"condition\":{\"project_command\":{\"id\":\"project.<owner>.playtest.is_ready\",\"field\":\"met\"}},\"timeout_ms\":30000}.");
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
    ...(restartWait ? { restartWait: restartWait as Record<string, unknown> } : {}),
    maxRestarts: Math.min(num(args.max_restarts, EXPLORE_DEFAULTS.max_restarts), EXPLORE_LIMITS.max_restarts),
    policy,
    routing: args.routing === "raycast_real" ? "raycast_real" : "dispatch",
    placeholderPattern: typeof args.placeholder_pattern === "string" ? (args.placeholder_pattern || null) : DEFAULT_PLACEHOLDER_PATTERN,
    minTouchDp: minTouch > 0 ? minTouch : null,
    capture: args.capture !== false,
    fillInputs: args.fill_inputs !== false,
    fillText: typeof args.fill_text === "string" && args.fill_text ? args.fill_text : EXPLORE_DEFAULTS.fill_text,
    ...(memory !== NO_MEMORY ? { memory } : {}),
  };
}

/** A synthetic tool result the recorder reads like a real one: the step summary, logs, frame path. */
function stepResult(output: Record<string, unknown>, logs: unknown[], isError: boolean): CallToolResult {
  return { content: [{ type: "text", text: JSON.stringify({ ...output, logs }) }], isError };
}

/** The recorder seen by the engine: each step is a call of kind `explore` (or `check`) in the owner's session. */
export function exploreRecorder(recorder: PlaytestRecorder, owner: SessionOwner, kind: "explore" | "check" | "replay" = "explore", tool = PLAYTEST_EXPLORE_TOOL): ExploreRecorder {
  return {
    begin(label, args) {
      const handle = recorder.beginOwn(owner, kind, tool, { step: label, ...args });
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

/** What the project profile supplied to a run (shown to the agent and in the report). */
export interface ProfileApplied { file: string; applied: string[] }

export interface ExploreJobInput {
  options: ExploreOptions;
  call: ToolCaller;
  recorder: PlaytestRecorder;
  owner: SessionOwner;
  session: string;
  profile?: ProfileApplied;
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
        if (input.profile) result.profile = input.profile;
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
        ...shownResult(result),
        session: input.session,
        agentNextSteps: [
          counts.bug + counts.suspicious > 0
            ? `Triage the ${counts.bug} bug / ${counts.suspicious} suspicious finding(s): open the screens' frames (screens[].frame) and confirm each one before you report it.`
            : "No findings: say which screens were covered (coverage, screens[].title) and what was not tried (denied, untried).",
          ...(Array.isArray(result.known) && result.known.length
            ? [`${result.known.length} finding(s) matched the project profile's known list (known[]: with the reason) and are not counted.`] : []),
          "Write what this crawl taught into the project memory: playtest_memory {\"action\":\"map\"} (the screen map into Playtests/NOTES.md); a finding that is not a bug → playtest_memory {\"action\":\"add\",\"profile\":{\"known\":[{\"kind\":\"…\",\"path\":\"…\",\"reason\":\"…\"}]}}; a control that must never be tapped → playtest_memory {\"action\":\"add\",\"profile\":{\"explore\":{\"deny\":[\"…\"]}}}.",
          "To re-check these screens after a change: playtest_replay {\"action\":\"export\",\"source\":\"crawl\",\"scenario\":\"<name>\"} (add to_screen: \"S5\" for one path), then playtest_replay {\"scenario\":\"<name>\"}.",
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
export async function runCheck(recorder: PlaytestRecorder, owner: SessionOwner, callArgs: Record<string, unknown>, call: ToolCaller,
  profile: PlaytestProfile | null = null): Promise<Record<string, unknown>> {
  if (!recorder.isRecording(owner))
    throw new PlaytestError("no_open_session", "This agent has no open playtest session; start one first.");
  const { args, applied } = detectorArgsWithProfile(callArgs, profile);
  const memory = memoryOptions(profile);
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
  const split = memory.known ? memory.known.split(findingsFromSnapshot(out)) : { kept: findingsFromSnapshot(out), known: [] };
  const findings = sortFindings(split.kept);
  const known = split.known.map(knownEntry);
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
  const profileInfo = applied.length || known.length ? { file: PROFILE_FILE, applied: [...applied, ...(known.length ? ["known"] : [])] } : null;
  recorder.note(owner, {
    k: "check", label, mode: out.mode, findings, counts, ...(known.length ? { known } : {}), ...(profileInfo ? { profile: profileInfo } : {}),
    ...(handle ? { call: handle.seq } : {}), ...(notes.length ? { notes } : {}),
  });
  return {
    status: "ok", action: "check", label, mode: out.mode, counts, findings, ...(known.length ? { known } : {}), ...(profileInfo ? { profile: profileInfo } : {}),
    ...(frame ? { frame } : {}), ...(notes.length ? { notes } : {}),
    ...(px !== null ? { min_touch_px: px } : {}),
    agentNextSteps: findings.length
      ? ["Confirm each finding on the frame before reporting it; mark the step bug / suspicious with playtest_session {\"action\":\"mark\"} when it is real."]
      : ["No findings on this screen."],
  };
}

// ---------------------------------------------------------------------------
// Device matrix (unity_open_mcp_playtest_devices)
// ---------------------------------------------------------------------------

/** The tool's arguments as matrix options; invalid combinations throw invalid_arguments. */
export function parseDevicesOptions(args: Record<string, unknown>, memory: MemoryOptions = NO_MEMORY): DeviceMatrixOptions {
  const devices = Array.isArray(args.devices) ? args.devices.filter((d): d is string => typeof d === "string" && d.trim() !== "").map(d => d.trim()) : null;
  const preset = (typeof args.preset === "string" ? args.preset : "common") as DevicePreset;
  if (!(DEVICE_PRESETS as readonly string[]).includes(preset))
    throw new ExploreError("invalid_arguments", `Unknown preset '${preset}'. Valid: ${DEVICE_PRESETS.join(", ")}.`);
  if (devices && devices.length && typeof args.preset === "string")
    throw new ExploreError("invalid_arguments", "Pass devices or preset, not both.");
  const screens = args.screens === "explore" ? "explore" : "current";
  if (screens === "current" && args.explore !== undefined)
    throw new ExploreError("invalid_arguments", "explore options are used only with screens: \"explore\".");
  const minTouch = num(args.min_touch_dp, DEFAULT_MIN_TOUCH_DP);
  const placeholderPattern = typeof args.placeholder_pattern === "string" ? (args.placeholder_pattern || null) : DEFAULT_PLACEHOLDER_PATTERN;
  const explore = parseExploreOptions((args.explore && typeof args.explore === "object" ? args.explore : {}) as Record<string, unknown>, memory);
  // The crawl's detectors follow the matrix's own settings unless its options set them.
  const exploreArgs = (args.explore ?? {}) as Record<string, unknown>;
  if (exploreArgs.placeholder_pattern === undefined) explore.placeholderPattern = placeholderPattern;
  if (exploreArgs.min_touch_dp === undefined) explore.minTouchDp = minTouch > 0 ? minTouch : null;
  return {
    devices: devices && devices.length ? devices : null,
    preset,
    maxDevices: Math.min(num(args.max_devices, 8), DEVICE_LIMITS.max_devices),
    orientations: normalizeOrientations(args.orientations),
    screens,
    explore,
    placeholderPattern,
    minTouchDp: minTouch > 0 ? minTouch : null,
    capture: args.capture !== false,
    settleTimeoutMs: Math.min(Math.max(num(args.settle_timeout_ms, 10_000), 1_000), 20_000),
    ...(memory !== NO_MEMORY ? { memory } : {}),
  };
}

export interface DevicesJobInput {
  options: DeviceMatrixOptions;
  call: ToolCaller;
  recorder: PlaytestRecorder;
  owner: SessionOwner;
  session: string;
  profile?: ProfileApplied;
}

/** One device-matrix run as a `jobs` operation (registered per call, like the crawl). */
export function devicesOperation(input: DevicesJobInput): JobOperation {
  return {
    mutating: false,
    cancellable: true,
    validate() { /* parsed before the job starts */ },
    async run(_args, context) {
      const matrix = new DeviceMatrix(input.options, {
        call: input.call,
        check: exploreRecorder(input.recorder, input.owner, "check", PLAYTEST_DEVICES_TOOL),
        explore: exploreRecorder(input.recorder, input.owner),
        onCrawl: (result, device, orientation) => {
          if (input.profile) result.profile = input.profile;
          const findings = (Array.isArray(result.findings) ? result.findings : []).map(f => ({ ...(f as object), device, orientation }));
          const counts = result.finding_counts as { bug: number; suspicious: number };
          if (input.recorder.note(input.owner, { k: "explore", ...result, findings, device, orientation })) {
            try {
              input.recorder.mark(input.owner, {
                label: `Explore on ${device} (${orientation}): ${result.steps} taps, ${(result.coverage as Record<string, number>).screens} screens (${result.stop_reason})`,
                verdict: verdictOf(counts),
                note: counts.bug + counts.suspicious > 0
                  ? `${counts.bug} bug / ${counts.suspicious} suspicious finding(s) on this device — see the Devices and Exploration sections.`
                  : "No findings on this device.",
              });
            } catch { /* mark limit */ }
          }
        },
        signal: context.signal,
        progress: (phase, fraction, detail) => context.report({ phase, fraction, detail }),
      });
      let result: Record<string, unknown>;
      try {
        result = await matrix.run();
        if (input.profile) result.profile = input.profile;
      } catch (error) {
        if (error instanceof ExploreError) return { state: "failed", error: { code: error.code, message: error.message } };
        return { state: "failed", error: { code: "devices_failed", message: error instanceof Error ? error.message : String(error) } };
      }
      const counts = result.finding_counts as { bug: number; suspicious: number };
      const recorded = input.recorder.note(input.owner, { k: "devices", ...result });
      if (recorded && input.options.screens === "current") {
        try {
          input.recorder.mark(input.owner, {
            label: `Devices: ${result.checked} checked (${(input.options.orientations).join(", ")})`,
            verdict: verdictOf(counts),
            note: counts.bug + counts.suspicious > 0
              ? `${counts.bug} bug / ${counts.suspicious} suspicious finding(s) across devices — the Devices section groups them by element.`
              : "No findings on any device.",
          });
        } catch { /* mark limit */ }
      }
      const final = {
        ...result,
        session: input.session,
        agentNextSteps: [
          counts.bug + counts.suspicious > 0
            ? "Triage per element (matrix[]: which devices show each finding): open the device frames (entries[].frame) and confirm before you report; a finding on notch devices only is a safe-area issue."
            : "No findings on these devices: say which devices and orientations were checked (entries[]).",
          ...(Array.isArray(result.skipped) && result.skipped.length ? ["Some device / orientation pairs were skipped (skipped[]: reason) — check them by hand if they matter."] : []),
          (result.restored as { restored?: boolean } | null)?.restored
            ? "The Game view was put back as it was."
            : "The Game view could not be put back (restored): set it with game_view.",
          recorded ? "End the session with playtest_session {\"action\":\"end\"}: the report has a Devices section." : "The playtest session ended during the run; this result is the record.",
        ],
      };
      if (context.signal.aborted && result.stop_reason === "cancelled") return { state: "cancelled", result: final };
      return { state: "succeeded", result: final };
    },
  };
}
