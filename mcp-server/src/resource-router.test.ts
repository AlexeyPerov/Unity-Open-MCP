import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile, mkdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { PingCache } from "./ping-cache.ts";
import {
  ResourceRouter,
  handleHealthBaseline,
  handleBridgeStatus,
} from "./resource-router.ts";
import type { ResourceHandlerDeps } from "./resource-router.ts";
import type { PingSnapshot } from "./ping-cache.ts";

interface MockLive {
  readResourceCalls: string[];
  readResourceResult: Record<string, unknown>;
}

function makeDeps(
  overrides: Partial<ResourceHandlerDeps> & {
    mockLive?: MockLive;
  } = {},
): { deps: ResourceHandlerDeps; mockLive: MockLive } {
  const mockLive: MockLive = overrides.mockLive ?? {
    readResourceCalls: [],
    readResourceResult: {
      status: "ok",
      asOf: "2026-06-12T00:00:00Z",
      summary: { error: 1, warn: 2, info: 3 },
      source: "scan_paths",
    },
  };

  const live = {
    readResource: async (route: string) => {
      mockLive.readResourceCalls.push(route);
      return mockLive.readResourceResult;
    },
  } as unknown as ResourceHandlerDeps["live"];

  const pingCache = overrides.pingCache ?? new PingCache();

  return {
    deps: {
      live,
      pingCache,
      projectPath: overrides.projectPath ?? "/fake/project",
      port: overrides.port ?? 19120,
    },
    mockLive,
  };
}

function parseContent(result: { contents: Array<{ text: string }> }): unknown {
  return JSON.parse(result.contents[0]!.text);
}

// ---------------------------------------------------------------------------
// health/summary
// ---------------------------------------------------------------------------

test("health/summary returns ok payload from bridge", async () => {
  const { deps } = makeDeps();
  const router = new ResourceRouter(deps);
  const result = await router.read("unity-open-mcp://health/summary");
  const body = parseContent(result) as Record<string, unknown>;
  assert.equal(body.status, "ok");
  assert.equal(body.asOf, "2026-06-12T00:00:00Z");
  assert.deepEqual(body.summary, { error: 1, warn: 2, info: 3 });
  assert.equal(body.source, "scan_paths");
});

test("health/summary returns no_data when bridge returns no_data", async () => {
  const { deps, mockLive } = makeDeps();
  mockLive.readResourceResult = {
    status: "no_data",
    asOf: null,
    summary: null,
    nextStep: "Run unity_open_mcp_scan_paths or a gated mutation to populate the cache.",
  };
  const router = new ResourceRouter(deps);
  const result = await router.read("unity-open-mcp://health/summary");
  const body = parseContent(result) as Record<string, unknown>;
  assert.equal(body.status, "no_data");
  assert.equal(body.asOf, null);
  assert.equal(body.summary, null);
  assert.ok(typeof body.nextStep === "string");
});

test("health/summary read calls bridge with correct route", async () => {
  const { deps, mockLive } = makeDeps();
  const router = new ResourceRouter(deps);
  await router.read("unity-open-mcp://health/summary");
  assert.equal(mockLive.readResourceCalls.length, 1);
  assert.equal(mockLive.readResourceCalls[0], "unity-open-mcp://health/summary");
});

// ---------------------------------------------------------------------------
// health/baseline
// ---------------------------------------------------------------------------

test("health/baseline returns no_baseline when file is missing", async () => {
  const tmpDir = await mkdtemp(join(tmpdir(), "mcp-baseline-"));
  try {
    const { deps } = makeDeps({ projectPath: tmpDir });
    const router = new ResourceRouter(deps);
    const result = await router.read("unity-open-mcp://health/baseline");
    const body = parseContent(result) as Record<string, unknown>;
    assert.equal(body.status, "no_baseline");
    assert.equal(body.asOf, null);
    assert.ok(typeof body.baselinePath === "string");
    assert.ok((body.nextStep as string).includes("baseline_create"));
  } finally {
    await rm(tmpDir, { recursive: true, force: true });
  }
});

