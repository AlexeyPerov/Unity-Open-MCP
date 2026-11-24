// Playtest session report — `end` turns the session log into a self-contained
// folder a human can open: report.md + the full frames (already linked into
// report/frames/ during the session) + downscaled thumbnails in report/thumbs/.
// Steps are the calls between two `mark`s, each with its verdict and note;
// every console error / exception is listed once with its first occurrence
// (step, call, during / after it) and stack; the player-state diff from the
// session's snapshot and the editor events (play mode, domain reloads) close
// it. Calls that were in flight when an earlier MCP server process stopped are
// reported as lost; calls of the ending process that had not returned yet, as
// still running at end.
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { findingKey, type Finding } from "./detectors.js";
import { thumbnail } from "./png.js";
import type { Redactor } from "./redact.js";
import { REPORT_DIR, REPORT_FILE, THUMBS_DIR, type SessionMeta, type SessionStore } from "./session-store.js";

export interface ReportSummary {
  report: string;
  steps: number;
  calls: number;
  failed_calls: number;
  captures: number;
  thumbnails: number;
  duration_ms: number;
  verdicts: { ok: number; bug: number; suspicious: number };
  errors: { unique: number; total: number };
  findings: Array<{ step: number; label: string; verdict: string; note?: string }>;
  top_errors: Array<{ type: string; message: string; first_step: number | null; first_call: number | null; relation: string; count: number }>;
  lost_in_flight: Array<{ seq: number; tool: string; at: string }>;
  running_at_end: Array<{ seq: number; tool: string; at: string }>;
  state_diff: { prefs?: unknown; files?: unknown; editor_prefs?: unknown; identical?: unknown; error?: unknown } | null;
  hooks: { begin?: string; end?: string };
  /** Findings of UI crawls and screen checks (deduplicated), bug first. */
  detector_findings: { bug: number; suspicious: number; items: Array<Record<string, unknown>> };
  explorations: Array<{ stop_reason: string; steps: number; screens: number; bug: number; suspicious: number; device?: string }>;
  device_runs: Array<{ screens: string; checked: number; stop_reason: string; bug: number; suspicious: number; restored: boolean }>;
  /** Scenario replays: verdict and, when it failed, the first failing step. */
  replays: Array<{ scenario: string; verdict: string; steps: number; failed_step?: number; reason?: string }>;
  notes: string[];
}

export interface ReportInput {
  store: SessionStore;
  dir: string;
  meta: SessionMeta;
  entries: Array<Record<string, any>>;
  stateDiff: { ok: boolean; output?: any; error?: any } | null;
  redactor: Redactor;
  maxThumbnails: number;
  now: () => number;
  /** Wall-clock budget for thumbnail decoding (ms). */
  thumbnailBudgetMs?: number;
}

interface CallRow {
  seq: number;
  tool: string;
  kind: string;
  at: string;
  duration_ms: number;
  status: string;
  args?: unknown;
  error?: { code?: string; message?: string };
  summary?: Record<string, unknown>;
  captures: Array<Record<string, any>>;
  errors: Array<Record<string, any>>;
  errorsAfter: Array<Record<string, any>>;
  job_id?: string;
  of?: number;
  lost?: boolean;
  running?: boolean;
  streamDown?: boolean;
}

interface Step {
  number: number | null;
  label: string;
  verdict: string | null;
  note?: string;
  at?: string;
  calls: CallRow[];
}

