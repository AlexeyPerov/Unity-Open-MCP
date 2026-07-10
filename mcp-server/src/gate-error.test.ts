// Tests for gate-error.ts deriveIsError(). Pure function — no I/O. The
// headline regression guard is the historical TypeError misclassification:
// a malformed/partial body (missing `mutation`) must return false, not throw.
// See the inline comment in gate-error.ts (specs/feedback.md entry 2026-07-02-b).

import { test } from "node:test";
import assert from "node:assert/strict";
import { deriveIsError, type MutationEnvelope } from "./gate-error.js";

// Helper: build a well-formed success envelope (the common shape). Each test
// then mutates one field to exercise a branch.
function okEnvelope(): MutationEnvelope {
  return {
    mutation: { success: true, output: {}, error: null },
    gate: { mode: "warn", skipped: false, validation: null, delta: null },
    agentNextSteps: [],
  };
}

// ---------------------------------------------------------------------------
// The historical bug: missing/malformed `mutation` must NOT throw.
// ---------------------------------------------------------------------------

test("deriveIsError(null) returns false (historical TypeError regression)", () => {
  // Previously a null envelope threw TypeError on the `mutation` access,
  // which postTool's catch misclassified as a connection failure. The guard
  // must short-circuit to false instead.
  assert.equal(deriveIsError(null as unknown as MutationEnvelope), false);
});

test("deriveIsError(undefined) returns false", () => {
  assert.equal(deriveIsError(undefined as unknown as MutationEnvelope), false);
});

test("deriveIsError({}) (missing mutation field) returns false, does not throw", () => {
  // This is the exact shape the inline comment describes: a body that passed
  // an outer shape check but has no `mutation` object. Accessing
  // `.mutation.success` on a missing field would throw; the guard prevents it.
  assert.equal(deriveIsError({} as MutationEnvelope), false);
});

test("deriveIsError({ mutation: null }) returns false", () => {
  assert.equal(
    deriveIsError({ mutation: null } as unknown as MutationEnvelope),
    false,
  );
});

test("deriveIsError({ mutation: 'not-an-object' }) returns false", () => {
  // typeof check rejects string-typed mutation.
  assert.equal(
    deriveIsError({
      mutation: "boom",
    } as unknown as MutationEnvelope),
    false,
  );
});

test("deriveIsError does not throw on any malformed input (defensive contract)", () => {
  // A sampling of adversarial inputs — none must throw; all must return false.
  const malformed: unknown[] = [
    null,
    undefined,
    0,
    "",
    false,
    [],
    { mutation: undefined },
    { mutation: 42 },
    { mutation: "" },
    { mutation: { success: null } },
  ];
  for (const input of malformed) {
    assert.doesNotThrow(() => deriveIsError(input as MutationEnvelope));
    assert.equal(deriveIsError(input as MutationEnvelope), false);
  }
});

// ---------------------------------------------------------------------------
// mutation.success === false → error (regardless of gate)
// ---------------------------------------------------------------------------

test("mutation.success=false is an error", () => {
  const env = okEnvelope();
  env.mutation.success = false;
  assert.equal(deriveIsError(env), true);
});

test("mutation.success=false is an error even when gate is off / skipped", () => {
  const env = okEnvelope();
  env.mutation.success = false;
  env.gate.mode = "off";
  env.gate.skipped = true;
  assert.equal(deriveIsError(env), true);
});

test("mutation.success=true with a non-enforcing gate is NOT an error", () => {
  assert.equal(deriveIsError(okEnvelope()), false);
});

// ---------------------------------------------------------------------------
// Gate enforce + delta.newErrors > 0 → error
// ---------------------------------------------------------------------------

test("enforce gate with delta.newErrors > 0 is an error", () => {
  const env = okEnvelope();
  env.gate.mode = "enforce";
  env.gate.delta = { newErrors: 3 };
  assert.equal(deriveIsError(env), true);
});

test("enforce gate with delta.newErrors === 0 is NOT an error", () => {
  const env = okEnvelope();
  env.gate.mode = "enforce";
  env.gate.delta = { newErrors: 0 };
  assert.equal(deriveIsError(env), false);
});

test("enforce gate with delta.newErrors negative is NOT an error (count can't go below 0)", () => {
  // A negative delta (e.g. -1 from a fix applying) should not be treated as
  // an error — only a positive count means new problems appeared.
  const env = okEnvelope();
  env.gate.mode = "enforce";
  env.gate.delta = { newErrors: -1 };
  assert.equal(deriveIsError(env), false);
});

test("enforce gate with delta.newErrors not a number is NOT an error", () => {
  const env = okEnvelope();
  env.gate.mode = "enforce";
  env.gate.delta = { newErrors: "three" as unknown as number };
  assert.equal(deriveIsError(env), false);
});

test("enforce gate with missing delta is NOT an error", () => {
  const env = okEnvelope();
  env.gate.mode = "enforce";
  env.gate.delta = null;
  assert.equal(deriveIsError(env), false);
});

test("non-enforce gate with positive newErrors is NOT an error (gate mode matters)", () => {
  // warn/off gates report but do not error-ify a success mutation.
  for (const mode of ["warn", "off"] as const) {
    const env = okEnvelope();
    env.gate.mode = mode;
    env.gate.delta = { newErrors: 5 };
    assert.equal(deriveIsError(env), false, `mode=${mode}`);
  }
});
