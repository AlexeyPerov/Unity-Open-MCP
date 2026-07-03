// Tests for the CLI stdout drain helper (src/cli/cli.ts writeAndDrain).
//
// writeAndDrain exists because `process.stdout.write` is asynchronous when the
// destination is a pipe: it returns false once the internal buffer exceeds the
// high-water mark and the data is held until the next 'drain' event. An
// immediate `process.exit` after a large `run-tool` write truncated the output
// at the pipe buffer size (64 KB on macOS). writeAndDrain awaits the drain so
// emitResult -> process.exit can no longer cut the JSON off mid-stream.
//
// The helper is exercised with a small fake DrainableWritable so no real pipe
// or large payload is needed: (1) write returns true → resolves immediately,
// (2) write returns false → resolves only after 'drain' fires.

import { test } from "node:test";
import assert from "node:assert/strict";
import { writeAndDrain } from "./cli.js";

/** Minimal fake writable. Emits 'drain' on the next tick when armed. */
function fakeWritable(returnsFalse: boolean): {
  writes: string[];
  stream: { write(chunk: string): boolean; once(event: "drain", cb: () => void): unknown };
  drain(): void;
} {
  const writes: string[] = [];
  let drainCb: (() => void) | null = null;
  return {
    writes,
    stream: {
      write(chunk: string): boolean {
        writes.push(chunk);
        return !returnsFalse;
      },
      once(_event: "drain", cb: () => void): unknown {
        drainCb = cb;
        return this;
      },
    },
    drain(): void {
      if (drainCb) drainCb();
    },
  };
}

test("writeAndDrain: resolves immediately when write returns true (no backpressure)", async () => {
  const fake = fakeWritable(false);
  await writeAndDrain(fake.stream, "hello");
  assert.deepEqual(fake.writes, ["hello"]);
});

test("writeAndDrain: waits for the drain event when write returns false (backpressure)", async () => {
  const fake = fakeWritable(true);
  let resolved = false;
  const p = writeAndDrain(fake.stream, "big payload");
  p.then(() => {
    resolved = true;
  });
  // The chunk is recorded synchronously, but the promise must NOT resolve until
  // 'drain' fires.
  assert.deepEqual(fake.writes, ["big payload"]);
  await Promise.resolve();
  assert.equal(resolved, false, "must not resolve before the drain event");

  fake.drain();
  await p;
  assert.equal(resolved, true, "resolves once 'drain' fires");
});
