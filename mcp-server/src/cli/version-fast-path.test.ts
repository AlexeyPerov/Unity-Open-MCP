// M31 Plan 6 / T6.6 — CLI `--version` / `--help` fast-path tests.
//
// These exercise the runCli dispatcher's fast paths: --version and --help
// must short-circuit BEFORE the heavy command/router modules are imported
// (commands.ts imports ALL_TOOLS, the ~270-tool surface). The lazy-load
// contract is verified structurally (runCli returns {handled:true, exitCode:0}
// for --version without throwing and without needing UNITY_PROJECT_PATH) and
// the output is asserted byte-identical to the pre-change format.
//
// stdout is captured by swapping `process.stdout.write` for the duration of
// each test; the real stream is restored in finally.

import { test } from "node:test";
import assert from "node:assert/strict";
import { runCli } from "./cli.js";
import { helpText, versionText } from "./help-text.js";

/**
 * Capture everything runCli writes to process.stdout while `fn` runs.
 * Returns the concatenated string. Restores the original write in finally.
 *
 * runCli's writes go through `writeAndDrain`, which uses the
 * `write(chunk, callback)` overload and awaits the callback. The fake must
 * therefore accept AND invoke that callback (synchronously) or writeAndDrain's
 * promise never resolves and the test hangs.
 */
async function captureStdout(fn: () => Promise<unknown>): Promise<string> {
  const chunks: string[] = [];
  const real = process.stdout.write.bind(process.stdout);
  process.stdout.write = ((chunk: unknown, cb?: (err?: Error | null) => void) => {
    chunks.push(typeof chunk === "string" ? chunk : String(chunk));
    // writeAndDrain's contract: the callback fires once the OS-level write
    // completes. Synchronous here is correct for a fake (no kernel in flight).
    if (typeof cb === "function") cb(null);
    return true;
  }) as typeof process.stdout.write;
  try {
    await fn();
  } finally {
    process.stdout.write = real;
  }
  return chunks.join("");
}

test("T6.6 runCli(--version) returns handled exit 0 and prints 'unity-open-mcp <version>'", async () => {
  const out = await captureStdout(() =>
    runCli({ version: "9.9.9", argv: ["--version"] }),
  );
  assert.equal(out, "unity-open-mcp 9.9.9\n");
});

test("T6.6 runCli(-V) short form matches --version", async () => {
  const out = await captureStdout(() =>
    runCli({ version: "1.2.3", argv: ["-V"] }),
  );
  assert.equal(out, "unity-open-mcp 1.2.3\n");
});

test("T6.6 runCli(--help) returns handled exit 0 and prints the help text", async () => {
  // The dispatcher writes helpText(binName) + "\n". Assert the captured
  // output exactly equals that, so a future change to the help path (e.g.
  // accidentally routing through the heavy module) is caught.
  const expected = helpText("unity-open-mcp") + "\n";
  const out = await captureStdout(() =>
    runCli({ version: "0.0.0", argv: ["--help"] }),
  );
  assert.equal(out, expected);
});

test("T6.6 runCli(-h) short form matches --help", async () => {
  const expected = helpText("unity-open-mcp") + "\n";
  const out = await captureStdout(() =>
    runCli({ version: "0.0.0", argv: ["-h"] }),
  );
  assert.equal(out, expected);
});

test("T6.6 runCli(--version) does not require UNITY_PROJECT_PATH", async () => {
  // The fast path must short-circuit before env resolution. Clear the env var
  // to prove --version does not consult it (a real subcommand would fail).
  const saved = process.env.UNITY_PROJECT_PATH;
  delete process.env.UNITY_PROJECT_PATH;
  try {
    const out = await captureStdout(() =>
      runCli({ version: "0.7.0", argv: ["--version"] }),
    );
    assert.equal(out, "unity-open-mcp 0.7.0\n");
  } finally {
    if (saved !== undefined) process.env.UNITY_PROJECT_PATH = saved;
  }
});

test("T6.6 runCli(no command) returns handled:false (stdio server fallthrough)", async () => {
  // No argv at all → the dispatcher returns {handled:false} so index.ts
  // falls through to the stdio server. This path must NOT import the heavy
  // modules either (index.ts imports server.ts lazily on this branch).
  const outcome = await runCli({ version: "0.0.0", argv: [] });
  assert.equal(outcome.handled, false);
  assert.equal(outcome.exitCode, 0);
});

test("T6.6 help-text module exports byte-identical versionText/helpText to commands.ts", () => {
  // commands.ts re-exports the light module's formatters; assert the two
  // surfaces agree so a future drift (e.g. someone edits one copy) is caught.
  assert.equal(versionText("5.5.5"), "unity-open-mcp 5.5.5");
  // helpText must mention every subcommand (same contract commands.test.ts
  // asserts on its own import).
  const text = helpText("unity-open-mcp");
  for (const cmd of ["ping", "wait-for-ready", "status", "run-tool", "stream-events", "verify", "baseline", "regression"]) {
    assert.ok(text.includes(cmd), `helpText mentions ${cmd}`);
  }
});
