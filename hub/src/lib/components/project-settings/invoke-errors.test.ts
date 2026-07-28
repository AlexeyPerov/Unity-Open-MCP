import { test } from "node:test";
import assert from "node:assert/strict";

import { describeInvokeError } from "./invoke-errors.ts";

// A14: Tauri `invoke` rejections arrive as the backend's serialized error
// enum (a plain object), so a `catch (e)` that does
// `e instanceof Error ? e.message : String(e)` renders the rejection as
// "[object Object]". The helper must surface the real reason for each
// shape a rejection can take.

test("describeInvokeError returns the message of a plain Error", () => {
  assert.equal(describeInvokeError(new Error("boom")), "boom");
});

test("describeInvokeError returns a raw string unchanged", () => {
  assert.equal(describeInvokeError("network down"), "network down");
});

test("describeInvokeError extracts .message from a serde-tagged object", () => {
  // The common Tauri shape: a serialized Rust enum variant with a `message`
  // field (e.g. ManifestError::ParseFailed, MigrateError::SourceOverlapsPackage).
  const err = { type: "parseFailed", message: "unexpected token at line 12" };
  assert.equal(describeInvokeError(err), "unexpected token at line 12");
});

test("describeInvokeError falls back to the .type tag when .message is absent", () => {
  // Serde-flattened variants that only carry a `type` tag (no message field).
  const err = { type: "alreadyRunning" };
  assert.equal(describeInvokeError(err), "alreadyRunning");
});

test("describeInvokeError ignores a non-string .message", () => {
  const err = { type: "weird", message: 42 };
  assert.equal(describeInvokeError(err), "weird");
});

test("describeInvokeError ignores an empty .message and falls back to .type", () => {
  const err = { type: "persistFailed", message: "" };
  assert.equal(describeInvokeError(err), "persistFailed");
});

test("describeInvokeError falls back to String(e) for an unrecognized object", () => {
  const err = { code: 500, detail: "internal" };
  assert.equal(describeInvokeError(err), String(err));
});

test("describeInvokeError handles null / undefined / number primitives", () => {
  assert.equal(describeInvokeError(null), "null");
  assert.equal(describeInvokeError(undefined), "undefined");
  assert.equal(describeInvokeError(42), "42");
});
