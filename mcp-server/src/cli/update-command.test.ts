import assert from "node:assert/strict";
import { test } from "node:test";

import {
  UPDATE_EXIT,
  compareSemver,
  runUpdateCommand,
  type ProcessResult,
} from "./update-command.js";

function response(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

function npmLatest(version: string): typeof fetch {
  return (async (input: string | URL | Request) => {
    assert.match(String(input), /registry\.npmjs\.org/);
    return response({ version });
  }) as typeof fetch;
}

const okProcess = async (): Promise<ProcessResult> => ({
  exitCode: 0,
  stdout: "",
  stderr: "",
});

/** package.json reader that declares unity-open-mcp only under `projectDir`. */
function manifestAt(projectDir: string): (path: string) => Promise<string> {
  return async (path) => {
    if (path === `${projectDir}/package.json`) {
      return JSON.stringify({ dependencies: { "unity-open-mcp": "^1.2.3" } });
    }
    throw Object.assign(new Error(`ENOENT: ${path}`), { code: "ENOENT" });
  };
}

test("compareSemver handles stable, prerelease, and build metadata", () => {
  assert.equal(compareSemver("1.3.0", "1.2.9"), 1);
  assert.equal(compareSemver("1.2.3", "1.2.3+build.4"), 0);
  assert.equal(compareSemver("1.2.3", "1.2.3-rc.2"), 1);
  assert.equal(compareSemver("1.2.3-rc.10", "1.2.3-rc.2"), 1);
  assert.equal(compareSemver("not-a-version", "1.2.3"), null);
});

test("update --check exits 10 when npm reports a newer version", async () => {
  const result = await runUpdateCommand({
    currentVersion: "1.2.3",
    check: true,
    json: true,
    dependencies: { fetch: npmLatest("1.3.0"), runProcess: okProcess },
  });
  assert.equal(result.exitCode, UPDATE_EXIT.AVAILABLE);
  assert.deepEqual(result.json, {
    command: "update",
    check: true,
    currentVersion: "1.2.3",
    latestVersion: "1.3.0",
    source: "npm",
    status: "update_available",
    updateAvailable: true,
    fallbackReason: undefined,
  });
});

test("update --check exits 0 when installed version is current or newer", async () => {
  for (const currentVersion of ["1.2.3", "1.3.0"]) {
    const result = await runUpdateCommand({
      currentVersion,
      check: true,
      json: false,
      dependencies: { fetch: npmLatest("1.2.3"), runProcess: okProcess },
    });
    assert.equal(result.exitCode, 0);
    assert.match(result.human, /up to date/);
  }
});

test("update without --check still explains the Unity package boundary when current", async () => {
  const result = await runUpdateCommand({
    currentVersion: "1.2.3",
    check: false,
    json: false,
    dependencies: { fetch: npmLatest("1.2.3"), runProcess: okProcess },
  });
  assert.equal(result.exitCode, 0);
  assert.match(result.human, /updates only the MCP server/);
  assert.match(result.human, /both Unity packages and client pins/);
});

test("version lookup falls back from npm to the newest stable trio GitHub tag", async () => {
  const calls: string[] = [];
  const fetchImpl = (async (input: string | URL | Request) => {
    const url = String(input);
    calls.push(url);
    if (url.includes("registry.npmjs.org")) return response({}, 503);
    return response([
      { tag_name: "hub-v9.0.0", draft: false, prerelease: false },
      { tag_name: "v1.4.0-rc.1", draft: false, prerelease: true },
      { tag_name: "v1.3.0", draft: false, prerelease: false },
      { tag_name: "v1.4.0", draft: true, prerelease: false },
      { tag_name: "v1.2.9", draft: false, prerelease: false },
    ]);
  }) as typeof fetch;
  const result = await runUpdateCommand({
    currentVersion: "1.2.3",
    check: true,
    json: true,
    dependencies: { fetch: fetchImpl, runProcess: okProcess },
  });
  assert.equal(result.exitCode, UPDATE_EXIT.AVAILABLE);
  assert.equal((result.json as { latestVersion: string }).latestVersion, "1.3.0");
  assert.equal((result.json as { source: string }).source, "github");
  assert.equal(calls.length, 2);
});

test("network failure soft-fails with a distinct exit and changes nothing", async () => {
  let processCalls = 0;
  const result = await runUpdateCommand({
    currentVersion: "1.2.3",
    check: false,
    json: false,
    dependencies: {
      fetch: (async () => { throw new Error("offline"); }) as typeof fetch,
      runProcess: async () => {
        processCalls++;
        return { exitCode: 0, stdout: "", stderr: "" };
      },
    },
  });
  assert.equal(result.exitCode, UPDATE_EXIT.LOOKUP_FAILED);
  assert.equal(processCalls, 0);
  assert.match(result.human, /No files were changed/);
});

test("npx update prints pin guidance and does not run npm install", async () => {
  const calls: string[][] = [];
  const result = await runUpdateCommand({
    currentVersion: "1.2.3",
    check: false,
    json: false,
    dependencies: {
      fetch: npmLatest("1.3.0"),
      env: { npm_command: "exec" },
      packageRoot: "/tmp/_npx/hash/node_modules/unity-open-mcp",
      runProcess: async (_command, args) => {
        calls.push(args);
        return { exitCode: 0, stdout: "", stderr: "" };
      },
    },
  });
  assert.equal(result.exitCode, 0);
  assert.equal(calls.length, 0);
  assert.match(result.human, /unity-open-mcp@1\.3\.0/);
  assert.match(result.human, /updates only the MCP server/);
});

test("global install mode runs npm install --global with the resolved version", async () => {
  const calls: Array<{ args: string[]; cwd?: string }> = [];
  const result = await runUpdateCommand({
    currentVersion: "1.2.3",
    check: false,
    json: false,
    dependencies: {
      fetch: npmLatest("1.3.0"),
      env: {},
      packageRoot: "/opt/npm/lib/node_modules/unity-open-mcp",
      runProcess: async (_command, args, cwd) => {
        calls.push({ args, cwd });
        if (args[0] === "root") {
          return { exitCode: 0, stdout: "/opt/npm/lib/node_modules\n", stderr: "" };
        }
        return { exitCode: 0, stdout: "updated", stderr: "" };
      },
    },
  });
  assert.equal(result.exitCode, 0);
  assert.deepEqual(calls[1], {
    args: ["install", "--global", "unity-open-mcp@1.3.0"],
    cwd: undefined,
  });
  assert.match(result.human, /Restart the MCP client/);
});

test("local install mode runs npm install in the owning project", async () => {
  const calls: Array<{ args: string[]; cwd?: string }> = [];
  const result = await runUpdateCommand({
    currentVersion: "1.2.3",
    check: false,
    json: true,
    dependencies: {
      fetch: npmLatest("1.3.0"),
      env: {},
      packageRoot: "/work/game-tools/node_modules/unity-open-mcp",
      readTextFile: manifestAt("/work/game-tools"),
      runProcess: async (_command, args, cwd) => {
        calls.push({ args, cwd });
        if (args[0] === "root") {
          return { exitCode: 0, stdout: "/opt/npm/lib/node_modules\n", stderr: "" };
        }
        return { exitCode: 0, stdout: "updated", stderr: "" };
      },
    },
  });
  assert.equal(result.exitCode, 0);
  assert.deepEqual(calls[1], {
    args: ["install", "unity-open-mcp@1.3.0"],
    cwd: "/work/game-tools",
  });
});

test("local install mode keeps the on-disk casing of the owning project path", async () => {
  const calls: Array<{ args: string[]; cwd?: string }> = [];
  const result = await runUpdateCommand({
    currentVersion: "1.2.3",
    check: false,
    json: true,
    dependencies: {
      fetch: npmLatest("1.3.0"),
      env: {},
      packageRoot: "/home/Dev/Projects/MyGame/node_modules/unity-open-mcp",
      readTextFile: manifestAt("/home/Dev/Projects/MyGame"),
      runProcess: async (_command, args, cwd) => {
        calls.push({ args, cwd });
        if (args[0] === "root") {
          return { exitCode: 0, stdout: "/opt/npm/lib/node_modules\n", stderr: "" };
        }
        return { exitCode: 0, stdout: "updated", stderr: "" };
      },
    },
  });
  assert.equal(result.exitCode, 0);
  assert.equal(calls[1]?.cwd, "/home/Dev/Projects/MyGame");
});

test("a failed `npm root -g` probe never turns a global install into a local one", async () => {
  const calls: string[][] = [];
  const result = await runUpdateCommand({
    currentVersion: "1.2.3",
    check: false,
    json: true,
    dependencies: {
      fetch: npmLatest("1.3.0"),
      env: {},
      packageRoot: "/usr/local/lib/node_modules/unity-open-mcp",
      // No package.json declares unity-open-mcp under /usr/local/lib.
      readTextFile: manifestAt("/nowhere"),
      runProcess: async (_command, args) => {
        calls.push(args);
        return { exitCode: 1, stdout: "", stderr: "npm ERR! prefix unavailable" };
      },
    },
  });
  assert.equal(result.exitCode, 0);
  assert.deepEqual(calls, [["root", "--global"]]);
  assert.equal((result.json as { installMode: string }).installMode, "unknown");
  assert.equal((result.json as { applied: boolean }).applied, false);
});

test("npm install failure uses exit 12 and preserves project/config ownership", async () => {
  const result = await runUpdateCommand({
    currentVersion: "1.2.3",
    check: false,
    json: false,
    dependencies: {
      fetch: npmLatest("1.3.0"),
      env: {},
      packageRoot: "/work/node_modules/unity-open-mcp",
      readTextFile: manifestAt("/work"),
      runProcess: async (_command, args) =>
        args[0] === "root"
          ? { exitCode: 0, stdout: "/global/node_modules\n", stderr: "" }
          : { exitCode: 1, stdout: "", stderr: "npm ERR! permission denied\n" },
    },
  });
  assert.equal(result.exitCode, UPDATE_EXIT.INSTALL_FAILED);
  assert.match(result.human, /permission denied/);
  assert.match(result.human, /No Unity project or MCP client configuration was changed/);
});
