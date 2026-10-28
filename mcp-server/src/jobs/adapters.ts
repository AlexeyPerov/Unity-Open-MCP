import { setTimeout as delay } from "node:timers/promises";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { ProjectJobHttpError, type LiveClient } from "../live-client.js";
import { validateSchema } from "../tool-contract.js";
import { runTests } from "../tools/run-tests.js";
import { inputsimGesture } from "../tools/inputsim-gesture.js";
import { JobManagerError, type JobOperation, type JobOutcome, type JobOwner } from "./job-manager.js";

type ClientFor = (owner: JobOwner) => LiveClient;
function invalid(errors: string[]): void {
  if (errors.length) throw new JobManagerError("invalid_arguments", errors.join("; "));
}
export function testRunOperation(client: ClientFor): JobOperation {
  return {
    // A test run has side effects even though the legacy tool is classified as a sense.
    mutating: true, cancellable: false,
    validate(args) {
      invalid(validateSchema(args, runTests.inputSchema));
      if (args.run_id !== undefined) throw new JobManagerError("invalid_arguments", "Jobs own run_id; use idempotency_key to retry.");
    },
    async run(args, context) {
      context.report({ phase: "starting test run" });
      const response = await client(context.owner).runTestsJob({ ...args, run_id: context.jobId, timeout_ms: args.timeout_ms ?? 600000 }, () => {
        context.lifecycle("connected", `Test results owned by run id ${context.jobId}`, true);
        context.report({ phase: "awaiting test result file" });
      });
      const text = response.content.find(item => item.type === "text");
      const result = text?.type === "text" ? JSON.parse(text.text) : {};
      if (result.error?.code === "test_results_timeout" || result.error?.code === "timeout") {
        context.lifecycle("disconnected", "Test result observation budget expired; run may still be executing.", false);
      }
      if (response.isError || result.status === "aborted" || result.summary?.failed > 0)
        return { state: "failed", error: { code: result.error?.code ?? "test_run_failed", message: result.error?.message ?? "Test run did not pass." }, result };
      if (result.status !== "completed" || result.runId !== context.jobId || !result.summary)
        throw new Error("Test result did not confirm terminal ownership.");
      context.report({ phase: "test results received" });
      return { state: "succeeded", result };
    },
  };
}

/** Bound the bridge status poll so a hung async command cannot own the project queue forever. */
export const PROJECT_JOB_DEFAULT_DEADLINE_MS = 30 * 60_000;
/** How long the bridge may leave status polls unanswered before the job's outcome counts as unknown. */
export const PROJECT_JOB_DEFAULT_UNREACHABLE_MS = 5 * 60_000;
export interface ProjectJobPollOptions { deadlineMs?: number; pollIntervalMs?: number; unreachableMs?: number }

