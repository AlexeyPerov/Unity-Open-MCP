// The compile result as the answer of a compile-type call.
//
// `recompile_scripts` exists to compile, and an `assets_refresh` that imported
// scripts starts a compile: for both, "the call ran" says nothing — the agent
// needs the compile's outcome. The bridge cannot report it in the call's own
// response (a successful compile ends in a domain reload that closes the
// response; the compile starts on a later Editor tick), so the router waits
// for it with the editor_status wait primitive (editor-state.ts#waitForEditor),
// keyed to the generation the call started at, and folds the result into the
// call's answer:
//
//   - `status`: `compiled` | `compile_failed` | `no_compile_ran` |
//     `compile_unverified` | `compiling` (still running past the inline window;
//     the wait continues as a `jobs` job) | the wait's error code
//     (`bridge_compile_failed`, `bridge_offline`, `wait_timeout`);
//   - `compile`: generationAtStart, generation, status, errorCount, the first
//     errors, whether a compile ran after generationAtStart, waitedMs;
//   - a failed compile is a failed call: `isError: true`, `mutation.success:
//     false` and `mutation.error.code: "compile_failed"` (block-1 refusal shape:
//     the error object carries the detail), and `agentNextSteps` lead with the
//     failure — the gate's "Gate passed" line, which only covered asset
//     validation before the compile ran, is dropped;
//   - the wait steps the bridge appended are dropped (the wait already ran),
//     as are its pre-wait `compilePending` flag and the `_compileVerify`
//     snapshot taken before the compile finished.
//
// A call that left no compile (an assets_refresh that imported no scripts, a
// refused call) is returned unchanged.

import {
  describeWait, waitCall,
  type WaitOutcome, type WaitRequest,
} from "./editor-state.js";

export const COMPILE_RESULT_TOOLS: ReadonlySet<string> = new Set([
  "unity_open_mcp_recompile_scripts",
  "unity_open_mcp_assets_refresh",
]);

/** How many diagnostics a failed compile result carries inline (read_compile_errors has them all). */
export const COMPILE_RESULT_FIRST_ERRORS = 5;

function num(v: unknown): number | undefined {
  return typeof v === "number" && Number.isFinite(v) ? v : undefined;
}

/**
 * The compile a compile-type call left to wait for, or null when it left none.
 * `generationAtStart` is the compile generation the call started at (the wait
 * requires a compile after it); undefined when the answer did not carry it.
 */
export function compileToAwait(
  toolName: string,
  body: Record<string, any>,
  isError: boolean,
): { generationAtStart?: number } | null {
  // A domain reload closed the response: the call ran, a compile finished and
  // the reload is under way.
  if (body.status === "triggered_reload") {
    const generationAtStart = num(body.compile?.generationAtStart);
    return generationAtStart !== undefined ? { generationAtStart } : {};
  }
  if (isError) return null;
  const mutation = body.mutation;
  if (!mutation || typeof mutation !== "object" || mutation.success !== true) return null;
  const output = mutation.output && typeof mutation.output === "object" ? mutation.output as Record<string, any> : {};
  const compile = body.compile && typeof body.compile === "object" ? body.compile as Record<string, any> : {};
  const generationAtStart = num(compile.generationAtStart) ?? num(output.compileGenerationAtStart);
  const at = generationAtStart !== undefined ? { generationAtStart } : {};
  if (toolName === "unity_open_mcp_recompile_scripts") return output.requested === false ? null : at;
  const generation = num(compile.generation);
  const triggered = output.compileDue === true || output.isCompiling === true
    || compile.pending === true || compile.running === true || body.compilePending === true
    || (generation !== undefined && generationAtStart !== undefined && generation > generationAtStart);
  return triggered ? at : null;
}

/** One diagnostic as a single line (the pipeline's message already names file and line). */
function describeError(e: Record<string, unknown>): string {
  const message = typeof e.message === "string" ? e.message : "";
  const file = typeof e.file === "string" ? e.file : "";
  const text = file && !message.includes(file)
    ? `${file}(${e.line ?? "?"}${typeof e.column === "number" ? `,${e.column}` : ""}): ${typeof e.code === "string" ? e.code + " " : ""}${message}`
    : message || (typeof e.raw === "string" ? e.raw : "");
  return text.length > 300 ? text.slice(0, 297) + "..." : text;
}

/**
 * The call's own steps that still apply: not its wait advice (the wait ran),
 * not a gate pass over a failed compile, and none of a triggered_reload
 * answer's (they only say how to wait for the result now in hand).
 */
function keptSteps(body: Record<string, any>, failed: boolean): string[] {
  if (body.status === "triggered_reload") return [];
  const steps = Array.isArray(body.agentNextSteps) ? body.agentNextSteps.filter((s: unknown): s is string => typeof s === "string") : [];
  return steps.filter((s: string) => !s.includes("wait_until") && !(failed && s.startsWith("Gate passed")));
}

const short = (toolName: string) => toolName.replace(/^unity_open_mcp_/, "");

function baseBody(body: Record<string, any>): Record<string, any> {
  const { _compileVerify: _verify, compilePending: _pending, message: _message, ...rest } = body;
  return rest;
}

/**
 * Fold a finished wait (inline) into the call's answer. `outcome` comes from
 * waitForEditor with `request`; `generationAtStart` is the call's.
 */
