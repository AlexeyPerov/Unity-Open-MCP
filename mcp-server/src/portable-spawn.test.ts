// End-to-end check that a portable spawn needs no UNITY_PROJECT_PATH.
//
// Starts the built stdio server (`dist/index.js`) exactly the way a committed
// MCP client config would — `--project-from-cwd`, optionally with
// `--unity-subpath`, and a cwd pointing at a throwaway Unity tree — then reads
// the resolution line the bootstrap writes to stderr. No Unity Editor and no
// bridge are involved: the assertion is only that the server resolves an
// absolute project root and boots.

import { test, type TestContext } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, mkdir, realpath, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

// dist-test/portable-spawn.test.js -> mcp-server/dist/index.js
const SERVER_ENTRY = join(
  dirname(fileURLToPath(import.meta.url)),
  "..",
  "dist",
  "index.js",
);

const RESOLVED_LINE = /\[unity-open-mcp\] Unity project resolved to (.+) \(source: (.+)\)/;

async function unityTree(t: TestContext, subpath: string): Promise<string> {
  // realpath: macOS hands out /var/... symlinks for tmpdir, and the server
  // reports the resolved path.
  const root = await realpath(await mkdtemp(join(tmpdir(), "unity-open-mcp-spawn-")));
  t.after(() => rm(root, { recursive: true, force: true }));
  for (const marker of ["Assets", "Packages", "ProjectSettings"]) {
    await mkdir(join(root, subpath, marker), { recursive: true });
  }
  return root;
}

interface SpawnOutcome {
  stderr: string;
  exitCode: number | null;
}

/**
 * Run the stdio server until it prints the resolution line (then kill it) or
 * until it exits on its own (a rejected configuration).
 */
function runServer(
  args: string[],
  cwd: string,
  overrides: Record<string, string | undefined>,
): Promise<SpawnOutcome> {
  const env: NodeJS.ProcessEnv = { ...process.env };
  for (const [key, value] of Object.entries(overrides)) {
    if (value === undefined) delete env[key];
    else env[key] = value;
  }
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [SERVER_ENTRY, ...args], {
      cwd,
      env,
      stdio: ["pipe", "pipe", "pipe"],
    });
    let stderr = "";
    const timer = setTimeout(() => {
      child.kill("SIGKILL");
      reject(new Error(`server did not settle in time; stderr:\n${stderr}`));
    }, 20_000);
    child.stderr.setEncoding("utf8");
    child.stderr.on("data", (chunk: string) => {
      stderr += chunk;
      // The bootstrap logs the auth-token line last; by then the project path
      // is resolved and the server is connecting its transport.
      if (stderr.includes("auth token")) child.kill("SIGTERM");
    });
    child.on("error", (err) => {
      clearTimeout(timer);
      reject(err);
    });
    child.on("close", (code) => {
      clearTimeout(timer);
      resolve({ stderr, exitCode: code });
    });
  });
}

/** Env with UNITY_PROJECT_PATH removed, so only the flags can resolve a path. */
const NO_PROJECT_ENV: Record<string, string | undefined> = {
  UNITY_PROJECT_PATH: undefined,
};

test("stdio spawn with --project-from-cwd resolves an absolute path with no env var", async (t) => {
  const root = await unityTree(t, ".");
  const outcome = await runServer(["--project-from-cwd"], root, NO_PROJECT_ENV);
  const match = outcome.stderr.match(RESOLVED_LINE);
  assert.ok(match, `expected a resolution line, got:\n${outcome.stderr}`);
  assert.equal(match[1], root);
  assert.equal(match[2], "cwd");
  assert.match(outcome.stderr, /Bridge port resolved to \d+/);
});

test("stdio spawn with --unity-subpath resolves the monorepo layout", async (t) => {
  const root = await unityTree(t, "Client");
  const outcome = await runServer(
    ["--project-from-cwd", "--unity-subpath", "Client"],
    root,
    NO_PROJECT_ENV,
  );
  const match = outcome.stderr.match(RESOLVED_LINE);
  assert.ok(match, `expected a resolution line, got:\n${outcome.stderr}`);
  assert.equal(match[1], join(root, "Client"));
  assert.equal(match[2], "cwd+subpath");
});

test("a relative UNITY_PROJECT_PATH resolves against the spawn cwd", async (t) => {
  const root = await unityTree(t, "Client");
  const outcome = await runServer([], root, { UNITY_PROJECT_PATH: "Client" });
  const match = outcome.stderr.match(RESOLVED_LINE);
  assert.ok(match, `expected a resolution line, got:\n${outcome.stderr}`);
  assert.equal(match[1], join(root, "Client"));
  assert.equal(match[2], "env");
});

test("--project-from-cwd on a workspace whose Unity project is a subfolder fails loudly", async (t) => {
  const root = await unityTree(t, "Client");
  const outcome = await runServer(["--project-from-cwd"], root, NO_PROJECT_ENV);
  assert.equal(outcome.exitCode, 1);
  assert.match(outcome.stderr, /is not a Unity project root/);
  assert.match(outcome.stderr, /--unity-subpath/);
});

test("no project path at all exits with the actionable message", async (t) => {
  const root = await unityTree(t, ".");
  const outcome = await runServer([], root, NO_PROJECT_ENV);
  assert.equal(outcome.exitCode, 1);
  assert.match(outcome.stderr, /No Unity project path/);
  assert.match(outcome.stderr, /--project-from-cwd/);
});