const cell = (value: string) => value.replace(/\|/g, "\\|").replace(/\r?\n/g, " ");
const code = (value: string) => "`" + value.replace(/`/g, "'") + "`";
// A code span inside a table cell: a `|` splits the row even there, so it is escaped too.
const cellCode = (value: string) => cell(code(value));
const clip = (value: string, max: number) => (value.length > max ? `${value.slice(0, max)}…` : value);
const firstLine = (value: string) => value.split("\n")[0].trim();
const shortTool = (tool: string) => tool.replace(/^unity_open_mcp_/, "").replace(/^unity_senses_/, "senses_");
const time = (isoText: string | undefined) => (isoText ? isoText.replace(/^.*T/, "").replace("Z", "") : "");
// One row per message: the event stream says Exception / Assert where a response's logs[] says error.
const errorKey = (e: Record<string, any>) => firstLine(String(e.message ?? "")).slice(0, 200);
const SPECIFIC_TYPES = new Set(["exception", "assert"]);
const sameMessage = (a: Record<string, any>, b: Record<string, any>) => firstLine(String(a.message ?? "")).slice(0, 200) === firstLine(String(b.message ?? "")).slice(0, 200);

function duration(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`;
  const m = Math.floor(ms / 60_000), s = Math.round((ms % 60_000) / 1000);
  return `${m} min ${s} s`;
}

/** Merge an attributed stream error into a call, replacing a stack-less response copy of the same message. */
function mergeError(list: Array<Record<string, any>>, item: Record<string, any>): void {
  const index = list.findIndex(e => sameMessage(e, item));
  if (index < 0) { list.push(item); return; }
  if (!list[index].stack && item.stack) list[index] = { ...list[index], ...item };
}

export function writeReport(input: ReportInput): ReportSummary {
  const { dir, meta, entries, redactor } = input;
  const reportDir = join(dir, REPORT_DIR);
  mkdirSync(join(reportDir, THUMBS_DIR), { recursive: true });

  // ---- model -------------------------------------------------------------
  const calls = new Map<number, CallRow>();
  const started = new Map<number, Record<string, any>>();
  const orderedEvents: Array<{ kind: "call" | "mark" | "start"; entry: Record<string, any> }> = [];
  const editorEvents: Array<Record<string, any>> = [];
  const notes: string[] = [];
  const looseErrors: Array<Record<string, any>> = [];
  let endNote: string | undefined;
  let running = new Set<number>();
  let errorRepeats: Array<{ type?: string; message?: string; count?: number }> = [];
  let errorsOverLimit = 0, streamGaps = 0, firstGap: string | undefined;
  const explores: Array<Record<string, any>> = [];
  const checks: Array<Record<string, any>> = [];
  const deviceRuns: Array<Record<string, any>> = [];
  const replays: Array<Record<string, any>> = [];
  for (const entry of entries) {
    switch (entry.k) {
      case "replay":
        replays.push(entry);
        break;
      case "devices":
        deviceRuns.push(entry);
        break;
      case "explore":
        explores.push(entry);
        break;
      case "check":
        checks.push(entry);
        break;
      case "call_started":
        started.set(entry.seq, entry);
        orderedEvents.push({ kind: "start", entry });
        break;
      case "call":
        calls.set(entry.seq, {
          seq: entry.seq, tool: entry.tool, kind: entry.kind, at: entry.at, duration_ms: entry.duration_ms ?? 0,
          status: entry.status, args: entry.args ?? started.get(entry.seq)?.args, error: entry.error, summary: entry.summary,
          captures: Array.isArray(entry.captures) ? entry.captures : [], errors: Array.isArray(entry.errors) ? [...entry.errors] : [],
          errorsAfter: [], job_id: entry.job_id, of: entry.of, streamDown: entry.stream === false,
        });
        if (!started.has(entry.seq)) orderedEvents.push({ kind: "call", entry });
        break;
      case "mark":
        orderedEvents.push({ kind: "mark", entry });
        break;
      case "errors":
        for (const item of Array.isArray(entry.items) ? entry.items : []) {
          const target = typeof item.seq === "number" ? calls.get(item.seq) : undefined;
          if (target && item.relation === "during") mergeError(target.errors, item);
          else if (target) target.errorsAfter.push(item);
          else looseErrors.push(item);
        }
        break;
      case "editor":
        editorEvents.push(entry);
        break;
      case "stream_gap":
        if (streamGaps++ === 0) firstGap = `${entry.at} (${entry.detail})`;
        break;
      case "limit":
        notes.push(String(entry.message));
        break;
      case "adopted":
        notes.push(`At ${entry.at} a new MCP server process took the session over from pid ${entry.from_pid}.`);
        break;
      case "end_note":
        endNote = entry.note;
        break;
      case "session_ended":
        if (entry.dropped_calls) notes.push(`${entry.dropped_calls} call(s) over the recording limit were not logged.`);
        if (entry.frames_skipped) notes.push(`${entry.frames_skipped} frame(s) over the frame limit were referenced, not copied.`);
        running = new Set(Array.isArray(entry.running) ? entry.running : []);
        errorRepeats = Array.isArray(entry.error_repeats) ? entry.error_repeats : [];
        errorsOverLimit = typeof entry.errors_over_limit === "number" ? entry.errors_over_limit : 0;
        if (entry.errors_dropped) notes.push(`${entry.errors_dropped} console error(s) arrived while the backlog of one call was full and were dropped (not counted).`);
        break;
    }
  }

  if (streamGaps) notes.push(`The bridge event stream reported missed events ${streamGaps} time(s), first at ${firstGap}; errors from those moments may be missing.`);

  // Steps: the calls before each mark belong to it; a call still "started" without
  // a result was lost (an earlier process stopped) or still running at end.
  const steps: Step[] = [];
  let current: CallRow[] = [];
  const lost: Array<{ seq: number; tool: string; at: string }> = [];
  const runningAtEnd: Array<{ seq: number; tool: string; at: string }> = [];
  for (const { kind, entry } of orderedEvents) {
    if (kind === "mark") {
      steps.push({ number: entry.step, label: entry.label, verdict: entry.verdict, note: entry.note, at: entry.at, calls: current });
      current = [];
      continue;
    }
    const row = calls.get(entry.seq);
    if (row) { current.push(row); continue; }
    if (kind === "start") {
      const still = running.has(entry.seq);
      (still ? runningAtEnd : lost).push({ seq: entry.seq, tool: entry.tool, at: entry.at });
      current.push({ seq: entry.seq, tool: entry.tool, kind: entry.kind, at: entry.at, duration_ms: 0, status: still ? "running" : "lost", args: entry.args,
        captures: [], errors: [], errorsAfter: [], ...(still ? { running: true } : { lost: true }) });
    }
  }
  if (current.length) steps.push({ number: null, label: "After the last mark", verdict: null, calls: current });

  // Error catalog: one row per message, first occurrence wins.
  interface ErrorRow { id: number; type: string; message: string; stack?: string; first_step: number | null; first_call: number | null; first_tool?: string; relation: string; at?: string; count: number }
  const catalog = new Map<string, ErrorRow>();
  let totalErrors = 0;
  const record = (e: Record<string, any>, step: Step | null, call: CallRow | null, relation: string) => {
    totalErrors++;
    const key = errorKey(e);
    const row = catalog.get(key);
    if (row) {
      row.count++;
      if (!row.stack && e.stack) row.stack = e.stack;
      if (SPECIFIC_TYPES.has(String(e.type).toLowerCase()) && !SPECIFIC_TYPES.has(row.type.toLowerCase())) row.type = String(e.type);
      return;
    }
    catalog.set(key, {
      id: catalog.size + 1, type: String(e.type ?? "error"), message: String(e.message ?? ""), stack: e.stack,
      first_step: step?.number ?? null, first_call: call?.seq ?? null, first_tool: call?.tool, relation, at: e.at, count: 1,
    });
  };
  const stepOfCall = new Map<number, Step>();
  for (const step of steps) for (const call of step.calls) stepOfCall.set(call.seq, step);
  for (const e of looseErrors) record(e, null, null, "before the first call");
  // Walk in time order so "first occurrence" is chronological.
  const timeline: Array<{ e: Record<string, any>; step: Step; call: CallRow; relation: string; at: number }> = [];
  for (const step of steps) for (const call of step.calls) {
    // The same message from the response and the stream inside one call is one occurrence.
    call.errors = call.errors.filter((e, i) => call.errors.findIndex(other => sameMessage(other, e)) === i);
    for (const e of call.errors) timeline.push({ e, step, call, relation: "during", at: Date.parse(e.at ?? call.at) || 0 });
    for (const e of call.errorsAfter) timeline.push({ e, step, call, relation: "after", at: Date.parse(e.at ?? call.at) || 0 });
  }
  timeline.sort((a, b) => a.at - b.at);
  for (const t of timeline) record(t.e, t.step, t.call, t.relation);
  // Occurrences the recorder counted but did not log (a message past its repeat cap, errors past the session cap).
  let suppressed = 0;
  for (const repeat of errorRepeats) {
    const count = typeof repeat.count === "number" ? repeat.count : 0;
    const row = catalog.get(errorKey(repeat));
    if (row) row.count += count;
    suppressed += count;
  }
  totalErrors += suppressed + errorsOverLimit;
  if (suppressed) notes.push(`${suppressed} repeat(s) of ${errorRepeats.length} message(s) over the per-message log cap were counted, not logged (they are in the ×counts).`);
  if (errorsOverLimit) notes.push(`${errorsOverLimit} console error(s) over the per-call / per-session error limit were counted, not logged.`);
  const errorRows = [...catalog.values()].sort((a, b) => a.id - b.id);

  // ---- thumbnails --------------------------------------------------------
  const budgetEnd = input.now() + (input.thumbnailBudgetMs ?? 20_000);
  let thumbs = 0, captureCount = 0, thumbSkipped = 0;
  const thumbFor = (capture: Record<string, any>): string | null => {
    if (!capture.file) return null;
    if (thumbs >= input.maxThumbnails || input.now() > budgetEnd) { thumbSkipped++; return null; }
    const source = join(reportDir, capture.file);
    const name = capture.file.replace(/^frames\//, "").replace(/\.png$/i, "") + ".thumb.png";
    const target = join(reportDir, THUMBS_DIR, name);
    try {
      if (!existsSync(target)) {
        const contact = capture.label === "contact sheet";
        const thumb = thumbnail(readFileSync(source), contact ? 960 : 320, contact ? 480 : 320);
        if (!thumb) return null;
        writeFileSync(target, thumb.png);
      }
      thumbs++;
      return `${THUMBS_DIR}/${name}`;
    } catch { return null; }
  };

  // ---- detector findings (crawls and checks) -----------------------------
  const detector: Array<Record<string, any> & { source: string }> = [];
  const seenFinding = new Set<string>();
  const addFinding = (f: Record<string, any>, source: string) => {
    // A check is its own observation; crawl runs share one list.
    const key = `${findingKey(f as Finding)}|${source.startsWith("check") ? source : ""}`;
    if (seenFinding.has(key)) return;
    seenFinding.add(key);
    detector.push({ ...f, source });
  };
  explores.forEach((run, i) => { for (const f of Array.isArray(run.findings) ? run.findings : []) addFinding(f, `explore run ${i + 1}`); });
  checks.forEach((check, i) => { for (const f of Array.isArray(check.findings) ? check.findings : []) addFinding(f, `check ${i + 1}`); });
  // A crawl per device files its findings through its explore block; a current-screen run through its entries.
  deviceRuns.forEach((run, i) => {
    if (run.screens !== "current") return;
    for (const entry of Array.isArray(run.entries) ? run.entries : [])
      for (const f of Array.isArray(entry.findings) ? entry.findings : []) addFinding(f, `devices run ${i + 1}`);
  });
  detector.sort((a, b) => (a.severity === b.severity ? 0 : a.severity === "bug" ? -1 : 1));
  const detectorCounts = { bug: detector.filter(f => f.severity === "bug").length, suspicious: detector.filter(f => f.severity !== "bug").length };
  const findingWhere = (f: Record<string, any>) => [
    f.device ? `on ${f.device} (${f.orientation ?? ""})` : null,
    f.screen ? String(f.screen) : null, f.label ? `'${clip(String(f.label), 60)}'` : null, f.path ? code(String(f.path)) : null,
  ].filter(Boolean).join(" ");
  const frameOfCall = (seq: unknown): Record<string, any> | null => {
    if (typeof seq !== "number") return null;
    return calls.get(seq)?.captures.find(c => c.file) ?? null;
  };

  // ---- markdown ----------------------------------------------------------
  const verdicts = { ok: 0, bug: 0, suspicious: 0 };
  for (const step of steps) if (step.verdict && step.verdict in verdicts) verdicts[step.verdict as keyof typeof verdicts]++;
  const allCalls = [...calls.values()];
  const failedCalls = allCalls.filter(c => c.status === "error").length;
  for (const c of allCalls) captureCount += c.captures.filter(x => x.file).length;
  const startedMs = Date.parse(meta.started_at), endedMs = Date.parse(meta.ended_at ?? new Date(input.now()).toISOString());
  const device = meta.device ?? {};
  const view = (device.view ?? {}) as Record<string, any>;
  const deviceText = [
    device.summary ? String(device.summary) : null,
    view.mode ? `${view.mode === "simulator" ? `Device Simulator ${view.device ?? ""}`.trim() : `Game view ${view.size ?? ""}`.trim()}` : null,
    Array.isArray(view.screen) ? `${view.screen[0]}×${view.screen[1]}${view.orientation ? ` ${view.orientation}` : ""}` : null,
  ].filter(Boolean).join(" · ") || "not recorded";
  // A hook ran: an invoke that succeeded, or a job (`jobs start` row) whose observed result did not fail.
  const hooks: { begin?: string; end?: string } = {};
  const failedJobs = new Set(allCalls.filter(c => c.of !== undefined && c.status === "error").map(c => c.of));
  for (const c of allCalls) {
    const args = c.args as any;
    const id = typeof args?.command_id === "string" ? args.command_id as string
      : typeof args?.tool_or_command === "string" ? args.tool_or_command as string : "";
    const ran = c.status === "ok" || (c.status === "job_started" && !failedJobs.has(c.seq));
    if (/(^|\.)playtest\.begin$/.test(id) && ran) hooks.begin = id;
    if (/(^|\.)playtest\.end$/.test(id) && ran) hooks.end = id;
  }

  const md: string[] = [];
  md.push(`# Playtest report: ${cell(meta.name)}`, "");
  md.push("| | |", "|---|---|");
  md.push(`| Session | ${cellCode(meta.id)} |`);
  md.push(`| Project | ${cell(meta.company)} / ${cell(meta.product)} (${cellCode(meta.project)}) |`);
  md.push(`| Started | ${meta.started_at} |`);
  md.push(`| Ended | ${meta.ended_at ?? "—"} (${Number.isFinite(endedMs - startedMs) ? duration(endedMs - startedMs) : "?"}) |`);
  md.push(`| Device | ${cell(deviceText)} |`);
  md.push(`| Player-state snapshot | ${meta.snapshot ? `${cellCode(meta.snapshot.name)} (${meta.snapshot.taken ? "taken at start" : "existing"})` : "none"} |`);
  if (hooks.begin || hooks.end) md.push(`| Project isolation hook | ${[hooks.begin, hooks.end].filter(Boolean).map(h => cellCode(h!)).join(" → ")}${hooks.begin && !hooks.end ? " (end hook not recorded)" : ""} |`);
  md.push(`| Recorded | ${allCalls.length} call(s) in ${steps.length} step(s), ${failedCalls} failed, ${captureCount} frame(s), ${errorRows.length} distinct error(s) (${totalErrors} total) |`);
  md.push(`| Verdicts | ok ${verdicts.ok} · bug ${verdicts.bug} · suspicious ${verdicts.suspicious} |`);
  md.push("");

  md.push("## Findings", "");
  const findings = steps.filter(s => s.verdict === "bug" || s.verdict === "suspicious");
  const badReplays = replays.filter(r => r.verdict !== "passed" && r.verdict !== "recorded");
  if (!findings.length && !errorRows.length && !lost.length && !runningAtEnd.length && !failedCalls && !detector.length && !badReplays.length) md.push("No bug or suspicious verdicts, no detector findings, no failed replays, no console errors, no failed calls.");
  for (const step of findings) md.push(`- **${step.verdict}** — step ${step.number}: ${cell(step.label)}${step.note ? ` — ${cell(step.note)}` : ""}`);
  for (const f of detector.slice(0, 40))
    md.push(`- **${f.severity}** ${code(String(f.kind))} — ${findingWhere(f) || "the screen"}: ${cell(clip(String(f.detail ?? ""), 200))} (${f.source}${typeof f.step === "number" ? `, step ${f.step}` : ""})`);
  if (detector.length > 40) md.push(`- … ${detector.length - 40} more detector finding(s) in [Exploration](#exploration) / [Screen checks](#screen-checks).`);
  for (const row of errorRows) md.push(`- **${row.type}** ${code(clip(firstLine(row.message), 160))} — first in ${row.first_step !== null ? `step ${row.first_step}` : row.first_call !== null ? "the unmarked calls" : "the session before any call"}${row.first_call !== null ? `, call #${row.first_call} ${code(shortTool(row.first_tool ?? ""))} (${row.relation} it)` : ""}${row.count > 1 ? `, ×${row.count}` : ""} → [E${row.id}](#e${row.id})`);
  if (failedCalls) md.push(`- ${failedCalls} recorded call(s) failed (status \`error\` in the steps below).`);
  for (const l of lost) md.push(`- **Lost in flight:** call #${l.seq} ${code(shortTool(l.tool))} started at ${l.at} and never finished — the MCP server process stopped during it.`);
  for (const l of runningAtEnd) md.push(`- **Still running at end:** call #${l.seq} ${code(shortTool(l.tool))} started at ${l.at} and had not returned when the session ended.`);
  if (endNote) md.push("", `> ${cell(endNote)}`);
  md.push("");

  if (explores.length) {
    md.push("## Exploration", "");
    explores.forEach((run, i) => {
      const coverage = (run.coverage ?? {}) as Record<string, number>;
      const counts = (run.finding_counts ?? {}) as Record<string, number>;
      md.push(`### Run ${i + 1}${run.device ? ` on ${cell(String(run.device))} (${cell(String(run.orientation ?? ""))})` : ""} — ${cell(String(run.stop_reason ?? "?"))}`, "");
      md.push("| | |", "|---|---|");
      md.push(`| Taps | ${run.steps ?? 0} in ${duration(Number(run.duration_ms ?? 0))}${run.restarts ? `, ${run.restarts} restart(s)` : ""} |`);
      md.push(`| Screens | ${coverage.screens ?? 0} |`);
      md.push(`| Controls | ${coverage.actions_tried ?? 0} of ${coverage.actions_total ?? 0} tried${coverage.untried ? `, ${coverage.untried} untried` : ""}${Array.isArray(run.denied) && run.denied.length ? `, ${run.denied.length} denied` : ""} |`);
      md.push(`| Findings | bug ${counts.bug ?? 0} · suspicious ${counts.suspicious ?? 0} |`);
      md.push("");
      const screens = Array.isArray(run.screens) ? run.screens : [];
      if (screens.length) {
        md.push("| Screen | Title | Frame | Controls tried | Denied | Notes |", "|---|---|---|---|---|---|");
        for (const screen of screens) {
          const capture = frameOfCall(screen.frame_call);
          const thumb = capture ? thumbFor(capture) : null;
          const frame = capture ? (thumb ? `[![${screen.id}](${thumb})](${capture.file})` : `[frame](${capture.file})`) : "—";
          const labels = Array.isArray(screen.labels) && screen.labels.length ? ` (${screen.labels.slice(0, 5).map((l: string) => `'${clip(l, 24)}'`).join(", ")})` : "";
          md.push(`| ${screen.id} | ${cell(clip(String(screen.title ?? ""), 60))}${cell(labels)} | ${frame} | ${screen.tried ?? 0} / ${screen.actions ?? 0} | ${screen.denied ?? 0} | ${screen.dead_end ? "dead end" : ""}${screen.visits > 1 ? `${screen.dead_end ? ", " : ""}${screen.visits} visits` : ""} |`);
        }
        md.push("");
      }
      const runFindings = Array.isArray(run.findings) ? run.findings : [];
      if (runFindings.length) {
        md.push("| Severity | Kind | Screen | Element | Detail | Step |", "|---|---|---|---|---|---|");
        for (const f of runFindings)
          md.push(`| ${f.severity} | ${cellCode(String(f.kind))} | ${f.screen ?? ""} | ${cell([f.label ? `'${clip(String(f.label), 60)}'` : "", f.path ? code(String(f.path)) : ""].filter(Boolean).join(" "))} | ${cell(clip(String(f.detail ?? ""), 200))} | ${f.step ?? ""} |`);
        if (run.findings_truncated) md.push(`| … | ${run.findings_truncated} more | | | | |`);
        md.push("");
      }
      const edges = Array.isArray(run.edges) ? run.edges : [];
      if (edges.length) {
        md.push("<details><summary>Exploring taps (" + edges.length + "; navigation and restarts are in the steps below)</summary>", "", "| Step | From | Tapped | To | Outcome |", "|---|---|---|---|---|");
        for (const e of edges) md.push(`| ${e.step} | ${e.from} | ${cell(clip(String(e.action), 60))} | ${e.to} | ${e.outcome} |`);
        md.push("", "</details>", "");
      }
      if (Array.isArray(run.denied) && run.denied.length) {
        md.push("Never tapped (deny-list):", "");
        for (const d of run.denied) md.push(`- ${d.screen} '${cell(clip(String(d.label), 60))}' ${code(String(d.path))} — ${cell(String(d.reason))}`);
        md.push("");
      }
      if (Array.isArray(run.untried) && run.untried.length) {
        md.push("Not tried (budget or no way back):", "");
        for (const u of run.untried) md.push(`- ${u.screen} '${cell(clip(String(u.label), 60))}' ${code(String(u.path))}`);
        md.push("");
      }
      if (Array.isArray(run.navigation_failures) && run.navigation_failures.length) {
        md.push("Navigation that did not reach the expected screen:", "");
        for (const n of run.navigation_failures) md.push(`- step ${n.step}: '${cell(clip(String(n.action), 60))}' on ${n.from} reached ${n.reached}, expected ${n.expected}`);
        md.push("");
      }
      for (const note of Array.isArray(run.notes) ? run.notes : []) notes.push(`Exploration run ${i + 1}: ${String(note)}`);
    });
  }

  if (deviceRuns.length) {
    md.push("## Devices", "");
    deviceRuns.forEach((run, i) => {
      const counts = (run.finding_counts ?? {}) as Record<string, number>;
      const restored = (run.restored ?? {}) as Record<string, any>;
      md.push(`### Devices run ${i + 1} — ${run.screens === "explore" ? "a crawl per device" : "the current screen"} — ${cell(String(run.stop_reason ?? "?"))}`, "");
      md.push(`${run.checked ?? 0} device check(s) in ${duration(Number(run.duration_ms ?? 0))}; bug ${counts.bug ?? 0} · suspicious ${counts.suspicious ?? 0}; ` +
        `the Game view ${restored.restored ? "was put back" : `was not put back${restored.error ? ` (${cell(clip(JSON.stringify(restored.error), 120))})` : ""}`}.`, "");
      const rows = Array.isArray(run.entries) ? run.entries : [];
      if (rows.length) {
        const explore = run.screens === "explore";
        md.push(explore ? "| Device | Orientation | Screen | Insets | Taps / screens / controls | Findings |" : "| Device | Orientation | Screen | Insets | Findings | Frame |",
          explore ? "|---|---|---|---|---|---|" : "|---|---|---|---|---|---|");
        for (const e of rows) {
          const insets = e.insets ? ["top", "bottom", "left", "right"].filter(k => e.insets[k]).map(k => `${k} ${e.insets[k]}`).join(", ") || "none" : "?";
          const found = `bug ${e.counts?.bug ?? 0} · suspicious ${e.counts?.suspicious ?? 0}`;
          const screen = Array.isArray(e.screen) ? `${e.screen[0]}×${e.screen[1]}${e.orientation_applied === false ? " (did not rotate)" : ""}` : "?";
          if (explore) md.push(`| ${cell(String(e.device))} | ${e.orientation} | ${screen} | ${insets} | ${e.steps ?? 0} / ${e.screens ?? 0} / ${e.actions ?? ""} (${cell(String(e.stop_reason ?? ""))}) | ${found} |`);
          else {
            const capture = frameOfCall(e.frame_call);
            const thumb = capture ? thumbFor(capture) : null;
            const frame = capture ? (thumb ? `[![${cell(String(e.device))}](${thumb})](${capture.file})` : `[frame](${capture.file})`) : "—";
            md.push(`| ${cell(String(e.device))} | ${e.orientation} | ${screen} | ${insets} | ${found} | ${frame} |`);
          }
        }
        md.push("");
      }
      const matrix = Array.isArray(run.matrix) ? run.matrix : [];
      if (matrix.length) {
        md.push("Findings by element (the devices that show each):", "");
        for (const g of matrix)
          md.push(`- **${g.severity}** ${code(String(g.kind))} — ${[g.label ? `'${clip(String(g.label), 60)}'` : null, g.path ? code(String(g.path)) : null].filter(Boolean).join(" ") || "the screen"}: ${cell(clip(String(g.detail ?? ""), 160))} — on ${g.count} of ${g.of}: ${cell((g.on as string[]).join(", "))}`);
        md.push("");
      }
      if (Array.isArray(run.skipped) && run.skipped.length) {
        md.push("Skipped:", "");
        for (const sk of run.skipped) md.push(`- ${cell(String(sk.device))} (${sk.orientation}) — ${cell(String(sk.reason))}${sk.note ? `: ${cell(clip(String(sk.note), 160))}` : ""}`);
        md.push("");
      }
      for (const note of Array.isArray(run.notes) ? run.notes : []) notes.push(`Devices run ${i + 1}: ${String(note)}`);
    });
  }

  if (replays.length) {
    md.push("## Replays", "");
    replays.forEach((run, i) => {
      const scenario = (run.scenario ?? {}) as Record<string, any>;
      const attempts = Array.isArray(run.attempts) ? run.attempts as Array<Record<string, any>> : [];
      md.push(`### Replay ${i + 1}: ${code(String(scenario.name ?? "?"))} — ${run.verdict}${run.consistent ? " (the same step failed every attempt)" : ""}`, "");
      md.push(`${scenario.file ? `File ${code(String(scenario.file))} · ` : ""}${scenario.steps ?? 0} step(s) · ${duration(Number(run.duration_ms ?? 0))}${run.record ? " · baseline run" : ""}${run.stop_reason && run.stop_reason !== "completed" ? ` · stopped: ${run.stop_reason}` : ""}`, "");
      if (attempts.length > 1 || attempts.some(a => !a.passed))
        md.push(`Attempts: ${attempts.map(a => `${a.attempt} ${a.passed ? "passed" : `failed${a.failed_step !== undefined ? ` at step ${a.failed_step} (${a.reason})` : ""}`}`).join(" · ")}`, "");
      const rows = Array.isArray(run.steps) ? run.steps as Array<Record<string, any>> : [];
      if (rows.length) {
        md.push("| # | Step | Status | Screen | Notes | Frame |", "|---|---|---|---|---|---|");
        for (const st of rows) {
          const capture = frameOfCall(st.frame_call);
          const thumb = capture ? thumbFor(capture) : null;
          const frame = capture ? (thumb ? `[![step ${st.n}](${thumb})](${capture.file})` : `[frame](${capture.file})`) : "";
          const status = st.status === "failed" ? `**failed** ${cellCode(String(st.reason ?? ""))}` : String(st.status);
          const screen = st.similarity !== undefined
            ? `${Math.round(Number(st.similarity) * 100)}% '${cell(clip(String(st.title ?? ""), 40))}'${st.expected_title && st.similarity < 1 ? ` (expected '${cell(clip(String(st.expected_title), 40))}')` : ""}`
            : st.title ? `'${cell(clip(String(st.title), 40))}' (no baseline)` : "";
          const notes = [
            st.status === "failed" && st.message ? cell(clip(String(st.message), 160)) : null,
            st.diff ? `+${(st.diff.added ?? []).slice(0, 3).map((t: string) => cell(clip(t, 50))).join(", ")}${(st.diff.added ?? []).length > 3 ? "…" : ""} / −${(st.diff.removed ?? []).slice(0, 3).map((t: string) => cell(clip(t, 50))).join(", ")}${(st.diff.removed ?? []).length > 3 ? "…" : ""}` : null,
            Array.isArray(st.drift) && st.drift.length ? `drift: ${st.drift.map((d: any) => `${d.kind}`).join(", ")}` : null,
            Array.isArray(st.errors) && st.errors.length ? `errors: ${cell(clip(String(st.errors[0]), 100))}` : null,
            st.visual ? visualNote(st.visual) : null,
            st.changed ? "baseline changed" : null,
          ].filter(Boolean).join("; ");
          md.push(`| ${st.n} | ${cell(clip(String(st.label ?? ""), 70))} | ${status} | ${screen} | ${notes} | ${frame} |`);
        }
        md.push("");
      }
      const visualFails = rows.filter(st => st.visual?.status === "failed");
      if (visualFails.length) {
        md.push("Visual differences (baseline · now · diff: red = changed pixels, yellow = regions, blue = masked):", "");
        for (const st of visualFails) {
          const captures = calls.get(Number(st.frame_call))?.captures ?? [];
          // The step's frame comes first; the baseline and the diff carry their labels.
          const shots = [["baseline", captures.find(c => c.file && c.label === "visual baseline")], ["now", captures.find(c => c.file && !c.label)],
            ["diff", captures.find(c => c.file && c.label === "visual diff")]]
            .map(([name, capture]) => {
              if (!capture) return null;
              const thumb = thumbFor(capture as Record<string, any>);
              return thumb ? `[![${name}](${thumb})](${(capture as Record<string, any>).file})` : `[${name}](${(capture as Record<string, any>).file})`;
            }).filter(Boolean);
          const where = Array.isArray(st.visual.regions) ? st.visual.regions.slice(0, 3).map((r: any) => `${r.where}${Array.isArray(r.near) && r.near.length ? ` (${cell(r.near.join(", "))})` : ""}`).join("; ") : "";
          md.push(`- step ${st.n} '${cell(clip(String(st.label ?? ""), 60))}': ${pctText(st.visual.ratio)} changed${where ? ` — ${where}` : ""}`, "");
          if (shots.length) md.push(shots.join(" "), "");
        }
      }
      const runVisual = run.visual as Record<string, any> | undefined;
      if (runVisual) md.push(`Pictures (${code(String(runVisual.key ?? "?"))}): ${["passed", "failed", "unbaselined", "recorded", "skipped"].filter(k => Number(runVisual[k]) > 0).map(k => `${runVisual[k]} ${k}`).join(", ") || "none compared"}.`, "");
      const candidates = (run.failure as Record<string, any> | undefined)?.candidates;
      if (Array.isArray(candidates) && candidates.length)
        md.push(`On screen instead: ${candidates.slice(0, 8).map((c: string) => cell(clip(c, 60))).join("; ")}`, "");
      const drift = Array.isArray(run.drift) ? run.drift as Array<Record<string, any>> : [];
      if (drift.length) {
        md.push("Drift (passed, but changed):", "");
        for (const d of drift.slice(0, 20)) md.push(`- step ${d.step}: ${code(String(d.kind))} ${cell(clip(String(d.detail ?? ""), 200))}`);
        md.push("");
      }
      const found = (run.findings ?? {}) as Record<string, any>;
      if (Array.isArray(found.new) && found.new.length) {
        md.push("New detector findings against the baseline:", "");
        for (const f of found.new.slice(0, 20)) md.push(`- step ${f.step}: **${f.severity}** ${code(String(f.kind))} ${f.path ? code(String(f.path)) : ""} ${cell(clip(String(f.detail ?? ""), 160))}`);
        md.push("");
      }
      if (Array.isArray(found.fixed) && found.fixed.length) md.push(`No longer found: ${found.fixed.slice(0, 10).map((k: string) => code(k)).join(", ")}`, "");
      for (const e of Array.isArray(run.earlier_failures) ? run.earlier_failures : [])
        md.push(`Attempt ${e.attempt} failed at step ${e.n} '${cell(clip(String(e.label ?? ""), 60))}': ${code(String(e.reason ?? ""))} ${cell(clip(String(e.message ?? ""), 160))}`, "");
      const baseline = run.baseline as Record<string, any> | undefined;
      if (baseline) md.push(baseline.error ? `The baseline could not be written to ${code(String(baseline.file))}: ${cell(String(baseline.error))}` : `Baseline written to ${code(String(baseline.file))}${Array.isArray(baseline.changed_steps) && baseline.changed_steps.length ? ` (changed steps: ${baseline.changed_steps.join(", ")})` : ""}.`, "");
      if (Number(run.unbaselined ?? 0) > 0) notes.push(`Replay ${i + 1}: ${run.unbaselined} step(s) had no baseline and passed on execution alone.`);
      for (const note of Array.isArray(run.notes) ? run.notes : []) notes.push(`Replay ${i + 1}: ${String(note)}`);
    });
  }

  if (checks.length) {
    md.push("## Screen checks", "");
    checks.forEach((check, i) => {
      const counts = (check.counts ?? {}) as Record<string, number>;
      const capture = frameOfCall(check.call);
      const thumb = capture ? thumbFor(capture) : null;
      md.push(`### Check ${i + 1}: ${cell(String(check.label ?? ""))} — bug ${counts.bug ?? 0} · suspicious ${counts.suspicious ?? 0}`, "");
      if (capture) md.push(thumb ? `[![check ${i + 1}](${thumb})](${capture.file})` : `[frame](${capture.file})`, "");
      const list = Array.isArray(check.findings) ? check.findings : [];
      if (!list.length) md.push("No findings.", "");
      for (const f of list) md.push(`- **${f.severity}** ${code(String(f.kind))} — ${findingWhere(f) || "the screen"}: ${cell(clip(String(f.detail ?? ""), 200))}`);
      if (list.length) md.push("");
      for (const note of Array.isArray(check.notes) ? check.notes : []) notes.push(`Check ${i + 1}: ${String(note)}`);
    });
  }

  md.push("## Steps", "");
  if (!steps.length) md.push("No calls were recorded.", "");
  for (const step of steps) {
    md.push(step.number !== null ? `### Step ${step.number}: ${cell(step.label)} — ${step.verdict}` : `### ${step.label}`, "");
    if (step.note) md.push(`> ${cell(step.note)}`, "");
    if (!step.calls.length) { md.push("_No recorded calls in this step._", ""); continue; }
    md.push("| # | Time | Tool | Arguments | Result | Duration |", "|---|---|---|---|---|---|");
    for (const call of step.calls) {
      const result = call.lost ? "**lost** (server stopped)"
        : call.running ? "**still running at end**"
        : call.status === "error" ? `**error** ${cellCode(call.error?.code ?? "")}${call.error?.message ? ` ${cell(clip(call.error.message, 120))}` : ""}`
        : call.status === "job_started" ? `job ${cellCode(call.job_id ?? "")}`
        : resultText(call);
      md.push(`| ${call.seq}${call.of ? ` (of #${call.of})` : ""} | ${time(call.at)} | ${cellCode(shortTool(call.tool))} | ${call.args !== undefined ? cellCode(clip(JSON.stringify(call.args), 160)) : ""} | ${result} | ${call.lost || call.running ? "—" : duration(call.duration_ms)} |`);
    }
    md.push("");
    const stepErrors = step.calls.flatMap(c => [
      ...c.errors.map(e => ({ e, c, relation: "during" })), ...c.errorsAfter.map(e => ({ e, c, relation: "after" })),
    ]);
    if (stepErrors.length) {
      md.push("Console errors in this step:", "");
      for (const { e, c, relation } of stepErrors.slice(0, 20)) {
        const row = catalog.get(errorKey(e));
        md.push(`- ${e.type} ${code(clip(firstLine(String(e.message ?? "")), 200))} — ${relation} call #${c.seq}${row ? ` → [E${row.id}](#e${row.id})` : ""}`);
      }
      if (stepErrors.length > 20) md.push(`- … ${stepErrors.length - 20} more`);
      md.push("");
    }
    const shots: string[] = [];
    for (const call of step.calls) {
      const files = call.captures.filter(c => c.file);
      // A series: its contact sheet, else the first and the last frame.
      const sheet = files.find(c => c.label === "contact sheet");
      const chosen = sheet ? [sheet] : files.length > 3 ? [files[0], files[Math.floor(files.length / 2)], files[files.length - 1]] : files;
      for (const capture of chosen) {
        const thumb = thumbFor(capture);
        const caption = `#${call.seq}${capture.label ? ` ${capture.label}` : ""}${capture.frame !== undefined ? ` F${capture.frame}` : ""}`;
        shots.push(thumb ? `[![${caption}](${thumb})](${capture.file})` : `[${caption}](${capture.file})`);
      }
      const rest = files.filter(f => !chosen.includes(f));
      if (rest.length) shots.push(`#${call.seq}: ${rest.length} more frame(s): ${rest.slice(0, 12).map((f, i) => `[${f.frame !== undefined ? `F${f.frame}` : i + 1}](${f.file})`).join(" ")}${rest.length > 12 ? " …" : ""}`);
      const skipped = call.captures.filter(c => !c.file && c.source);
      if (skipped.length) shots.push(`#${call.seq}: ${skipped.length} frame(s) not copied (${skipped[0].skipped ?? skipped[0].missing ?? "unavailable"}): ${code(skipped[0].source)}${skipped.length > 1 ? " …" : ""}`);
    }
    if (shots.length) md.push("Frames:", "", shots.join(" "), "");
  }

  md.push("## Errors", "");
  if (!errorRows.length) md.push("No console errors or exceptions were logged while recording.", "");
  for (const row of errorRows) {
    md.push(`### E${row.id}`, "");
    md.push(`**${row.type}** ${code(clip(firstLine(row.message), 300))}`, "");
    md.push(`First occurrence: ${row.first_step !== null ? `step ${row.first_step}` : "outside a marked step"}${row.first_call !== null ? `, ${row.relation} call #${row.first_call} ${code(shortTool(row.first_tool ?? ""))}` : ` (${row.relation})`}${row.at ? ` at ${row.at}` : ""}; seen ${row.count} time(s).`, "");
    const body = [row.message.includes("\n") ? row.message : null, row.stack ? row.stack.trim() : null].filter(Boolean).join("\n");
    if (body) md.push("```text", clip(body, 4_000).replace(/```/g, "'''"), "```", "");
    else md.push("_No stack trace (reported by the tool response; the event stream was not connected)._", "");
  }

  md.push("## State diff", "");
  let stateDiff: ReportSummary["state_diff"] = null;
  if (!meta.snapshot) md.push("No player-state snapshot was taken for this session.", "");
  else if (!input.stateDiff) md.push(`Not computed (\`diff: false\`). Snapshot: ${code(meta.snapshot.name)}.`, "");
  else if (!input.stateDiff.ok) {
    stateDiff = { error: input.stateDiff.error };
    md.push(`The diff against ${code(meta.snapshot.name)} failed: ${code(JSON.stringify(input.stateDiff.error).slice(0, 300))}.`, "");
  } else {
    const diff = input.stateDiff.output as Record<string, any>;
    writeFileSync(join(reportDir, "state-diff.json"), JSON.stringify(diff, null, 2));
    stateDiff = { identical: diff.identical, prefs: diff.prefs?.counts, files: diff.files?.counts, editor_prefs: diff.editor_prefs?.counts ?? null };
    md.push(`Snapshot ${code(meta.snapshot.name)} → the state at the end of the session${diff.to?.mode ? ` (${diff.to.mode} mode)` : ""}. Full diff: [state-diff.json](state-diff.json).`, "");
    if (diff.identical) md.push("No PlayerPrefs, EditorPrefs or persistentDataPath change.", "");
    for (const [title, part] of [["PlayerPrefs", diff.prefs], ["EditorPrefs", diff.editor_prefs]] as const) {
      if (!part) continue;
      const rows = [
        ...(part.added ?? []).map((e: any) => ["added", e.key, "", show(e.to ?? e)]),
        ...(part.removed ?? []).map((e: any) => ["removed", e.key, show(e.from ?? e), ""]),
        ...(part.changed ?? []).map((e: any) => ["changed", e.key, show(e.from), show(e.to)]),
      ];
      if (!rows.length) continue;
      md.push(`${title}: ${JSON.stringify(part.counts ?? {})}`, "", "| Change | Key | From | To |", "|---|---|---|---|");
      for (const [change, key, from, to] of rows) md.push(`| ${change} | ${cellCode(String(key))} | ${cell(from)} | ${cell(to)} |`);
      if (part.truncated) md.push(`| … | ${part.truncated} more | | |`);
      md.push("");
    }
    const files = diff.files;
    if (files && ((files.added?.length ?? 0) + (files.removed?.length ?? 0) + (files.changed?.length ?? 0)) > 0) {
      md.push(`persistentDataPath files: ${JSON.stringify(files.counts ?? {})}`, "", "| Change | Path | Size |", "|---|---|---|");
      for (const e of files.added ?? []) md.push(`| added | ${cellCode(String(e.path))} | ${e.size ?? ""} |`);
      for (const e of files.removed ?? []) md.push(`| removed | ${cellCode(String(e.path))} | ${e.size ?? ""} |`);
      for (const e of files.changed ?? []) md.push(`| changed | ${cellCode(String(e.path))} | ${e.from?.size ?? ""} → ${e.to?.size ?? ""} |`);
      md.push("");
    }
  }

  if (editorEvents.length) {
    md.push("## Editor events", "", "| Time | State |", "|---|---|");
    for (const e of editorEvents) md.push(`| ${time(e.at)} | ${cellCode(String(e.state))}${EDITOR_STATE_NOTES[String(e.state)] ?? ""} |`);
    md.push("");
  }
  const streamDown = allCalls.filter(c => c.streamDown).length;
  if (streamDown) notes.push(`${streamDown} call(s) ran while the bridge event stream was disconnected (a domain reload or restart in progress): their errors come from the tool responses only, without stacks.`);
  if (thumbSkipped) notes.push(`${thumbSkipped} frame(s) are linked without a thumbnail (thumbnail limit ${input.maxThumbnails} or time budget).`);
  md.push("## Notes", "");
  md.push("- Frames are full-size PNGs in [frames/](frames/); thumbnails in [thumbs/](thumbs/). The raw log is `../log.jsonl` (one JSON line per event).");
  md.push("- Errors are matched to calls by time: \"during\" = logged while the call ran, \"after\" = logged between this call and the next recorded one.");
  md.push("- Secrets in arguments, notes, console text and the state diff are replaced with `[redacted]`.");
  for (const note of notes) md.push(`- ${cell(note)}`);
  md.push("");

  const reportPath = join(reportDir, REPORT_FILE);
  writeFileSync(reportPath, redactor.text(md.join("\n")));

  return {
    report: reportPath, steps: steps.length, calls: allCalls.length, failed_calls: failedCalls, captures: captureCount, thumbnails: thumbs,
    duration_ms: Number.isFinite(endedMs - startedMs) ? endedMs - startedMs : 0, verdicts,
    errors: { unique: errorRows.length, total: totalErrors },
    findings: findings.slice(0, 10).map(s => ({ step: s.number ?? 0, label: s.label, verdict: s.verdict!, ...(s.note ? { note: clip(s.note, 300) } : {}) })),
    top_errors: errorRows.slice(0, 5).map(r => ({ type: r.type, message: clip(firstLine(r.message), 200), first_step: r.first_step, first_call: r.first_call, relation: r.relation, count: r.count })),
    lost_in_flight: lost, running_at_end: runningAtEnd, state_diff: stateDiff, hooks,
    detector_findings: { ...detectorCounts, items: detector.slice(0, 10).map(({ source, ...f }) => ({ ...f, source })) },
    explorations: explores.map(run => ({
      stop_reason: String(run.stop_reason ?? ""), steps: Number(run.steps ?? 0), screens: Number(run.coverage?.screens ?? 0),
      bug: Number(run.finding_counts?.bug ?? 0), suspicious: Number(run.finding_counts?.suspicious ?? 0),
      ...(run.device ? { device: String(run.device) } : {}),
    })),
    replays: replays.map(run => {
      const f = run.failure as Record<string, any> | undefined;
      return {
        scenario: String(run.scenario?.name ?? ""), verdict: String(run.verdict ?? ""), steps: Number(run.scenario?.steps ?? 0),
        ...(f ? { failed_step: Number(f.n), reason: String(f.reason ?? "") } : {}),
      };
    }),
    device_runs: deviceRuns.map(run => ({
      screens: String(run.screens ?? ""), checked: Number(run.checked ?? 0), stop_reason: String(run.stop_reason ?? ""),
      bug: Number(run.finding_counts?.bug ?? 0), suspicious: Number(run.finding_counts?.suspicious ?? 0), restored: run.restored?.restored === true,
    })),
    notes,
  };
}

const EDITOR_STATE_NOTES: Record<string, string> = {
  reloading: " (domain reload)",
  stream_reconnected: " (the bridge was away and came back: a domain reload or an Editor restart)",
  entering_playmode: " (entering play mode)",
  exiting_playmode: " (leaving play mode)",
};

function show(value: any): string {
  if (value === undefined || value === null) return "";
  if (typeof value === "object") {
    if ("value" in value) return `${value.type ? `${value.type} ` : ""}${clip(JSON.stringify(value.value), 80)}`;
    if ("length" in value) return `${value.type ?? "string"} (${value.length} chars)`;
    return clip(JSON.stringify(value), 80);
  }
  return clip(String(value), 80);
}

function resultText(call: CallRow): string {
  const s = call.summary ?? {};
  const parts: string[] = [];
  if (call.kind === "replay") {
    if (typeof s.status === "string") parts.push(s.status === "failed" ? `failed ${s.reason ?? ""}`.trim() : s.status);
    if (typeof s.similarity === "number") parts.push(`${Math.round(s.similarity * 100)}% '${clip(String(s.title ?? ""), 30)}'`);
    if (call.captures.some(c => c.file)) parts.push("frame");
    return parts.map(p => cell(p)).join(" · ") || "ok";
  }
  if (call.kind === "explore" || call.kind === "check") {
    if (typeof s.outcome === "string") parts.push(s.outcome);
    if (typeof s.to === "string") parts.push(`→ ${s.to}`);
    else if (typeof s.screen === "string" && s.purpose === "observe") parts.push(`start ${s.screen}`);
    const found = Array.isArray(s.findings) ? s.findings.length : 0;
    if (found) parts.push(`${found} finding(s)`);
    if (call.captures.some(c => c.file)) parts.push("frame");
    return ["ok", ...parts].map(p => cell(p)).join(" · ");
  }
  if (typeof s.outcome === "string") parts.push(s.outcome);
  const fired = s.fired as Record<string, any> | undefined;
  if (fired && typeof fired === "object" && fired.type) parts.push(`fired ${fired.type}${fired.frame !== undefined ? ` @F${fired.frame}` : ""}`);
  const captured = s.capturedAt as Record<string, any> | undefined;
  if (captured && typeof captured === "object") parts.push([captured.mode, captured.device ?? captured.size, Array.isArray(captured.screen) ? captured.screen.join("×") : null].filter(Boolean).join(" "));
  const series = s.series as Record<string, any> | undefined;
  if (series && typeof series === "object" && series.captured !== undefined) parts.push(`${series.captured} frame(s)`);
  if (Array.isArray(s.backend)) parts.push(s.backend.join("+"));
  if (s.result !== undefined) parts.push(code(clip(JSON.stringify(s.result), 100)));
  if (typeof s.action === "string" && call.kind === "control") parts.push(`${s.action} requested`);
  else if (typeof s.isPlaying === "boolean") parts.push(s.isPlaying ? "playing" : "edit mode");
  if (call.captures.length && !series) parts.push(`${call.captures.length} frame(s)`);
  return ["ok", ...parts].map(p => cell(p)).join(" · ");
}

const pctText = (ratio: unknown) => `${Math.round(Number(ratio ?? 0) * 10000) / 100}%`;

/** A replay step's picture check, short: "visual 0%", "visual 0.4% changed", "no visual baseline". */
function visualNote(visual: Record<string, any>): string {
  switch (visual.status) {
    case "passed": return `visual ${pctText(visual.ratio)}`;
    case "failed": return `visual ${pctText(visual.ratio)} changed`;
    case "unbaselined": return "no visual baseline";
    case "recorded": return visual.note ? "visual recorded (still moving)" : "visual recorded";
    default: return `visual skipped${visual.note ? ` (${cell(clip(String(visual.note), 60))})` : ""}`;
  }
}