export function foldCompileResult(
  toolName: string,
  body: Record<string, any>,
  request: WaitRequest,
  outcome: WaitOutcome,
): { body: Record<string, unknown>; isError: boolean } {
  const c = outcome.compile;
  const generation = c?.generation ?? outcome.view?.compile?.generation ?? null;
  const errors = c?.status === "compile_failed" ? c.errors ?? [] : [];
  const bridgeCompile = body.compile && typeof body.compile === "object" ? body.compile as Record<string, unknown> : {};
  const compile: Record<string, unknown> = {
    generationAtStart: request.afterGeneration ?? num(bridgeCompile.generationAtStart) ?? null,
    generation,
    status: c?.status ?? null,
    errorCount: c?.errorCount ?? outcome.view?.compile?.errorCount ?? null,
    ...(c?.errorSource ? { errorSource: c.errorSource } : {}),
    ...(outcome.compileRan !== null ? { ran: outcome.compileRan } : {}),
    ...(errors.length ? { errors } : {}),
    transitionsSeen: outcome.transitionsSeen,
    waitedMs: outcome.waitedMs,
  };
  const rest = baseBody(body);

  if (!outcome.reached) {
    const { body: waitBody } = describeWait(request, outcome);
    const error = waitBody.error as { code: string; message: string };
    return {
      body: failedBody(rest, compile, error.code, error,
        `${short(toolName)} ran, but its compile result could not be read: ${error.message}`,
        [...(waitBody.agentNextSteps as string[]), ...keptSteps(body, true)]),
      isError: true,
    };
  }

  if (c?.status === "compile_failed") {
    const count = c.errorCount;
    const first = errors[0] ? describeError(errors[0]) : null;
    const headline = `Compile failed: ${count} error(s) in generation ${generation}` + (first ? ` — first: ${first}` : "") + ".";
    const again = toolName === "unity_open_mcp_recompile_scripts"
      ? "then call recompile_scripts again (it imports new or deleted scripts and waits for the compile)"
      : "then call assets_refresh again (or recompile_scripts) — it waits for the compile and reports it";
    const step = `The compile failed with ${count} error(s) (generation ${generation})` + (first ? `; first: ${first}` : "") +
      `. Fix them, ${again}. unity_open_mcp_read_compile_errors has the full diagnostics.`;
    return {
      body: failedBody(rest, compile, "compile_failed",
        { code: "compile_failed", message: headline, errorCount: count, generation, errors },
        headline, [step, ...keptSteps(body, true)]),
      isError: true,
    };
  }

  const steps: string[] = [];
  let status: string;
  let headline: string;
  if (outcome.compileRan === false || outcome.compileDueNotStarted) {
    status = "no_compile_ran";
    headline = `No compile ran after generation ${request.afterGeneration ?? generation}` +
      (c?.status ? ` (the last compile is ${c.status.replace(/_/g, " ")})` : "") + ".";
    steps.push(...(describeWait(request, outcome).body.agentNextSteps as string[]));
  } else if (c?.status === "no_errors_found") {
    status = "compiled";
    headline = `Compiled clean: generation ${generation}, 0 errors.`;
    steps.push(headline);
  } else if (c?.status === "assembly_stale") {
    status = "compiled";
    headline = `Compiled (generation ${generation}), but script sources changed again after it started; the assemblies predate them.`;
    steps.push(`${headline} Call recompile_scripts again before relying on the new code.`);
  } else {
    status = "compile_unverified";
    headline = `The compile finished (generation ${generation}), but its result could not be verified (${c?.status ?? "no compile state"}).`;
    steps.push(`${headline} Check unity_open_mcp_read_compile_errors before relying on the new code.`);
  }
  return {
    body: { ...rest, status, headline, compile, agentNextSteps: [...steps, ...keptSteps(body, false)] },
    isError: false,
  };
}

/** A failed compile-type call: block-1 shape (mutation.success false, the detail in mutation.error). */
function failedBody(
  rest: Record<string, any>,
  compile: Record<string, unknown>,
  status: string,
  error: Record<string, unknown>,
  headline: string,
  steps: string[],
): Record<string, unknown> {
  const out: Record<string, unknown> = { ...rest, status, headline, compile, agentNextSteps: steps };
  if (rest.mutation && typeof rest.mutation === "object") out.mutation = { ...rest.mutation, success: false, error };
  else out.error = error;
  return out;
}

/**
 * The answer when the compile outlives the inline window: the wait continues
 * as a `jobs` job (an editor_status wait), whose result carries the compile
 * status and error count.
 */
export function foldCompileJob(
  body: Record<string, any>,
  request: WaitRequest,
  outcome: WaitOutcome,
  job: { job_id: string; state?: unknown; operation?: unknown },
  remainingMs: number,
): Record<string, unknown> {
  const rest = baseBody(body);
  const generation = outcome.compile?.generation ?? outcome.view?.compile?.generation ?? null;
  return {
    ...rest,
    status: "compiling",
    headline: `The compile is still running after ${outcome.waitedMs} ms; the wait continues as job ${job.job_id}.`,
    compile: {
      generationAtStart: request.afterGeneration ?? null,
      generation,
      status: outcome.compile?.status ?? null,
      holdingFor: outcome.holdingFor,
      transitionsSeen: outcome.transitionsSeen,
      waitedMs: outcome.waitedMs,
    },
    job_id: job.job_id,
    job: { state: job.state, operation: job.operation },
    agentNextSteps: [
      `Call unity_open_mcp_jobs {"action":"wait","job_id":"${job.job_id}","timeout_ms":30000} until the job is terminal ` +
        `(up to ${Math.round(remainingMs / 1000)} s more): its result is the compile's status, generation and error count ` +
        `(the same answer as ${waitCall("idle", request.afterGeneration)}).`,
      ...keptSteps(body, false),
    ],
  };
}
