// Tests for compile-verify failure-code detection. Pure decision-function
// checks over before/after snapshots — no I/O.

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  detectCompileVerify,
  buildCompileVerifyAnnotation,
  COMPILE_VERIFY_RECOMMENDATIONS,
  type CompileVerifySnapshot,
} from "./compile-verify.js";

const T0 = 1_700_000_000_000;
const T1 = T0 + 5_000;
const T2 = T0 + 10_000;

function snap(
  count: number | undefined,
  dll: number | undefined,
): CompileVerifySnapshot {
  return { bridgeToolCount: count, dllMtimeMs: dll };
}

test("clean compile (count up, dll advanced) → null", () => {
  const r = detectCompileVerify({
    before: snap(200, T0),
    after: snap(202, T1),
    sourceMtimeMs: T0,
  });
  assert.equal(r.code, null);
  assert.equal(r.recommendation, null);
});

test("dll advanced past source → null (even if count unchanged)", () => {
  // A compile that picked up the edit but didn't change the tool count (e.g.
  // a pure-logic edit to an existing tool) is a SUCCESS, not a no-op.
  const r = detectCompileVerify({
    before: snap(200, T0),
    after: snap(200, T2), // DLL advanced
    sourceMtimeMs: T1, // source edited between T0 and the compile
  });
  assert.equal(r.code, null);
});

test("dll older than source edit → dll_stale", () => {
  const r = detectCompileVerify({
    before: snap(200, T0),
    after: snap(200, T0), // DLL did not move
    sourceMtimeMs: T1, // but source is newer than the DLL
  });
  assert.equal(r.code, "dll_stale");
  assert.equal(
    r.recommendation,
    COMPILE_VERIFY_RECOMMENDATIONS.dll_stale,
  );
});

test("count unchanged + dll did not advance + no source mtime → compile_noop", () => {
  const r = detectCompileVerify({
    before: snap(200, T0),
    after: snap(200, T0),
    // no sourceMtimeMs — the generic no-op path should still fire
  });
  assert.equal(r.code, "compile_noop");
  assert.equal(
    r.recommendation,
    COMPILE_VERIFY_RECOMMENDATIONS.compile_noop,
  );
});

test("dll_stale takes priority over compile_noop when both apply", () => {
  // DLL older than source AND count unchanged — dll_stale is the more
  // actionable diagnosis (names the specific edit that didn't land).
  const r = detectCompileVerify({
    before: snap(200, T0),
    after: snap(200, T0),
    sourceMtimeMs: T1,
  });
  assert.equal(r.code, "dll_stale");
});

test("count changed but dll stale vs source → dll_stale", () => {
  // Even if the registry count changed, a DLL older than the source edit means
  // the edit itself was not compiled in (count change may be unrelated).
  const r = detectCompileVerify({
    before: snap(200, T0),
    after: snap(205, T0), // count up but DLL unmoved
    sourceMtimeMs: T1,
  });
  assert.equal(r.code, "dll_stale");
});

test("partial snapshots: only counts available, unchanged → compile_noop", () => {
  const r = detectCompileVerify({
    before: snap(200, undefined),
    after: snap(200, undefined),
  });
  // Without DLL mtimes the dllDidNotAdvance clause is false, so this should
  // NOT fire compile_noop (we lack the second confirming signal).
  assert.equal(r.code, null);
});

test("partial snapshots: only dll available, advanced → null", () => {
  const r = detectCompileVerify({
    before: snap(undefined, T0),
    after: snap(undefined, T1),
  });
  assert.equal(r.code, null);
});

test("partial snapshots: nothing available → null (no false positive)", () => {
  const r = detectCompileVerify({
    before: snap(undefined, undefined),
    after: snap(undefined, undefined),
    sourceMtimeMs: T1,
  });
  assert.equal(r.code, null);
});

test("buildCompileVerifyAnnotation: null result → null annotation", () => {
  assert.equal(
    buildCompileVerifyAnnotation({ code: null, recommendation: null }),
    null,
  );
});

test("buildCompileVerifyAnnotation: flagged result → {code, recommendation}", () => {
  const ann = buildCompileVerifyAnnotation({
    code: "compile_noop",
    recommendation: COMPILE_VERIFY_RECOMMENDATIONS.compile_noop,
  });
  assert.deepEqual(ann, {
    code: "compile_noop",
    recommendation: COMPILE_VERIFY_RECOMMENDATIONS.compile_noop,
  });
});

test("recommendation strings contain no internal IDs/specs paths", () => {
  // AGENTS.md §"No internal references in user-visible surfaces" — these
  // strings ship to agents; they must not mention specs/, milestone IDs, or
  // reference-project handles.
  for (const code of ["compile_noop", "dll_stale"] as const) {
    const s = COMPILE_VERIFY_RECOMMENDATIONS[code];
    assert.ok(!/specs?\//.test(s), `${code} mentions specs path`);
    assert.ok(!/M\d/.test(s), `${code} mentions a milestone ID`);
    assert.ok(
      !/ankle|coplay|ucp/i.test(s),
      `${code} mentions a reference project`,
    );
  }
});