export function projectCommandOperation(id: string, cancellable: boolean, client: ClientFor, poll: ProjectJobPollOptions = {}): JobOperation {
  const deadlineMs = poll.deadlineMs ?? PROJECT_JOB_DEFAULT_DEADLINE_MS;
  const pollIntervalMs = poll.pollIntervalMs ?? 500;
  const unreachableMs = poll.unreachableMs ?? PROJECT_JOB_DEFAULT_UNREACHABLE_MS;
  return {
    mutating: true, cancellable,
    validate(args) {
      const allowed = ["args", "schema_version", "paths_hint", "gate", "ignore_scene_dirty", "confirm_bypass"];
      invalid(Object.keys(args).filter(key => !allowed.includes(key)).map(key => `Unknown project job option: ${key}`));
    },
    async run(args, context) {
      const live = client(context.owner);
      context.report({ phase: "preflight" });
      const catalog = await live.projectCommands({ action: "describe", id });
      const command = catalog.command as Record<string, any> | undefined;
      if (!command?.available || !command.async || command.cancellable !== cancellable)
        return { state: "failed", error: { code: "command_unavailable", message: "Describe the command again; async contract is unavailable or changed." } };
      const errors = validateSchema(args.args ?? {}, command.inputSchema);
      if (errors.length) return { state: "failed", error: { code: "invalid_arguments", message: errors.join("; ") } };
      let response = await live.projectCommandJob({ action: "start", job_id: context.jobId,
        invocation: { ...args, action: "invoke", command_id: id, schema_version: args.schema_version ?? command.schemaVersion } });
      let cancelSent = false;
      let phase: string | undefined;
      let ownershipConfirmed = false;
      const startedAt = Date.now();
      while (true) {
        if (response.error || response.mutation?.success === false) {
          if (response.error?.code === "job_not_found") context.lifecycle("disconnected", "Bridge lost domain-local job ownership.", false);
          return { state: "failed", error: { code: response.error?.code ?? response.mutation?.error?.code ?? "command_rejected", message: response.error?.message ?? response.mutation?.error?.message ?? "Bridge rejected command." }, result: response };
        }
        if (response.job_id !== context.jobId) throw new Error("Bridge did not confirm job ownership");
        if (!ownershipConfirmed) {
          context.lifecycle("connected", `Bridge owns job ${context.jobId}`, true);
          ownershipConfirmed = true;
        }
        if (phase !== response.phase) { phase = response.phase; context.report({ phase: response.phase }); }
        if (response.state === "succeeded") return { state: "succeeded", result: response.result };
        if (response.state === "cancelled") return { state: "cancelled", result: response.result };
        if (response.state === "failed") return { state: "failed", error: { code: "command_failed", message: "See terminal gate/command result." }, result: response.result };
        // A poll only observes work the bridge keeps running. A main thread busy
        // past the bridge's dispatch timeout (an import, synchronous project code)
        // answers HTTP 500 or not at all, so an unanswered poll is retried with
        // backoff. A cancel is not resent: it may still be queued, and the status
        // polls after it show whether it was acknowledged.
        const answeredAt = Date.now();
        let failures = 0;
        while (true) {
          if (Date.now() - startedAt >= deadlineMs) {
            // The bridge never reported a terminal state. Ask it to stop when the
            // contract allows, then release this job's execution slot as orphaned:
            // the remote outcome is unknown, so a blind retry is not safe.
            if (cancellable && !cancelSent) {
              cancelSent = true;
              try { await live.projectCommandJob({ action: "cancel", job_id: context.jobId }); } catch { /* best effort */ }
            }
            context.lifecycle("disconnected", `Bridge job reported no terminal state within ${deadlineMs} ms; outcome unknown.`, false);
            return { state: "failed", error: { code: "job_deadline_exceeded", message: `No terminal state within ${deadlineMs} ms.` }, result: response };
          }
          if (failures && Date.now() - answeredAt >= unreachableMs) {
            context.lifecycle("disconnected", `Bridge answered no job poll for ${unreachableMs} ms; outcome unknown.`, false);
            return { state: "failed", error: { code: "job_status_unavailable", message: `No job status within ${unreachableMs} ms.` }, result: response };
          }
          // The backoff yields to a cancel request: an abort while sleeping
          // ends the sleep so the cancel goes out now, not after the backoff.
          // Once the cancel has been sent the signal stays aborted, so the
          // remaining status polls sleep without it.
          try {
            await delay(pollIntervalMs * 2 ** Math.min(failures, 3), undefined,
              cancelSent ? undefined : { signal: context.signal });
          } catch { /* aborted: forward the cancel below */ }
          const action = context.signal.aborted && !cancelSent ? "cancel" : "status";
          if (action === "cancel") cancelSent = true;
          try {
            response = await live.projectCommandJob({ action, job_id: context.jobId });
            break;
          } catch (error) {
            // A 4xx is the bridge refusing the transport itself; retrying cannot help.
            if (error instanceof ProjectJobHttpError && error.status < 500) throw error;
            if (failures++ === 0) {
              // Ownership was confirmed by the last answer; the next answer reconfirms it.
              ownershipConfirmed = false;
              context.lifecycle("disconnected", `Job poll failed (${String(error).slice(0, 200)}); retrying while the bridge owns job ${context.jobId}.`, true);
            }
          }
        }
      }
    },
  };
}

// ---------------------------------------------------------------------------
// Play-mode operations — one generic adapter for every tool whose work runs on
// the bridge's shared play-mode scheduler (a player-loop tick that owns timing,
// cancellation and reload / exit-play cleanup). The tool call starts the
// operation with the job id as its operation_id and returns at once; status and
// cancel go through POST /playmode-operations. Records are domain-local: a
// reload drops them (after releasing held input), which surfaces here as
// `operation_not_found` and orphans the job — never a restart.
// ---------------------------------------------------------------------------