test("health/baseline returns ok when file exists", async () => {
  const tmpDir = await mkdtemp(join(tmpdir(), "mcp-baseline-"));
  try {
    await mkdir(join(tmpDir, "CI"), { recursive: true });
    await writeFile(
      join(tmpDir, "CI", "unity-open-mcp-baseline.json"),
      JSON.stringify({
        schemaVersion: 1,
        platformProfile: "desktop",
        generatedAt: "2026-06-12T00:00:00Z",
        summary: { error: 5, warn: 10, info: 0 },
        rules: [],
      }),
    );

    const { deps } = makeDeps({ projectPath: tmpDir });
    const router = new ResourceRouter(deps);
    const result = await router.read("unity-open-mcp://health/baseline");
    const body = parseContent(result) as Record<string, unknown>;
    assert.equal(body.status, "ok");
    assert.ok(body.asOf, "asOf should be populated from file mtime");
    assert.equal(body.schemaVersion, 1);
    assert.equal(body.platformProfile, "desktop");
    assert.deepEqual(body.summary, { error: 5, warn: 10, info: 0 });
  } finally {
    await rm(tmpDir, { recursive: true, force: true });
  }
});

test("health/baseline does not throw on corrupt JSON", async () => {
  const tmpDir = await mkdtemp(join(tmpdir(), "mcp-baseline-"));
  try {
    await mkdir(join(tmpDir, "CI"), { recursive: true });
    await writeFile(
      join(tmpDir, "CI", "unity-open-mcp-baseline.json"),
      "not valid json {{{",
    );

    const { deps } = makeDeps({ projectPath: tmpDir });
    const result = await handleHealthBaseline(deps);
    const body = parseContent(result) as Record<string, unknown>;
    assert.equal(body.status, "no_baseline");
  } finally {
    await rm(tmpDir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// bridge/status
// ---------------------------------------------------------------------------

test("bridge/status returns no_data when no ping has occurred", async () => {
  const { deps } = makeDeps();
  const result = handleBridgeStatus(deps);
  const body = parseContent(result) as Record<string, unknown>;
  assert.equal(body.status, "no_data");
  assert.equal(body.asOf, null);
  assert.equal(body.connected, false);
});

test("bridge/status returns ok with cached ping data", () => {
  const pingCache = new PingCache();
  pingCache.record({
    connected: true,
    projectPath: "/path/to/MyGame",
    unityVersion: "6000.0.23f1",
    bridgeVersion: "0.1.0",
    mode: "live",
    compiling: false,
    isPlaying: false,
  });

  const { deps } = makeDeps({ pingCache, port: 19120 });
  const result = handleBridgeStatus(deps);
  const body = parseContent(result) as Record<string, unknown>;
  assert.equal(body.status, "ok");
  assert.ok(body.asOf);
  assert.equal(body.connected, true);
  assert.equal(body.projectPath, "/path/to/MyGame");
  assert.equal(body.bridgePort, 19120);
  assert.equal(body.compiling, false);
  assert.equal(body.isPlaying, false);
});

test("bridge/status handler does not call live", () => {
  const { deps, mockLive } = makeDeps();
  handleBridgeStatus(deps);
  assert.equal(mockLive.readResourceCalls.length, 0);
});

// ---------------------------------------------------------------------------
// unknown URI
// ---------------------------------------------------------------------------

test("unknown URI returns error payload, not exception", async () => {
  const { deps } = makeDeps();
  const router = new ResourceRouter(deps);
  const result = await router.read("unity-open-mcp://unknown/uri");
  const body = parseContent(result) as Record<string, unknown>;
  assert.equal(body.status, "no_data");
  assert.ok((body.error as string).includes("Unknown resource URI"));
});
