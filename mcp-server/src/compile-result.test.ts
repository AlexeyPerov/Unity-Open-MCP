import test from "node:test";
import assert from "node:assert/strict";
import { compileToAwait, foldCompileResult } from "./compile-result.js";
import type { WaitOutcome, WaitRequest } from "./editor-state.js";

const RECOMPILE = "unity_open_mcp_recompile_scripts";
const REFRESH = "unity_open_mcp_assets_refresh";

function envelope(output: Record<string, unknown>, compile: Record<string, unknown>, extra: Record<string, unknown> = {}) {
  return {
    mutation: { success: true, output, error: null },
    gate: { outcome: "passed" },
    compile,
    agentNextSteps: ["Gate passed — no new issues detected.", "Wait for it with unity_open_mcp_editor_status {\"wait_until\":\"idle\",\"after_generation\":5}."],
    ...extra,
  };
}

function outcome(partial: Partial<WaitOutcome>): WaitOutcome {
  return {
    reached: true, reason: "reached", waitedMs: 1200, view: null, compile: null,
    transitionsSeen: ["compiling", "reloading"], compileRan: true, compileDueNotStarted: false, holdingFor: null,
    ...partial,
  };
}

const REQUEST: WaitRequest = { until: "idle", afterGeneration: 5, timeoutMs: 600_000 };

test("compileToAwait: what a compile-type call left to wait for", () => {
  assert.deepEqual(compileToAwait(RECOMPILE, envelope({ requested: true }, { generationAtStart: 5, pending: true }), false), { generationAtStart: 5 });
  assert.deepEqual(compileToAwait(RECOMPILE, { status: "triggered_reload", compile: { generationAtStart: 8 } }, false), { generationAtStart: 8 });
  assert.equal(compileToAwait(RECOMPILE, { error: { code: "scene_dirty" } }, true), null, "a refused call is answered as is");
  assert.equal(compileToAwait(RECOMPILE, { mutation: { success: false, error: { code: "compile_request_failed" } } }, true), null);
  // assets_refresh only when the refresh started (or left) a compile.
  assert.equal(compileToAwait(REFRESH, envelope({ refreshed: true, compileDue: false, isCompiling: false }, { generationAtStart: 4, generation: 4, pending: false }), false), null);
  assert.deepEqual(compileToAwait(REFRESH, envelope({ refreshed: true, compileDue: true }, { generationAtStart: 4, generation: 4, pending: true }), false), { generationAtStart: 4 });
  assert.deepEqual(compileToAwait(REFRESH, envelope({ refreshed: true }, { generationAtStart: 4, generation: 5, pending: false }), false), { generationAtStart: 4 },
    "a compile that already ran during the call counts");
});

test("foldCompileResult: a clean compile is the call's status", () => {
  const body = { ...envelope({ requested: true }, { generationAtStart: 5, pending: true }), _compileVerify: { code: "compile_noop" }, compilePending: true };
  const folded = foldCompileResult(RECOMPILE, body, REQUEST,
    outcome({ compile: { status: "no_errors_found", generation: 6, errorCount: 0, sourceMatches: true } }));
  assert.equal(folded.isError, false);
  assert.equal(folded.body.status, "compiled");
  assert.deepEqual((folded.body.compile as Record<string, unknown>).generation, 6);
  assert.equal(folded.body._compileVerify, undefined, "the pre-compile snapshot is dropped");
  assert.equal(folded.body.compilePending, undefined);
  const steps = folded.body.agentNextSteps as string[];
  assert.match(steps[0], /Compiled clean: generation 6/);
  assert.ok(steps.includes("Gate passed — no new issues detected."));
  assert.ok(!steps.some(s => s.includes("wait_until")));
});

test("foldCompileResult: a failed compile fails the call in the refusal shape", () => {
  const folded = foldCompileResult(RECOMPILE, envelope({ requested: true }, { generationAtStart: 5 }), REQUEST,
    outcome({ compile: { status: "compile_failed", generation: 6, errorCount: 2, errors: [{ file: "Assets/A.cs", line: 3, column: 1, code: "CS0246", message: "missing type" }] } }));
  assert.equal(folded.isError, true);
  assert.equal(folded.body.status, "compile_failed");
  const mutation = folded.body.mutation as Record<string, any>;
  assert.equal(mutation.success, false);
  assert.equal(mutation.error.code, "compile_failed");
  assert.equal(mutation.error.errorCount, 2);
  assert.match(String(folded.body.headline), /Compile failed: 2 error\(s\) in generation 6 — first: Assets\/A\.cs\(3,1\): CS0246 missing type/);
  assert.ok(!(folded.body.agentNextSteps as string[]).some(s => s.startsWith("Gate passed")));
});

test("foldCompileResult: no compile after the call's generation, and a bridge that never came back", () => {
  const none = foldCompileResult(REFRESH, envelope({ refreshed: true, compileDue: true }, { generationAtStart: 5 }), REQUEST,
    outcome({ compileRan: false, compile: { status: "no_errors_found", generation: 5, errorCount: 0 } }));
  assert.equal(none.isError, false);
  assert.equal(none.body.status, "no_compile_ran");

  const dead = foldCompileResult(RECOMPILE, { status: "triggered_reload", compile: { generationAtStart: 5 }, message: "cut" }, REQUEST,
    outcome({ reached: false, reason: "dead_bridge", compileRan: null }));
  assert.equal(dead.isError, true);
  assert.equal(dead.body.status, "bridge_compile_failed");
  assert.equal((dead.body.error as Record<string, unknown>).code, "bridge_compile_failed", "no mutation envelope: the error is top-level");
  assert.match((dead.body.agentNextSteps as string[])[0], /read_compile_errors/);
});