/** Tools served by the generic `playmode_operation` jobs adapter. */
export const PLAYMODE_OPERATION_TOOLS: ReadonlyMap<string, Record<string, any>> = new Map([
  [inputsimGesture.name, inputsimGesture.inputSchema as Record<string, any>],
]);
export const PLAYMODE_JOB_DEFAULT_DEADLINE_MS = 10 * 60_000;
export const PLAYMODE_JOB_DEFAULT_UNREACHABLE_MS = 2 * 60_000;
export interface PlaymodePollOptions { deadlineMs?: number; pollIntervalMs?: number; unreachableMs?: number }

const PLAYMODE_TERMINAL = new Set(["succeeded", "failed", "cancelled", "orphaned"]);

/** Parsed JSON body of a tool result ({} when it is not JSON). */
export function toolResultBody(result: CallToolResult): Record<string, any> {
  const text = result.content.find(item => item.type === "text");
  if (text?.type !== "text") return {};
  try { return JSON.parse(text.text) as Record<string, any>; } catch { return {}; }
}

/**
 * The tool's own output: registry tools answer inside the gate envelope
 * (`mutation.output` / `mutation.error`); other shapes are returned as-is.
 */
export function toolOutput(result: CallToolResult): Record<string, any> {
  const body = toolResultBody(result);
  const mutation = body.mutation;
  if (mutation && typeof mutation === "object") {
    if (mutation.output && typeof mutation.output === "object") return mutation.output as Record<string, any>;
    if (mutation.error) return { error: mutation.error };
  }
  return body;
}

export type PlaymodePollOutcome =
  | { kind: "terminal"; snapshot: Record<string, any>; cancelSent: boolean }
  | { kind: "lost"; detail: Record<string, any> }
  | { kind: "deadline"; snapshot: Record<string, any> | null; cancelSent: boolean }
  | { kind: "unreachable"; error: string };

/**
 * Poll a scheduler operation to a terminal state. An abort of `signal` sends one
 * cancel and keeps polling for its acknowledgement; at the deadline a cancel is
 * sent once (the bridge then releases held input) before giving up.
 */
export async function pollPlaymodeOperation(live: LiveClient, operationId: string, options: PlaymodePollOptions & {
  signal?: AbortSignal;
  onSnapshot?: (snapshot: Record<string, any>) => void;
  onTransport?: (connected: boolean, detail: string) => void;
} = {}): Promise<PlaymodePollOutcome> {
  const deadlineMs = options.deadlineMs ?? PLAYMODE_JOB_DEFAULT_DEADLINE_MS;
  const pollIntervalMs = options.pollIntervalMs ?? 250;
  const unreachableMs = options.unreachableMs ?? PLAYMODE_JOB_DEFAULT_UNREACHABLE_MS;
  const startedAt = Date.now();
  let lastAnswer = Date.now();
  let failures = 0;
  let cancelSent = false;
  let last: Record<string, any> | null = null;
  while (true) {
    const overdue = Date.now() - startedAt >= deadlineMs;
    const wantCancel = !cancelSent && (overdue || options.signal?.aborted === true);
    if (!wantCancel) {
      try {
        await delay(pollIntervalMs * 2 ** Math.min(failures, 3), undefined,
          cancelSent || !options.signal ? undefined : { signal: options.signal });
      } catch { /* aborted: send the cancel now */ }
    }
    const action = !cancelSent && (overdue || options.signal?.aborted === true) ? "cancel" : "status";
    if (action === "cancel") cancelSent = true;
    let answer: Record<string, any>;
    try {
      answer = await live.playmodeOperation({ action, operation_id: operationId });
      if (failures) options.onTransport?.(true, `Bridge answered again for operation ${operationId}.`);
      failures = 0;
      lastAnswer = Date.now();
    } catch (error) {
      if (error instanceof ProjectJobHttpError && error.status < 500) return { kind: "unreachable", error: String(error) };
      if (failures++ === 0) options.onTransport?.(false, `Operation poll failed (${String(error).slice(0, 200)}); retrying.`);
      if (Date.now() - lastAnswer >= unreachableMs) return { kind: "unreachable", error: `No answer for ${unreachableMs} ms.` };
      continue;
    }
    if (answer.error?.code === "operation_not_found") return { kind: "lost", detail: answer };
    if (answer.error) return { kind: "unreachable", error: `${answer.error.code}: ${answer.error.message}` };
    last = answer;
    options.onSnapshot?.(answer);
    if (PLAYMODE_TERMINAL.has(String(answer.state))) return { kind: "terminal", snapshot: answer, cancelSent };
    // A cancel sent at the deadline gets a short grace window for its acknowledgement.
    if (overdue && cancelSent && Date.now() - startedAt >= deadlineMs + 5_000) return { kind: "deadline", snapshot: last, cancelSent };
  }
}

