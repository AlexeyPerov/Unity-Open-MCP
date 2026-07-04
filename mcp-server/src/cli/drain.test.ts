// Tests for the CLI stdout drain helper (src/cli/cli.ts writeAndDrain).
//
// writeAndDrain exists because `process.stdout.write` is asynchronous when the
// destination is a pipe. The previous implementation awaited Node's internal
// 'drain' event, but 'drain' only signals the internal buffer dropped back
// below the high-water mark — libuv can still have the write queued at the
// kernel level, so a subsequent `process.exit()` truncated large payloads
// (~64 KB on macOS). The fix uses the `write(chunk, callback)` overload whose
// callback fires only after libuv completes the actual OS write.
//
// The helper is exercised with a small fake DrainableWritable that models the
// callback semantics: (1) synchronous-completion write → callback invoked on
// the same tick, (2) deferred write → callback invoked only when `flush()` is
// called, mirroring an in-flight kernel write.

import { test } from "node:test";
import assert from "node:assert/strict";
import { writeAndDrain } from "./cli.js";

/**
 * Minimal fake writable. The callback passed to `write(chunk, cb)` is held
 * until `flush()` is called, modelling a pending OS-level write. `flush(true)`
 * models a write error.
 */
function fakeWritable(): {
  writes: string[];
  stream: {
    write(chunk: string, cb: (err?: Error | null) => void): boolean;
    write(chunk: string): boolean;
    once(event: "drain", cb: () => void): unknown;
  };
  flush(err?: Error | null): void;
} {
  const writes: string[] = [];
  let pendingCb: ((err?: Error | null) => void) | null = null;
  return {
    writes,
    stream: {
      write(chunk: string, cb?: (err?: Error | null) => void): boolean {
        writes.push(chunk);
        // Hold the callback to model an in-flight kernel write. If no callback
        // was supplied (the no-callback overload), behave like a synchronous
        // stream and return true.
        if (cb) pendingCb = cb;
        return true;
      },
      once(_event: "drain", _cb: () => void): unknown {
        return this;
      },
    },
    flush(err?: Error | null): void {
      if (pendingCb) {
        const cb = pendingCb;
        pendingCb = null;
        cb(err);
      }
    },
  };
}

test("writeAndDrain: resolves once the write callback fires (in-flight write completes)", async () => {
  const fake = fakeWritable();
  let resolved = false;
  const p = writeAndDrain(fake.stream, "hello");
  p.then(() => {
    resolved = true;
  });
  // The chunk is recorded synchronously, but the promise must NOT resolve
  // until the OS-level write callback fires.
  assert.deepEqual(fake.writes, ["hello"]);
  await Promise.resolve();
  assert.equal(resolved, false, "must not resolve before the write callback");

  fake.flush();
  await p;
  assert.equal(resolved, true, "resolves once the write callback fires");
});

test("writeAndDrain: rejects when the write callback errors", async () => {
  const fake = fakeWritable();
  const p = writeAndDrain(fake.stream, "boom");
  await Promise.resolve();
  fake.flush(new Error("EPIPE"));
  await assert.rejects(p, /EPIPE/);
});

test("writeAndDrain: a large payload is delivered in full before resolving", async () => {
  const fake = fakeWritable();
  // ~128 KB payload — comfortably past the macOS pipe buffer size that
  // motivated the fix. Only one write() is issued; the contract is that the
  // whole chunk reaches the kernel intact.
  const payload = "x".repeat(128 * 1024);
  const p = writeAndDrain(fake.stream, payload);
  await Promise.resolve();
  assert.equal(fake.writes.length, 1);
  assert.equal(fake.writes[0].length, 128 * 1024);
  fake.flush();
  await p;
});
