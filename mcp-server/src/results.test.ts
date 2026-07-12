// Tests for the shared makeErrorResult factory.
//
// This factory consolidated three near-identical private helpers (batch-spawn,
// compressible-router, live-client) that had divergent positional signatures.
// These tests pin the on-wire shape so any future refactor preserves what
// downstream parsers and the existing per-router tests assert on
// (`body.error.code`, `body.error.message`, `result.isError`).

import test from "node:test";
import assert from "node:assert/strict";
import { makeErrorResult } from "./results.js";

function body(result: ReturnType<typeof makeErrorResult>): Record<string, unknown> {
  assert.equal(result.content.length, 1);
  assert.equal(result.content[0].type, "text");
  return JSON.parse(result.content[0].text);
}

test("makeErrorResult: emits { error: { code, message } } by default", () => {
  const result = makeErrorResult({ code: "bridge_error", message: "boom" });
  assert.deepEqual(body(result), { error: { code: "bridge_error", message: "boom" } });
  assert.equal(result.isError, true);
});

test("makeErrorResult: detail replaces the error envelope when supplied", () => {
  // Mirrors the live-client pattern where a richer body (with nested error)
  // overrides the default envelope. The code/message args are documentation
  // only here — detail wins.
  const result = makeErrorResult({
    code: "bridge_offline",
    message: "discarded",
    detail: {
      error: { code: "bridge_offline", message: "Cannot connect to bridge at http://127.0.0.1:22028" },
    },
  });
  assert.deepEqual(body(result), {
    error: { code: "bridge_offline", message: "Cannot connect to bridge at http://127.0.0.1:22028" },
  });
  assert.equal(result.isError, true);
});

test("makeErrorResult: detail can be any structured payload", () => {
  // batch-spawn's contract: detail replaces the whole body.
  const result = makeErrorResult({
    code: "batch_spawn_failed",
    message: "discarded",
    detail: { exitCode: 1, stdout: "..." },
  });
  assert.deepEqual(body(result), { exitCode: 1, stdout: "..." });
});

test("makeErrorResult: null detail falls back to the default envelope", () => {
  // `detail ?? default` — explicit null is treated as absent, matching the
  // original `detail ?? { error: ... }` semantics in all three helpers.
  const result = makeErrorResult({ code: "c", message: "m", detail: null });
  assert.deepEqual(body(result), { error: { code: "c", message: "m" } });
});

test("makeErrorResult: preserves arbitrary code strings used across routers", () => {
  // Spot-check the codes the migrated call sites emit, to lock the contract.
  for (const code of [
    "batch_not_supported",
    "unknown_batch_tool",
    "project_path_missing",
    "batch_spawn_failed",
    "unity_not_discovered",
    "unity_spawn_refused",
    "unity_path_invalid",
    "unity_path_not_found",
    "missing_parameter",
    "source_unavailable",
    "bridge_error",
    "bridge_offline",
    "bridge_http_error",
    "bridge_not_connected",
    "compile_timeout",
    "test_results_timeout",
  ]) {
    const result = makeErrorResult({ code, message: "x" });
    assert.equal((body(result).error as { code: string }).code, code);
    assert.equal(result.isError, true);
  }
});