/** Flatten a terminal scheduler snapshot into a job/tool result. */
export function playmodeResult(snapshot: Record<string, any>): Record<string, unknown> {
  const { result, ...meta } = snapshot;
  return { operation: meta, ...(result && typeof result === "object" ? result : {}) };
}

export function playmodeOperation(tool: string, schema: Record<string, any>, client: ClientFor, poll: PlaymodePollOptions = {}): JobOperation {
  return {
    mutating: false, cancellable: true,
    validate(args) {
      if (args.operation_id !== undefined) throw new JobManagerError("invalid_arguments", "Jobs own operation_id; use idempotency_key to retry.");
      invalid(validateSchema(args, schema));
    },
    async run(args, context): Promise<JobOutcome> {
      const live = client(context.owner);
      context.report({ phase: "starting" });
      // Start is idempotent per operation id: a retried POST returns the same record.
      const started = toolOutput(await live.route(tool, { ...args, operation_id: context.jobId }));
      const operation = started.operation as Record<string, any> | undefined;
      if (started.error || !operation)
        return { state: "failed", error: { code: started.error?.code ?? "operation_rejected", message: started.error?.message ?? "Bridge did not start the play-mode operation." }, result: started };
      if (operation.operation_id !== context.jobId) throw new Error("Bridge did not confirm play-mode operation ownership.");
      context.lifecycle("connected", `Bridge play-mode scheduler owns operation ${context.jobId}`, true);
      let phase: string | undefined;
      const outcome = await pollPlaymodeOperation(live, context.jobId, {
        ...poll, signal: context.signal,
        onSnapshot: snapshot => {
          const expected = Number(snapshot.progress?.expectedMs);
          const elapsed = Number(snapshot.progress?.elapsedMs);
          const fraction = expected > 0 && Number.isFinite(elapsed) ? Math.min(1, Math.max(0, elapsed / expected)) : undefined;
          if (snapshot.phase !== phase || fraction !== undefined) {
            phase = String(snapshot.phase);
            context.report({ phase, ...(fraction === undefined ? {} : { fraction }) });
          }
        },
        onTransport: (connected, detail) => context.lifecycle(connected ? "connected" : "disconnected", detail, true),
      });
      switch (outcome.kind) {
        case "lost":
          context.lifecycle("disconnected", "Bridge lost the play-mode operation (domain reload or Editor restart); its held input was released by that cleanup.", false);
          return { state: "failed", error: { code: "operation_lost", message: "The bridge no longer knows this operation." }, result: outcome.detail };
        case "unreachable":
          context.lifecycle("disconnected", `Play-mode operation status unavailable: ${outcome.error}`, false);
          return { state: "failed", error: { code: "job_status_unavailable", message: outcome.error } };
        case "deadline":
          context.lifecycle("disconnected", "Play-mode operation reported no terminal state before the deadline; cancel sent.", false);
          return { state: "failed", error: { code: "job_deadline_exceeded", message: "No terminal state before the deadline." }, ...(outcome.snapshot ? { result: outcome.snapshot } : {}) };
      }
      const snapshot = outcome.snapshot;
      const result = playmodeResult(snapshot);
      if (snapshot.state === "succeeded") return { state: "succeeded", result };
      if (snapshot.state === "cancelled" && context.signal.aborted) return { state: "cancelled", result };
      if (snapshot.state === "orphaned") {
        context.lifecycle("disconnected", `Bridge ended the operation without completing it (${snapshot.reason}).`, false);
        return { state: "failed", error: { code: "operation_lost", message: String(snapshot.reason) }, result };
      }
      // cancelled without our request = play mode exited; failed = operation error.
      return { state: "failed", error: { code: snapshot.state === "cancelled" ? String(snapshot.reason ?? "cancelled") : "operation_failed",
        message: String(snapshot.result?.error ?? snapshot.reason ?? "Play-mode operation did not complete.") }, result };
    },
  };
}
