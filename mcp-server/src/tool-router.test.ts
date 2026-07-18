// Tests for the ToolRouter dispatch matrix: which branch each tool name hits
// (offline / local / live / batch / ping), arg coercion for the local branches,
// and the _route metadata attached to live and batch results.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm, mkdir, writeFile } from "node:fs/promises";
import { utimesSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { ToolRouter } from "./tool-router.js";
// M31-optimizations Plan 1 / L14 — pure helper + module-load flag for the
// route-logging gate, exercised by the L14 tests at the bottom of this file.
import {
  resolveRouteLogEnabled,
  ROUTE_LOG_ENABLED as ROUTE_LOG_ENABLED_AT_LOAD,
} from "./tool-router.js";
import type { LiveClient } from "./live-client.js";
import type { BatchSpawn } from "./batch-spawn.js";
import type { BridgeEventStream, PullResult } from "./event-stream.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { ToolSessionState, filterVisibleTools } from "./tool-session-state.js";
import { DEFAULT_ENABLED_GROUPS } from "./capabilities/tool-groups.js";
import { setUnityProcessScannerForTest } from "./running-unity.js";
import { ALL_TOOLS } from "./tools/index.js";
// M31 Plan 3 — injectable seams for the restart_editor + resource_pressure
// routes (no real process.kill / lsof / /proc in unit tests).
import { setProcessKillerForTest } from "./editor-process-control.js";
import { setFdProbeForTest } from "./process-diagnostics.js";

// The default-active group set is the single source of truth in
// `capabilities/tool-groups.ts` (the catalog's `defaultEnabled: true`
// entries). Several manage_tools tests assert against the *current* default
// set; derive it here so the tests track the catalog instead of hard-coding a
// stale `["core"]`-only snapshot.
function expectedDefaultActive(): string[] {
  return Array.from(DEFAULT_ENABLED_GROUPS).sort();
}

// ---------------------------------------------------------------------------
// fakes
// ---------------------------------------------------------------------------

interface LiveCall {
  tool: string;
  args: Record<string, unknown>;
}

function makeFakeLive(opts: {
  available?: boolean;
  result?: CallToolResult;
  bridgeTools?: Set<string> | null;
} = {}): LiveClient & { calls: LiveCall[] } {
  const calls: LiveCall[] = [];
  const available = opts.available ?? true;
  const result =
    opts.result ??
    ({
      content: [{ type: "text", text: JSON.stringify({ ok: true }) }],
      isError: false,
    } satisfies CallToolResult);
  // M18 Plan 2 — listBridgeTools probe used by capabilities / manage_tools
  // to report per-group compiled-state availability. null simulates an
  // offline bridge response; undefined falls back to "probe succeeded with
  // empty inventory". Tests that don't care can leave it unset.
  const bridgeTools = opts.bridgeTools;
  return {
    calls,
    async isLiveAvailable() {
      return available;
    },
    async route(tool: string, args: Record<string, unknown>) {
      calls.push({ tool, args });
      return result;
    },
    async listBridgeTools() {
      if (bridgeTools === null) return null;
      if (bridgeTools === undefined) {
        return { tools: new Set<string>(), groups: [] };
      }
      return { tools: bridgeTools, groups: [] };
    },
  } as unknown as LiveClient & { calls: LiveCall[] };
}

function makeFakeBatch(opts: {
  batchTools?: Set<string>;
  result?: CallToolResult;
} = {}): BatchSpawn & { calls: LiveCall[] } {
  const calls: LiveCall[] = [];
  const batchTools = opts.batchTools ?? new Set(["unity_open_mcp_scan_all"]);
  const result =
    opts.result ??
    ({
      content: [{ type: "text", text: JSON.stringify({ summary: { error: 0, warn: 0, info: 0 } }) }],
      isError: false,
    } satisfies CallToolResult);
  return {
    calls,
    isBatchTool(tool: string) {
      return batchTools.has(tool);
    },
    async route(tool: string, args: Record<string, unknown>) {
      calls.push({ tool, args });
      return result;
    },
  } as unknown as BatchSpawn & { calls: LiveCall[] };
}

// M13 T4.4 — fake event stream that returns whatever the test pre-seeds. The
// real BridgeEventStream opens an SSE reader; in unit tests we don't want any
// network I/O, so the fake just drains a queue.
function makeFakeEventStream(events: PullResult["events"] = []): BridgeEventStream {
  return {
    pull(): PullResult {
      const out = events.slice();
      events = [];
      return {
        subscriberId: "test-subscriber",
        events: out,
        dropped: 0,
        connected: true,
        started: true,
        lastError: null,
      };
    },
    ensureSubscription() { return true; },
    stop() { /* no-op */ },
  } as unknown as BridgeEventStream;
}

function parseBody(result: CallToolResult): Record<string, unknown> {
  const first = result.content[0];
  if (!first || first.type !== "text" || typeof first.text !== "string") {
    throw new Error("expected a text content part");
  }
  return JSON.parse(first.text);
}

function routeOf(result: CallToolResult): string {
  const route = parseBody(result)._route as { route?: string } | undefined;
  return route?.route ?? "";
}

function errorCode(result: CallToolResult): string {
  const error = parseBody(result).error as { code?: string } | undefined;
  return error?.code ?? "";
}

/** Minimal on-disk project so the offline branches (list_assets/generate_skill) read real files. */
async function setupProject(tmp: string): Promise<void> {
  await mkdir(join(tmp, "Assets"), { recursive: true });
  await writeFile(join(tmp, "ProjectVersion.txt"), "m_EditorVersion: 6000.0.23f1\n");
  await mkdir(join(tmp, "Assets", "Prefabs"), { recursive: true });
  await writeFile(
    join(tmp, "Assets", "Prefabs", "Player.prefab"),
    `%YAML 1.1
--- !u!1 &100
GameObject:
  m_Name: Player
`,
  );
}

async function withTmp(name: string, fn: (tmp: string) => Promise<void>): Promise<void> {
  const tmp = await mkdtemp(join(tmpdir(), name));
  try {
    await fn(tmp);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
}

/** Build a ToolRouter with a fresh ToolSessionState (catalog defaults active). */
function makeRouter(
  live: LiveClient,
  batch: BatchSpawn,
  projectPath: string,
  eventStream: BridgeEventStream,
  sessionState: ToolSessionState = new ToolSessionState(),
  onToolListChanged?: () => void | Promise<void>,
  hubBackend?: import("./tool-router.js").HubControlBackend,
): ToolRouter {
  return new ToolRouter(
    live,
    batch,
    projectPath,
    eventStream,
    sessionState,
    onToolListChanged,
    hubBackend,
  );
}

/**
 * M26 Plan 2 — fake Hub-control backend. Avoids real subprocess + network
 * side effects in router tests. Each method records its call + returns a
 * canned result. `openOk` controls whether install/install-modules deep links
 * "succeed".
 */
function makeFakeHubBackend(opts: {
  editors?: { version: string; path: string; platforms: string[]; releaseType: string }[];
  releases?: { entries: { version: string; stream: string; releaseDate: string | null; releaseNotesUrl: string; changeset: string | null }[]; stale: boolean; fetchedAt: string };
  openOk?: boolean;
  installPath?: { path: string | null; source: "hub-cli" | "filesystem" | "none"; error: { code: string; message: string } | null };
  setOk?: boolean;
} = {}): import("./tool-router.js").HubControlBackend & { calls: string[] } {
  const calls: string[] = [];
  const editors = opts.editors ?? [];
  const releases =
    opts.releases ?? { entries: [], stale: false, fetchedAt: "2026-07-01T00:00:00.000Z" };
  const openOk = opts.openOk ?? true;
  const installPath =
    opts.installPath ?? { path: "/fake/Hub/Editor", source: "filesystem" as const, error: null };
  const setOk = opts.setOk ?? true;
  return {
    calls,
    listInstalledEditors() {
      calls.push("listInstalledEditors");
      return editors;
    },
    async fetchAvailableReleases() {
      calls.push("fetchAvailableReleases");
      return releases;
    },
    openInstallDeepLink(version, changeset) {
      calls.push(`openInstallDeepLink:${version}`);
      return openOk
        ? { deepLink: `unityhub://${version}`, opened: true, error: null }
        : {
            deepLink: `unityhub://${version}`,
            opened: false,
            error: { code: "deep_link_open_failed", message: "fake: no handler" },
          };
    },
    getInstallPath() {
      calls.push("getInstallPath");
      return installPath;
    },
    setInstallPath(path) {
      calls.push(`setInstallPath:${path}`);
      return setOk
        ? { success: true, error: null, output: "ok" }
        : {
            success: false,
            error: { code: "hub_cli_not_found", message: "fake: no hub" },
            output: "",
          };
    },
  };
}

// ---------------------------------------------------------------------------
// local branches (no live/batch hop)
// ---------------------------------------------------------------------------

test("route: list_assets resolves offline and tags _source=offline", async () => {
  await withTmp("router-list-", async (tmp) => {
    await setupProject(tmp);
    const live = makeFakeLive();
    const batch = makeFakeBatch();
    const router = makeRouter(live, batch, tmp, makeFakeEventStream());

    const result = await router.route("unity_open_mcp_list_assets", { folder: "Assets" });
    const body = parseBody(result);
    assert.equal(result.isError, false);
    assert.equal(body._source, "offline");
    assert.equal(live.calls.length, 0);
    assert.equal(batch.calls.length, 0);
  });
});

test("route: capabilities returns local capability surface", async () => {
  await withTmp("router-caps-", async (tmp) => {
    const live = makeFakeLive();
    const batch = makeFakeBatch();
    const router = makeRouter(live, batch, tmp, makeFakeEventStream());

    const result = await router.route("unity_open_mcp_capabilities", {});
    const body = parseBody(result);
    assert.equal(result.isError, false);
    assert.equal(body._source, "local");
    assert.equal(live.calls.length, 0);
  });
});

test("route: capabilities filters by kind", async () => {
  await withTmp("router-caps-kind-", async (tmp) => {
    const router = makeRouter(makeFakeLive(), makeFakeBatch(), tmp, makeFakeEventStream());
    const res = await router.route("unity_open_mcp_capabilities", { kind: "rules" });
    const body = parseBody(res);
    assert.ok(Array.isArray(body.rules) && body.rules.length > 0, "rules surface present");
    assert.ok(Array.isArray(body.tools) && body.tools.length === 0, "tools filtered out by kind=rules");
  });
});

test("route: list_rules returns local rule catalog with _source=local", async () => {
  await withTmp("router-list-rules-", async (tmp) => {
    const live = makeFakeLive();
    const batch = makeFakeBatch();
    const router = makeRouter(live, batch, tmp, makeFakeEventStream());

    const res = await router.route("unity_open_mcp_list_rules", {});
    const body = parseBody(res);
    assert.equal(res.isError, false);
    assert.equal(body._source, "local");
    assert.ok(Array.isArray(body.rules) && (body.rules as unknown[]).length > 0);
    assert.equal(live.calls.length, 0, "list_rules must not hit the live bridge");
    assert.equal(batch.calls.length, 0, "list_rules must not spawn batch Unity");
  });
});

test("route: list_rules honors asset_kind filter locally", async () => {
  await withTmp("router-list-rules-filter-", async (tmp) => {
    const router = makeRouter(makeFakeLive(), makeFakeBatch(), tmp, makeFakeEventStream());
    const res = await router.route("unity_open_mcp_list_rules", { asset_kind: "prefab" });
    const body = parseBody(res);
    const rules = body.rules as Array<{ id: string }>;
    const ids = rules.map((r) => r.id);
    assert.ok(ids.includes("missing_references"));
    assert.ok(ids.includes("scene_prefab_health"));
    assert.ok(!ids.includes("materials"));
    assert.equal(body._source, "local");
  });
});

test("route: generate_skill returns local skill without writing", async () => {
  await withTmp("router-skill-", async (tmp) => {
    await setupProject(tmp);
    const live = makeFakeLive();
    const batch = makeFakeBatch();
    const router = makeRouter(live, batch, tmp, makeFakeEventStream());

    const result = await router.route("unity_open_mcp_generate_skill", { write: false });
    const body = parseBody(result);
    assert.equal(result.isError, false);
    assert.equal(body._source, "local");
    assert.ok(Array.isArray(body.written) && body.written.length === 0, "write=false must not persist");
    assert.ok(typeof body.skill === "string");
    assert.equal(live.calls.length, 0);
  });
});

// ---------------------------------------------------------------------------
// pull_events — M13 T4.4
// ---------------------------------------------------------------------------

test("route: pull_events returns bridge_unavailable when live is down", async () => {
  const live = makeFakeLive({ available: false });
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());

  const result = await router.route("unity_senses_pull_events", {});
  assert.equal(result.isError, true);
  assert.equal(errorCode(result), "bridge_unavailable");
});

test("route: pull_events drains queued events when live is up", async () => {
  const live = makeFakeLive({ available: true });
  const events = [
    {
      seq: 1,
      ts: "2026-06-17T00:00:00.000Z",
      type: "log" as const,
      logType: "error",
      message: "boom",
    },
    {
      seq: 2,
      ts: "2026-06-17T00:00:00.100Z",
      type: "editor_state" as const,
      state: "idle",
      isCompiling: false,
      isPlaying: false,
    },
  ];
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream(events));

  const result = await router.route("unity_senses_pull_events", { max_events: 10 });
  const body = parseBody(result);
  assert.equal(result.isError, false);
  assert.ok(Array.isArray(body.events) && body.events.length === 2, "both events drained");
  assert.equal(body.connected, true);
  assert.equal(body.started, true);
});

test("route: pull_events caps max_events at 1000", async () => {
  const live = makeFakeLive({ available: true });
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());

  const result = await router.route("unity_senses_pull_events", { max_events: 999999 });
  const body = parseBody(result);
  assert.equal(result.isError, false);
  // The fake yields an empty list; we only assert the call accepted the cap
  // and returned a well-formed result (no overflow error).
  assert.ok(Array.isArray(body.events));
});

// ---------------------------------------------------------------------------
// find_references — offline when no live, live when available
// ---------------------------------------------------------------------------

test("route: find_references goes offline when live bridge is down", async () => {
  await withTmp("router-refs-offline-", async (tmp) => {
    await setupProject(tmp);
    const live = makeFakeLive({ available: false });
    const batch = makeFakeBatch();
    const router = makeRouter(live, batch, tmp, makeFakeEventStream());

    const result = await router.route("unity_open_mcp_find_references", {
      guid: "0000000000000000000000000000aaaa",
    });
    // Offline with no references is a valid non-error empty result.
    assert.equal(result.isError, false);
    assert.equal(live.calls.length, 0);
  });
});

test("route: find_references requires asset_path or guid when offline", async () => {
  await withTmp("router-refs-missing-", async (tmp) => {
    const live = makeFakeLive({ available: false });
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream());

    const result = await router.route("unity_open_mcp_find_references", {});
    assert.equal(result.isError, true);
    assert.equal(errorCode(result), "missing_parameter");
  });
});

test("route: find_references goes live when bridge is available", async () => {
  await withTmp("router-refs-live-", async (tmp) => {
    const live = makeFakeLive({ available: true });
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream());

    await router.route("unity_open_mcp_find_references", { guid: "deadbeef" });
    assert.equal(live.calls.length, 1);
    assert.equal(live.calls[0].tool, "unity_open_mcp_find_references");
  });
});

// T6.1 — the live find_references/dependencies drill-downs must tag _source=live
// so the response is symmetric with the offline path's _source=offline. The fake
// live returns a bridge-shaped body (flat referencedBy list) that the router
// folds server-side; _source is injected after the fold.
test("route: find_references tags _source=live when served by the bridge", async () => {
  const live = makeFakeLive({
    available: true,
    result: {
      content: [{ type: "text", text: JSON.stringify({ referencedBy: ["Assets/Foo.prefab", "Assets/Bar.mat"], totalCount: 2 }) }],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_find_references", { guid: "deadbeef" });
  assert.equal(live.calls.length, 1);
  assert.equal(parseBody(result)._source, "live");
  assert.equal(routeOf(result), "live");
});

test("route: dependencies tags _source=live when served by the bridge", async () => {
  const live = makeFakeLive({
    available: true,
    result: {
      content: [{ type: "text", text: JSON.stringify({ forward: [], reverse: [], forwardCount: 0, reverseCount: 0 }) }],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_dependencies", { asset_path: "Assets/Foo.prefab" });
  assert.equal(live.calls.length, 1);
  assert.equal(live.calls[0].tool, "unity_open_mcp_dependencies");
  assert.equal(parseBody(result)._source, "live");
  assert.equal(routeOf(result), "live");
});

// ---------------------------------------------------------------------------
// T6.4 — dispatch precedence: validate_edit / scan_paths have named routeHandlers
// (routeVerifyResult) that win dispatch BEFORE the ALWAYS_BATCH_TOOLS lookup is
// reached. They are NOT in BATCH_TOOL_NAMES, so even the named handler's
// canBatch branch routes them live. This guards the invariant documented in
// batch-spawn.ts's ALWAYS_BATCH_TOOLS comment: an entry there for them would be
// dead code.
// ---------------------------------------------------------------------------

test("route: validate_edit hits the named handler and routes live (never batch)", async () => {
  const live = makeFakeLive({
    available: true,
    result: {
      content: [{ type: "text", text: JSON.stringify({ summary: { error: 0, warn: 0, info: 0 }, issues: [] }) }],
      isError: false,
    },
  });
  const batch = makeFakeBatch(); // isBatchTool(validate_edit) === false
  const router = makeRouter(live, batch, "/proj", makeFakeEventStream());

  await router.route("unity_open_mcp_validate_edit", { paths: ["Assets/Foo.prefab"] });
  assert.equal(live.calls.length, 1, "validate_edit should be served live via the named handler");
  assert.equal(live.calls[0].tool, "unity_open_mcp_validate_edit");
  assert.equal(batch.calls.length, 0, "validate_edit must never reach the batch spawner");
});

test("route: scan_paths hits the named handler and routes live (never batch)", async () => {
  const live = makeFakeLive({
    available: true,
    result: {
      content: [{ type: "text", text: JSON.stringify({ summary: { error: 0, warn: 0, info: 0 }, issues: [] }) }],
      isError: false,
    },
  });
  const batch = makeFakeBatch();
  const router = makeRouter(live, batch, "/proj", makeFakeEventStream());

  await router.route("unity_open_mcp_scan_paths", { paths: ["Assets/Foo.prefab"] });
  assert.equal(live.calls.length, 1, "scan_paths should be served live via the named handler");
  assert.equal(batch.calls.length, 0, "scan_paths must never reach the batch spawner");
});

// ---------------------------------------------------------------------------
// live routing — generic non-batch tool
// ---------------------------------------------------------------------------

test("route: non-batch tool routes to live and tags _route=live", async () => {
  const live = makeFakeLive({ available: true });
  const batch = makeFakeBatch();
  const router = makeRouter(live, batch, "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_invoke_method", { method: "Foo" });
  assert.equal(live.calls.length, 1);
  assert.equal(batch.calls.length, 0);
  assert.equal(routeOf(result), "live");
});

// ---------------------------------------------------------------------------
// batch tools — prefer live, fall back to batch
// ---------------------------------------------------------------------------

test("route: batch-capable tool prefers live when available", async () => {
  // find_members is batch-capable but NOT always-batch (not verify-family),
  // so it stays live-first when the bridge is up — the generic batch-tool
  // behavior. (scan_all / baseline_create / regression_check are always-batch
  // and covered by their own tests below.)
  const live = makeFakeLive({ available: true });
  const batch = makeFakeBatch({ batchTools: new Set(["unity_open_mcp_find_members"]) });
  const router = makeRouter(live, batch, "/proj", makeFakeEventStream());

  await router.route("unity_open_mcp_find_members", {});
  assert.equal(live.calls.length, 1, "live should serve the batch-capable tool when up");
  assert.equal(batch.calls.length, 0);
});

test("route: batch-capable tool falls back to batch with _route=batch when live is down", async () => {
  const live = makeFakeLive({ available: false });
  const batch = makeFakeBatch({
    batchTools: new Set(["unity_open_mcp_find_members"]),
    result: {
      content: [{ type: "text", text: JSON.stringify({ summary: { error: 0, warn: 0, info: 0 } }) }],
      isError: false,
    },
  });
  const router = makeRouter(live, batch, "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_find_members", {});
  assert.equal(live.calls.length, 0);
  assert.equal(batch.calls.length, 1, "batch should serve the tool when live is down");
  const body = parseBody(result);
  assert.equal(routeOf(result), "batch");
  assert.equal((parseBody(result)._route as { fallbackReason?: string }).fallbackReason, "live_unavailable");
});

test("route: compile_check always routes to batch even when live is available", async () => {
  // The whole point of compile_check is to recompile from scratch; routing it
  // to a live Editor that already compiled would trivially report success.
  const live = makeFakeLive({ available: true });
  const batch = makeFakeBatch({
    batchTools: new Set(["unity_open_mcp_compile_check"]),
    result: {
      content: [{ type: "text", text: JSON.stringify({ status: "compile_passed", errorCount: 0, errors: [] }) }],
      isError: false,
    },
  });
  const router = makeRouter(live, batch, "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_compile_check", {});
  assert.equal(live.calls.length, 0, "compile_check must never hit the live bridge");
  assert.equal(batch.calls.length, 1, "compile_check must always go to batch");
  assert.equal(routeOf(result), "batch");
  assert.equal(
    (parseBody(result)._route as { fallbackReason?: string }).fallbackReason,
    "compile_check_always_batch",
  );
});

// Verify-family tools (scan_all / baseline_create / regression_check) are
// batch-only: the headless verify package runs them and they are NOT
// registered on the live bridge. They must always route to batch even when the
// live bridge is up — otherwise an agent sees a bare tool_not_found for a known
// batch-only tool. Mirror the compile_check precedent.
test("route: verify tools always route to batch even when live is available", async () => {
  const verifyTools = [
    "unity_open_mcp_scan_all",
    "unity_open_mcp_baseline_create",
    "unity_open_mcp_regression_check",
  ];
  for (const tool of verifyTools) {
    const live = makeFakeLive({ available: true });
    const batch = makeFakeBatch({
      batchTools: new Set(verifyTools),
      result: {
        content: [{ type: "text", text: JSON.stringify({ summary: { error: 0, warn: 0, info: 0 } }) }],
        isError: false,
      },
    });
    const router = makeRouter(live, batch, "/proj", makeFakeEventStream());

    const result = await router.route(tool, {});
    assert.equal(live.calls.length, 0, `${tool} must never hit the live bridge`);
    assert.equal(batch.calls.length, 1, `${tool} must always go to batch`);
    assert.equal(routeOf(result), "batch", `${tool} _route.route`);
    assert.equal(
      (parseBody(result)._route as { fallbackReason?: string }).fallbackReason,
      "verify_always_batch",
      `${tool} fallbackReason`,
    );
  }
});

test("route: verify tools still batch when live bridge is down", async () => {
  // When live is down the tool still resolves via batch — the reason stays
  // verify_always_batch (not live_unavailable) because the routing decision is
  // "always batch", independent of live availability.
  const live = makeFakeLive({ available: false });
  const batch = makeFakeBatch({
    batchTools: new Set(["unity_open_mcp_scan_all"]),
    result: {
      content: [{ type: "text", text: JSON.stringify({ summary: { error: 0, warn: 0, info: 0 } }) }],
      isError: false,
    },
  });
  const router = makeRouter(live, batch, "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_scan_all", {});
  assert.equal(live.calls.length, 0);
  assert.equal(batch.calls.length, 1);
  assert.equal(routeOf(result), "batch");
  assert.equal(
    (parseBody(result)._route as { fallbackReason?: string }).fallbackReason,
    "verify_always_batch",
  );
});

// ---------------------------------------------------------------------------
// ping
// ---------------------------------------------------------------------------

test("route: ping returns batch ping result when live is down", async () => {
  const live = makeFakeLive({ available: false });
  const batch = makeFakeBatch();
  const router = makeRouter(live, batch, "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_ping", {});
  const body = parseBody(result);
  assert.equal(body.connected, false);
  assert.equal(body.mode, "batch");
  assert.equal(routeOf(result), "batch");
  assert.equal(batch.calls.length, 0, "ping must not spawn a batch process");
});

test("route: ping is served by live when bridge is up", async () => {
  const live = makeFakeLive({
    available: true,
    result: {
      content: [{ type: "text", text: JSON.stringify({ connected: true, mode: "live" }) }],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());

  // ping is not a batch tool and not compressible -> falls into the generic
  // live branch (liveAvailable true -> live.route).
  await router.route("unity_open_mcp_ping", {});
  assert.equal(live.calls.length, 1);
});

// ---------------------------------------------------------------------------
// compressible tools delegate to the compressible router (smoke)
// ---------------------------------------------------------------------------

test("route: read_asset is routed via the compressible path (offline hit, no live call)", async () => {
  await withTmp("router-read-", async (tmp) => {
    await setupProject(tmp);
    const live = makeFakeLive();
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream());

    const result = await router.route("unity_open_mcp_read_asset", {
      asset_path: "Assets/Prefabs/Player.prefab",
    });
    const body = parseBody(result);
    assert.equal(result.isError, false);
    assert.equal(live.calls.length, 0);
    // compressible results are tagged _route=live by the router wrapper even
    // when the source was offline (the wrapper does not inspect the body).
    assert.equal(routeOf(result), "live");
  });
});

// ---------------------------------------------------------------------------
// read_compile_errors — offline Editor.log reader (specs/feedback.md
// 2026-07-05 entry adds staleLogSuspected when a cited source file is newer
// than Editor.log).
// ---------------------------------------------------------------------------

test("route: read_compile_errors attaches staleLogSuspected when a cited source is newer than the log", async () => {
  // Real-shaped scenario: the log was written at t=1000, but the agent edited
  // Assets/Scripts/Bridge.cs at t=2000 (after fixing the namespace the log
  // still complains about). The router must surface staleLogSuspected so the
  // agent knows to force a recompile before trusting the stale errors.
  await withTmp("router-rce-stale-", async (tmp) => {
    await mkdir(join(tmp, "Logs"), { recursive: true });
    await mkdir(join(tmp, "Assets", "Scripts"), { recursive: true });
    const logPath = join(tmp, "Logs", "Editor.log");
    const srcPath = join(tmp, "Assets", "Scripts", "Bridge.cs");
    // Write a log with a CS0118 error citing the now-fixed source file.
    await writeFile(
      logPath,
      "Assets/Scripts/Bridge.cs(10,14): error CS0118: " +
        "'ParticleSystem' is a namespace but is used like a type\n",
    );
    await writeFile(srcPath, "namespace Fixed {}");
    utimesSync(logPath, new Date(1000_000), new Date(1000_000));
    utimesSync(srcPath, new Date(2000_000), new Date(2000_000));

    const router = makeRouter(makeFakeLive(), makeFakeBatch(), tmp, makeFakeEventStream());
    const result = await router.route("unity_open_mcp_read_compile_errors", {});
    const body = parseBody(result);

    assert.equal(result.isError, false);
    assert.equal(body.status, "compile_failed");
    assert.equal(body.errorCount, 1);
    assert.equal(body.staleLogSuspected, true);
    assert.ok(typeof body.staleLogHint === "string" && body.staleLogHint.length > 0);
    assert.ok(Array.isArray(body.staleLogNewerFiles));
    assert.deepEqual(body.staleLogNewerFiles, ["Assets/Scripts/Bridge.cs"]);
  });
});

test("route: read_compile_errors omits staleLogSuspected when the log is fresher than the sources", async () => {
  // Healthy case: the log was written AFTER the latest source edit (a fresh
  // compile just produced these errors). Must NOT attach the stale flag —
  // these errors are current and the agent should trust + fix them.
  await withTmp("router-rce-fresh-", async (tmp) => {
    await mkdir(join(tmp, "Logs"), { recursive: true });
    await mkdir(join(tmp, "Assets", "Scripts"), { recursive: true });
    const logPath = join(tmp, "Logs", "Editor.log");
    const srcPath = join(tmp, "Assets", "Scripts", "Bridge.cs");
    await writeFile(
      logPath,
      "Assets/Scripts/Bridge.cs(10,14): error CS0118: still broken\n",
    );
    await writeFile(srcPath, "namespace Still.Broken {}");
    utimesSync(srcPath, new Date(1000_000), new Date(1000_000));
    utimesSync(logPath, new Date(2000_000), new Date(2000_000));

    const router = makeRouter(makeFakeLive(), makeFakeBatch(), tmp, makeFakeEventStream());
    const result = await router.route("unity_open_mcp_read_compile_errors", {});
    const body = parseBody(result);

    assert.equal(body.status, "compile_failed");
    assert.equal(body.staleLogSuspected, undefined,
      "fresh log must not carry the stale flag");
  });
});

// ---------------------------------------------------------------------------
// M18 Plan 2 / T18.2.2 — manage_tools routes local + mutates session state
// ---------------------------------------------------------------------------

test("route: manage_tools list_groups returns the catalog with session activation state", async () => {
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "list_groups",
  });
  const body = parseBody(result);
  assert.equal(result.isError, false);
  assert.equal(body._source, "local");
  assert.ok(Array.isArray(body.groups));
  const core = (body.groups as Array<{ id: string; active: boolean }>).find(
    (g) => g.id === "core",
  );
  assert.ok(core);
  assert.equal(core!.active, true, "fresh session has core active");
  const nav = (body.groups as Array<{ id: string; active: boolean }>).find(
    (g) => g.id === "navigation",
  );
  assert.ok(nav);
  assert.equal(nav!.active, false, "fresh session has navigation inactive");
});

test("route: manage_tools activate adds a group to the session", async () => {
  const session = new ToolSessionState();
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "activate",
    group: "navigation",
  });
  const body = parseBody(result);
  assert.equal(result.isError, false);
  assert.equal(body.changed, true);
  assert.deepEqual(
    body.activeGroups,
    [...expectedDefaultActive(), "navigation"].sort(),
  );
  // The store reflects the change.
  assert.ok(session.isGroupActive("navigation"));
});

test("route: manage_tools activate is idempotent (changed=false on second call)", async () => {
  const session = new ToolSessionState();
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
  );
  await router.route("unity_open_mcp_manage_tools", {
    action: "activate",
    group: "probuilder",
  });
  const second = await router.route("unity_open_mcp_manage_tools", {
    action: "activate",
    group: "probuilder",
  });
  const body = parseBody(second);
  assert.equal(body.changed, false);
});

test("route: manage_tools activate rejects unknown group with structured error", async () => {
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "activate",
    group: "does-not-exist",
  });
  assert.equal(result.isError, true);
  assert.equal(errorCode(result), "unknown_group");
});

test("route: manage_tools activate without group returns missing_parameter", async () => {
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "activate",
  });
  assert.equal(result.isError, true);
  assert.equal(errorCode(result), "missing_parameter");
});

test("route: manage_tools deactivate removes a group", async () => {
  const session = new ToolSessionState();
  session.activate("navigation");
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "deactivate",
    group: "navigation",
  });
  const body = parseBody(result);
  assert.equal(body.changed, true);
  assert.deepEqual(body.activeGroups, expectedDefaultActive());
  assert.equal(session.isGroupActive("navigation"), false);
});

test("route: manage_tools reset restores default-on groups", async () => {
  const session = new ToolSessionState();
  session.activate("navigation");
  session.activate("probuilder");
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "reset",
  });
  const body = parseBody(result);
  assert.equal(body.reset, true);
  assert.deepEqual(body.activeGroups, expectedDefaultActive());
});

test("route: manage_tools unknown action returns structured error", async () => {
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "bogus",
  });
  assert.equal(result.isError, true);
  assert.equal(errorCode(result), "unknown_action");
});

test("route: manage_tools does not hit the live bridge", async () => {
  // manage_tools is server-only — it never touches the bridge even when live.
  const live = makeFakeLive();
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());
  await router.route("unity_open_mcp_manage_tools", { action: "list_groups" });
  assert.equal(live.calls.length, 0);
});

// ---------------------------------------------------------------------------
// M18 Plan 2 — manage_tools emits tools/list_changed when visibility changes
// ---------------------------------------------------------------------------

test("route: manage_tools activate notifies when visibility changes", async () => {
  let notifyCount = 0;
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    new ToolSessionState(),
    () => {
      notifyCount++;
    },
  );
  await router.route("unity_open_mcp_manage_tools", {
    action: "activate",
    // Activate a group that is NOT in the default-enabled set so the call
    // actually changes visibility and emits a notification. Domain groups
    // (navigation, …) are always opt-in.
    group: "navigation",
  });
  assert.equal(notifyCount, 1);
});

test("route: manage_tools activate does not notify when idempotent", async () => {
  let notifyCount = 0;
  const session = new ToolSessionState();
  session.activate("asset-intelligence");
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
    () => {
      notifyCount++;
    },
  );
  await router.route("unity_open_mcp_manage_tools", {
    action: "activate",
    group: "asset-intelligence",
  });
  assert.equal(notifyCount, 0);
});

test("route: manage_tools deactivate notifies when visibility changes", async () => {
  let notifyCount = 0;
  const session = new ToolSessionState();
  session.activate("navigation");
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
    () => {
      notifyCount++;
    },
  );
  await router.route("unity_open_mcp_manage_tools", {
    action: "deactivate",
    group: "navigation",
  });
  assert.equal(notifyCount, 1);
});

test("route: manage_tools deactivate does not notify when idempotent", async () => {
  let notifyCount = 0;
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    new ToolSessionState(),
    () => {
      notifyCount++;
    },
  );
  await router.route("unity_open_mcp_manage_tools", {
    action: "deactivate",
    group: "navigation",
  });
  assert.equal(notifyCount, 0);
});

test("route: manage_tools reset notifies when visibility changes", async () => {
  let notifyCount = 0;
  const session = new ToolSessionState();
  session.activate("navigation");
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
    () => {
      notifyCount++;
    },
  );
  await router.route("unity_open_mcp_manage_tools", { action: "reset" });
  assert.equal(notifyCount, 1);
});

test("route: manage_tools reset does not notify when already at defaults", async () => {
  let notifyCount = 0;
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    new ToolSessionState(),
    () => {
      notifyCount++;
    },
  );
  await router.route("unity_open_mcp_manage_tools", { action: "reset" });
  assert.equal(notifyCount, 0);
});

test("route: manage_tools list_groups does not notify", async () => {
  let notifyCount = 0;
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    new ToolSessionState(),
    () => {
      notifyCount++;
    },
  );
  await router.route("unity_open_mcp_manage_tools", { action: "list_groups" });
  assert.equal(notifyCount, 0);
});

// ---------------------------------------------------------------------------
// M31 Plan 2 / T31.2 — manage_tools suggest / activate_for (intent → groups)
// ---------------------------------------------------------------------------

test("route: manage_tools suggest returns recommendations without changing state", async () => {
  const session = new ToolSessionState();
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "suggest",
    intent: "bake a navmesh for the dungeon",
  });
  const body = parseBody(result);
  assert.equal(result.isError, false);
  assert.equal(body._source, "local");
  assert.equal(body.action, "suggest");
  assert.equal(body.empty, false);
  const groups = body.groups as Array<{ id: string; alreadyActive: boolean }>;
  const ids = groups.map((g) => g.id);
  assert.ok(ids.includes("navigation"), "navigation should be recommended");
  // suggest must NOT mutate session state.
  assert.deepEqual(session.activeGroups(), expectedDefaultActive());
});

test("route: manage_tools suggest with explicit tags returns the implied groups", async () => {
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "suggest",
    tags: ["audio", "lighting"],
  });
  const body = parseBody(result);
  const ids = (body.groups as Array<{ id: string }>).map((g) => g.id);
  assert.ok(ids.includes("audio"));
  assert.ok(ids.includes("lighting"));
});

test("route: manage_tools suggest with unknown intent returns empty + hint", async () => {
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "suggest",
    intent: "zzz qqq xxx yyy",
  });
  const body = parseBody(result);
  assert.equal(body.empty, true);
  assert.equal((body.groups as unknown[]).length, 0);
  assert.match(String(body.hint), /list_groups/);
  assert.equal(result.isError, false, "empty recommendation is not an error");
});

test("route: manage_tools suggest with no intent and no tags returns empty", async () => {
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "suggest",
  });
  const body = parseBody(result);
  assert.equal(body.empty, true);
});

test("route: manage_tools suggest adds gate-intelligence for a mutating intent", async () => {
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "suggest",
    intent: "create and modify several gameobjects",
  });
  const body = parseBody(result);
  const ids = (body.groups as Array<{ id: string }>).map((g) => g.id);
  assert.ok(ids.includes("gate-intelligence"), "mutating intent surfaces gate-intelligence");
  assert.ok(ids.includes("typed-editor"), "gameobject intent surfaces typed-editor");
});

test("route: manage_tools suggest reports unmatched caller tags", async () => {
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "suggest",
    tags: ["navigation", "floob"],
  });
  const body = parseBody(result);
  const intent = body.intent as { unmatchedTags: string[] };
  assert.deepEqual(intent.unmatchedTags, ["floob"]);
});

test("route: manage_tools suggest does not notify", async () => {
  let notifyCount = 0;
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    new ToolSessionState(),
    () => {
      notifyCount++;
    },
  );
  await router.route("unity_open_mcp_manage_tools", {
    action: "suggest",
    intent: "bake a navmesh",
  });
  assert.equal(notifyCount, 0, "suggest is read-only and must not notify");
});

test("route: manage_tools activate_for activates the recommended groups", async () => {
  const session = new ToolSessionState();
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "activate_for",
    intent: "bake a navmesh for the dungeon",
  });
  const body = parseBody(result);
  assert.equal(result.isError, false);
  assert.equal(body.action, "activate_for");
  assert.ok((body.activated as string[]).includes("navigation"));
  // Session state reflects the activation.
  assert.ok(session.isGroupActive("navigation"));
  // activeGroups in the response includes navigation.
  assert.ok((body.activeGroups as string[]).includes("navigation"));
});

test("route: manage_tools activate_for notifies when visibility changes", async () => {
  let notifyCount = 0;
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    new ToolSessionState(),
    () => {
      notifyCount++;
    },
  );
  await router.route("unity_open_mcp_manage_tools", {
    action: "activate_for",
    intent: "bake a navmesh",
  });
  assert.equal(notifyCount, 1, "activate_for must notify when groups come online");
});

test("route: manage_tools activate_for does not notify when nothing changes", async () => {
  let notifyCount = 0;
  const session = new ToolSessionState();
  // Pre-activate navigation so the recommend set is already fully active.
  session.activate("navigation");
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
    () => {
      notifyCount++;
    },
  );
  await router.route("unity_open_mcp_manage_tools", {
    action: "activate_for",
    tags: ["navigation"],
  });
  assert.equal(notifyCount, 0, "idempotent activate_for must not notify");
});

test("route: manage_tools activate_for is idempotent (changed=false on second call)", async () => {
  const session = new ToolSessionState();
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
  );
  await router.route("unity_open_mcp_manage_tools", {
    action: "activate_for",
    intent: "bake a navmesh",
  });
  const second = await router.route("unity_open_mcp_manage_tools", {
    action: "activate_for",
    intent: "bake a navmesh",
  });
  const body = parseBody(second);
  assert.equal(body.changed, false);
  assert.deepEqual(body.activated as string[], []);
  assert.ok((body.skipped as string[]).includes("navigation"));
});

test("route: manage_tools activate_for with unknown intent returns empty + hint, no state change", async () => {
  const session = new ToolSessionState();
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "activate_for",
    intent: "zzz qqq xxx",
  });
  const body = parseBody(result);
  assert.equal(body.empty, true);
  assert.equal(result.isError, false);
  // No state change, no notification needed.
  assert.deepEqual(session.activeGroups(), expectedDefaultActive());
});

test("route: manage_tools activate_for surfaces unavailable recommended groups honestly", async () => {
  // navigation is package-gated; with an empty bridge inventory its
  // availability is `false` (tools not compiled in). The recommended group
  // must surface in `unavailable` AND still be activated (today's model lets
  // an agent activate an unavailable group — the tools appear but error at
  // call time).
  const session = new ToolSessionState();
  const live = makeFakeLive({ available: true, bridgeTools: new Set() });
  const router = makeRouter(
    live,
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "activate_for",
    tags: ["navigation"],
  });
  const body = parseBody(result);
  const unavailable = body.unavailable as Array<{ id: string; reason: string | null }>;
  assert.ok(
    unavailable.some((u) => u.id === "navigation"),
    "navigation must be reported unavailable when its package is absent",
  );
  // Still activated (today's session model).
  assert.ok((body.activated as string[]).includes("navigation"));
  // And the augmented group carries available:false.
  const nav = (body.groups as Array<{ id: string; available: boolean | null }>)
    .find((g) => g.id === "navigation");
  assert.ok(nav);
  assert.equal(nav!.available, false);
});

test("route: manage_tools suggest also surfaces compiled-state availability", async () => {
  // suggest is read-only but must still report availability so the agent
  // knows whether activation would be useful.
  const live = makeFakeLive({ available: true, bridgeTools: new Set() });
  const router = makeRouter(
    live,
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "suggest",
    tags: ["navigation"],
  });
  const body = parseBody(result);
  const nav = (body.groups as Array<{ id: string; available: boolean | null }>)
    .find((g) => g.id === "navigation");
  assert.ok(nav);
  assert.equal(nav!.available, false);
});

test("route: manage_tools unknown_action error lists the new actions", async () => {
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "bogus",
  });
  assert.equal(result.isError, true);
  assert.equal(errorCode(result), "unknown_action");
  const body = parseBody(result);
  const msg = (body.error as { message: string }).message;
  assert.match(msg, /suggest/);
  assert.match(msg, /activate_for/);
});

test("route: manage_tools activate_for with intent + tags merges both signals", async () => {
  const session = new ToolSessionState();
  const router = makeRouter(
    makeFakeLive(),
    makeFakeBatch(),
    "/proj",
    makeFakeEventStream(),
    session,
  );
  const result = await router.route("unity_open_mcp_manage_tools", {
    action: "activate_for",
    intent: "verify the scene references",
    tags: ["navigation"],
  });
  const body = parseBody(result);
  const activated = body.activated as string[];
  // verify → gate-and-verify is already default-on (skipped) but gate-intelligence is added.
  assert.ok(activated.includes("gate-intelligence"));
  // navigation comes from the explicit tag.
  assert.ok(activated.includes("navigation"));
});

// ---------------------------------------------------------------------------
// testsuite-tauri phase-3 — bridge_status
//
// bridge_status is server-resolved: it reads the instance lock from disk
// (classifyInstance) and issues one /ping via the LiveClient, then synthesizes
// a coarse `status` token. The tests below inject a fake LiveClient that
// returns a specific ping body for `unity_open_mcp_ping`, plus a real (empty)
// project path so the lock read resolves to `null` → "gone" classification.
// ---------------------------------------------------------------------------

// A LiveClient fake whose `route` returns a ping-shaped body specifically for
// `unity_open_mcp_ping`, and `isLiveAvailable` derived from that body's
// `connected` flag (so bridge_status's /ping probe + the classifier compose
// the same way they do in production).
function makePingFakeLive(opts: {
  pingBody?: Record<string, unknown> | null;
  pingIsError?: boolean;
  available?: boolean;
  bridgeTools?: Set<string> | null;
}): LiveClient & { calls: LiveCall[] } {
  const calls: LiveCall[] = [];
  const pingBody = opts.pingBody ?? null;
  const pingIsError = opts.pingIsError ?? false;
  const available = opts.available ?? (pingBody?.connected === true);
  const bridgeTools = opts.bridgeTools;
  return {
    calls,
    async isLiveAvailable() {
      return available;
    },
    async route(tool: string, args: Record<string, unknown>) {
      calls.push({ tool, args });
      if (tool === "unity_open_mcp_ping") {
        if (pingBody === null) {
          // Mirror LiveClient.handlePing's offline error shape.
          return {
            content: [
              {
                type: "text",
                text: JSON.stringify({
                  error: { code: "bridge_offline", message: "Cannot connect" },
                }),
              },
            ],
            isError: true,
          } as CallToolResult;
        }
        return {
          content: [{ type: "text", text: JSON.stringify(pingBody) }],
          isError: pingIsError,
        } as CallToolResult;
      }
      return {
        content: [{ type: "text", text: JSON.stringify({ ok: true }) }],
        isError: false,
      } as CallToolResult;
    },
    async listBridgeTools() {
      if (bridgeTools === null) return null;
      if (bridgeTools === undefined) {
        return { tools: new Set<string>(), groups: [] };
      }
      return { tools: bridgeTools, groups: [] };
    },
  } as unknown as LiveClient & { calls: LiveCall[] };
}

test("route: bridge_status returns running when /ping is connected and idle", async () => {
  await withTmp("router-bstatus-running-", async (tmp) => {
    await setupProject(tmp);
    const live = makePingFakeLive({
      pingBody: { connected: true, compiling: false, mode: "live", unityVersion: "6000.0.0" },
    });
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream());

    const result = await router.route("unity_open_mcp_bridge_status", {});
    const body = parseBody(result);
    assert.equal(result.isError, false, "bridge_status never errors");
    assert.equal(body.status, "running");
    assert.equal(body.ready, true);
    assert.equal(body._source, "local");
    const instance = body.instance as { classification: string };
    const ping = body.ping as { reachable: boolean; connected?: boolean; compiling?: boolean };
    assert.equal(instance.classification, "gone"); // no lock file in tmp
    assert.equal(ping.reachable, true);
    assert.equal(ping.connected, true);
    assert.equal(ping.compiling, false);
    assert.equal(live.calls.length, 1);
    assert.equal(live.calls[0].tool, "unity_open_mcp_ping");
  });
});

test("route: bridge_status returns compiling when /ping reports compiling=true", async () => {
  await withTmp("router-bstatus-compiling-", async (tmp) => {
    await setupProject(tmp);
    const live = makePingFakeLive({
      pingBody: { connected: true, compiling: true, mode: "live" },
    });
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream());

    const result = await router.route("unity_open_mcp_bridge_status", {});
    const body = parseBody(result);
    assert.equal(result.isError, false);
    assert.equal(body.status, "compiling");
    assert.equal(body.ready, false);
    const ping = body.ping as { compiling?: boolean };
    assert.equal(ping.compiling, true);
  });
});

test("route: bridge_status returns stopped when bridge is offline", async () => {
  await withTmp("router-bstatus-stopped-", async (tmp) => {
    await setupProject(tmp);
    // No lock file + offline ping → "gone" classification + unreachable ping.
    const live = makePingFakeLive({ pingBody: null, available: false });
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream());

    const result = await router.route("unity_open_mcp_bridge_status", {});
    const body = parseBody(result);
    assert.equal(result.isError, false, "stopped is not an error");
    assert.equal(body.status, "stopped");
    assert.equal(body.ready, false);
    const ping = body.ping as { reachable: boolean };
    const instance = body.instance as { classification: string; lock: unknown };
    assert.equal(ping.reachable, false);
    assert.equal(instance.classification, "gone");
    assert.equal(instance.lock, null);
    assert.ok(typeof body.nextStep === "string" && body.nextStep.length > 0);
  });
});

test("route: bridge_status returns dead_bridge when the lock classifies dead", async () => {
  await withTmp("router-bstatus-dead-", async (tmp) => {
    await setupProject(tmp);
    // Plant a lock whose pid is alive (this test process) but heartbeat is
    // stale — the dead-bridge signature. classifyInstance returns dead_bridge.
    // We redirect homedir() at a sandbox so readInstanceLock sees our planted
    // lock for this project path.
    const sandboxDir = await mkdtemp(join(tmpdir(), "uomcp-bstatus-"));
    const prevHome = process.env.HOME;
    const prevUserProfile = process.env.USERPROFILE;
    process.env.HOME = sandboxDir;
    process.env.USERPROFILE = sandboxDir;
    try {
      const { projectHash } = await import("./instance-discovery.js");
      const hash = projectHash(tmp);
      const instancesDir = join(sandboxDir, ".unity-open-mcp", "instances");
      await mkdir(instancesDir, { recursive: true });
      const stale = new Date(Date.now() - 60_000).toISOString();
      const payload = {
        pid: process.pid,
        port: 24678,
        projectPath: tmp,
        projectHash: hash,
        startedAt: stale,
        updatedAt: stale,
        heartbeatAt: stale,
        state: "reloading",
        isPlaying: false,
        isCompiling: false,
        bridgeVersion: "0.0.0",
        unityVersion: "6000.0.0",
      };
      await writeFile(join(instancesDir, `${hash}.json`), JSON.stringify(payload));

      // Ping unreachable (the dead bridge never answers).
      const live = makePingFakeLive({ pingBody: null, available: false });
      const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream());

      const result = await router.route("unity_open_mcp_bridge_status", {});
      const body = parseBody(result);
      assert.equal(result.isError, false);
      assert.equal(body.status, "dead_bridge");
      const instance = body.instance as { classification: string };
      assert.equal(instance.classification, "dead_bridge");
      assert.ok(
        typeof body.nextStep === "string" && body.nextStep.includes("read_compile_errors"),
        "dead_bridge nextStep points at read_compile_errors",
      );
    } finally {
      if (prevHome === undefined) delete process.env.HOME;
      else process.env.HOME = prevHome;
      if (prevUserProfile === undefined) delete process.env.USERPROFILE;
      else process.env.USERPROFILE = prevUserProfile;
      await rm(sandboxDir, { recursive: true, force: true });
    }
  });
});

test("route: bridge_status returns unreachable when Unity is alive but listener is down (T-fix-3)", async () => {
  // M20 Plan 4-5 / T-fix-3 — a lock with a live PID + fresh heartbeat +
  // state="reloading" classifies as "reloading" (a normal domain reload in
  // flight). With the ping unreachable, status must be "unreachable" — NOT
  // the clean-looking "stopped" that masked the reload-window flakiness
  // pre-fix.
  await withTmp("router-bstatus-unreachable-", async (tmp) => {
    await setupProject(tmp);
    const sandboxDir = await mkdtemp(join(tmpdir(), "uomcp-bstatus-unreach-"));
    const prevHome = process.env.HOME;
    const prevUserProfile = process.env.USERPROFILE;
    process.env.HOME = sandboxDir;
    process.env.USERPROFILE = sandboxDir;
    try {
      const { projectHash } = await import("./instance-discovery.js");
      const hash = projectHash(tmp);
      const instancesDir = join(sandboxDir, ".unity-open-mcp", "instances");
      await mkdir(instancesDir, { recursive: true });
      // Fresh heartbeat (age 0) + live PID + state="reloading" → classifyInstance
      // returns "reloading", NOT dead_bridge (which needs a stale heartbeat).
      const fresh = new Date().toISOString();
      const payload = {
        pid: process.pid,
        port: 24679,
        projectPath: tmp,
        projectHash: hash,
        startedAt: fresh,
        updatedAt: fresh,
        heartbeatAt: fresh,
        state: "reloading",
        isPlaying: false,
        isCompiling: false,
        bridgeVersion: "0.0.0",
        unityVersion: "6000.0.0",
      };
      await writeFile(join(instancesDir, `${hash}.json`), JSON.stringify(payload));

      // Ping unreachable — the listener is torn down during the reload.
      const live = makePingFakeLive({ pingBody: null, available: false });
      const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream());

      const result = await router.route("unity_open_mcp_bridge_status", {});
      const body = parseBody(result);
      assert.equal(result.isError, false, "unreachable is not an error");
      assert.equal(body.status, "unreachable");
      assert.equal(body.ready, false);
      const instance = body.instance as { classification: string };
      assert.equal(instance.classification, "reloading");
      assert.ok(
        typeof body.nextStep === "string" &&
          body.nextStep.toLowerCase().includes("reload"),
        "unreachable nextStep mentions the reload window so the operator retries",
      );
    } finally {
      if (prevHome === undefined) delete process.env.HOME;
      else process.env.HOME = prevHome;
      if (prevUserProfile === undefined) delete process.env.USERPROFILE;
      else process.env.USERPROFILE = prevUserProfile;
      await rm(sandboxDir, { recursive: true, force: true });
    }
  });
});

test("route: bridge_status is registered in ALL_TOOLS and always-visible", async () => {
  const { ALL_TOOLS } = await import("./tools/index.js");
  const { filterVisibleTools } = await import("./tool-session-state.js");
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(names.includes("unity_open_mcp_bridge_status"));
  // A fresh session must still see bridge_status.
  const freshSession = new ToolSessionState();
  const visible = filterVisibleTools(ALL_TOOLS, freshSession).map((t) => t.name);
  assert.ok(
    visible.includes("unity_open_mcp_bridge_status"),
    "bridge_status is always-visible (no group assignment)",
  );
});

// ---------------------------------------------------------------------------
// M27 Plan 1 — cold Safe Mode detection (no lock + live Unity process).
//
// When Unity launches straight into Safe Mode, the bridge's
// [InitializeOnLoad] never runs and no instance lock is written.
// classifyInstance(null) → "gone", which pre-fix folded into the generic
// "stopped". The cold-Safe-Mode branch scans for a live Unity process whose
// -projectPath matches this project; a match reuses the dead_bridge status
// token + recoveryHint so the agent gets the same read_compile_errors
// recovery path as the mid-session dead-bridge case.
//
// The scanner is injected via setUnityProcessScannerForTest so no real
// ps / PowerShell process is spawned.
// ---------------------------------------------------------------------------

test("route: bridge_status returns dead_bridge for cold Safe Mode (no lock + live Unity process)", async () => {
  await withTmp("router-bstatus-coldsafe-", async (tmp) => {
    await setupProject(tmp);
    // No lock file planted → classifyInstance returns "gone".
    const restore = setUnityProcessScannerForTest({
      scan() {
        return [{ pid: 4242, projectPath: tmp }];
      },
    });
    try {
      // Ping unreachable (the bridge never compiled → no listener).
      const live = makePingFakeLive({ pingBody: null, available: false });
      const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream());

      const result = await router.route("unity_open_mcp_bridge_status", {});
      const body = parseBody(result);
      assert.equal(result.isError, false, "dead_bridge is not an error");
      assert.equal(body.status, "dead_bridge");
      assert.equal(body.ready, false);
      // classification is still "gone" — the lock IS absent. The cold-Safe-
      // Mode branch reuses the dead_bridge STATUS token without lying about
      // the lock classification.
      assert.equal(body.classification, "gone");
      const instance = body.instance as {
        classification: string;
        unityProcessPid?: number;
      };
      assert.equal(instance.classification, "gone");
      assert.equal(instance.unityProcessPid, 4242);
      const hint = body.recoveryHint as { tool: string; reason: string } | null;
      assert.ok(hint !== null, "cold Safe Mode surfaces a recoveryHint");
      assert.equal(hint.tool, "unity_open_mcp_read_compile_errors");
      assert.ok(
        typeof body.nextStep === "string" && body.nextStep.includes("read_compile_errors"),
        "cold Safe Mode nextStep points at read_compile_errors",
      );
    } finally {
      restore();
    }
  });
});

test("route: bridge_status still returns stopped when no lock AND no Unity process (genuine offline)", async () => {
  // Regression guard: the cold-Safe-Mode scan must not flip a genuine "Unity
  // is not running" state into dead_bridge. No lock + no matching process →
  // the pre-feature "stopped" answer stands.
  await withTmp("router-bstatus-genuine-stop-", async (tmp) => {
    await setupProject(tmp);
    const restore = setUnityProcessScannerForTest({
      scan() {
        return [];
      },
    });
    try {
      const live = makePingFakeLive({ pingBody: null, available: false });
      const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream());

      const result = await router.route("unity_open_mcp_bridge_status", {});
      const body = parseBody(result);
      assert.equal(body.status, "stopped");
      const instance = body.instance as {
        classification: string;
        unityProcessPid?: number;
      };
      assert.equal(instance.classification, "gone");
      assert.equal(instance.unityProcessPid, undefined);
      assert.equal(body.recoveryHint, null);
    } finally {
      restore();
    }
  });
});

test("route: bridge_status cold Safe Mode — Unity process for a DIFFERENT project does not trigger dead_bridge", async () => {
  // False-positive guard: the scanner must match THIS project's path, not any
  // running Unity. A Unity process editing another project must leave the
  // status at "stopped".
  await withTmp("router-bstatus-other-project-", async (tmp) => {
    await setupProject(tmp);
    const restore = setUnityProcessScannerForTest({
      scan() {
        return [{ pid: 555, projectPath: "/some/other/project" }];
      },
    });
    try {
      const live = makePingFakeLive({ pingBody: null, available: false });
      const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream());

      const result = await router.route("unity_open_mcp_bridge_status", {});
      const body = parseBody(result);
      assert.equal(body.status, "stopped");
    } finally {
      restore();
    }
  });
});

// ---------------------------------------------------------------------------
// M26 Plan 2 — Unity Hub control tools (local-routed).
//
// These resolve entirely in the MCP server (no bridge hop, no batch Unity):
// every response is tagged `_source: "local"`. They live in the
// `unity-hub-control` group (NOT always-visible), so a fresh default session
// must hide them until the group is activated. Mutating members
// (install_editor / install_modules / set_install_path) return the gate-
// consistent mutation/gate/agentNextSteps envelope with gate.skipped=true.
// ---------------------------------------------------------------------------

test("hub control tools are registered in ALL_TOOLS and group-gated (not always-visible)", async () => {
  const { ALL_TOOLS } = await import("./tools/index.js");
  const { filterVisibleTools, groupFor } = await import("./tool-session-state.js");
  const names = ALL_TOOLS.map((t) => t.name);
  const hubTools = [
    "unity_open_mcp_hub_list_editors",
    "unity_open_mcp_hub_available_releases",
    "unity_open_mcp_hub_install_editor",
    "unity_open_mcp_hub_install_modules",
    "unity_open_mcp_hub_get_install_path",
    "unity_open_mcp_hub_set_install_path",
  ];
  for (const name of hubTools) {
    assert.ok(names.includes(name), `${name} must be in ALL_TOOLS`);
    assert.equal(groupFor(name), "unity-hub-control", `${name} must map to unity-hub-control`);
  }
  // A fresh default session must NOT see the hub tools (they're group-gated).
  const freshSession = new ToolSessionState();
  const visible = filterVisibleTools(ALL_TOOLS, freshSession).map((t) => t.name);
  for (const name of hubTools) {
    assert.ok(!visible.includes(name), `${name} must be hidden in a fresh default session`);
  }
  // Activating the group exposes them.
  freshSession.activate("unity-hub-control");
  const visibleAfterActivate = filterVisibleTools(ALL_TOOLS, freshSession).map((t) => t.name);
  for (const name of hubTools) {
    assert.ok(
      visibleAfterActivate.includes(name),
      `${name} must be visible after activating unity-hub-control`,
    );
  }
});

test("route: hub_list_editors returns local source, no bridge hop", async () => {
  await withTmp("router-hub-list-", async (tmp) => {
    const live = makeFakeLive();
    const hub = makeFakeHubBackend({
      editors: [
        { version: "6000.3.18f1", path: "/fake/Unity", platforms: ["Android"], releaseType: "LTS" },
      ],
    });
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream(), undefined, undefined, hub);
    const result = await router.route("unity_open_mcp_hub_list_editors", {});
    const body = parseBody(result);
    assert.equal(result.isError, false);
    assert.equal(body._source, "local");
    assert.ok(Array.isArray(body.editors));
    assert.equal(body.count, 1);
    assert.equal(body.editors[0].version, "6000.3.18f1");
    assert.equal(hub.calls[0], "listInstalledEditors");
    assert.equal(live.calls.length, 0);
  });
});

test("route: hub_list_editors never touches the live client", async () => {
  await withTmp("router-hub-list-nohop-", async (tmp) => {
    const live = makeFakeLive();
    const hub = makeFakeHubBackend();
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream(), undefined, undefined, hub);
    await router.route("unity_open_mcp_hub_list_editors", {});
    // Local-route contract: zero bridge calls.
    assert.equal(live.calls.length, 0);
  });
});

test("route: hub_available_releases returns local source with entries + stale flag", async () => {
  await withTmp("router-hub-releases-", async (tmp) => {
    const live = makeFakeLive();
    const hub = makeFakeHubBackend({
      releases: {
        entries: [
          { version: "6000.3.18f1", stream: "LTS", releaseDate: "2026-06-17", releaseNotesUrl: "u", changeset: "abc" },
        ],
        stale: false,
        fetchedAt: "2026-07-01T00:00:00.000Z",
      },
    });
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream(), undefined, undefined, hub);
    const result = await router.route("unity_open_mcp_hub_available_releases", {});
    const body = parseBody(result);
    assert.equal(result.isError, false);
    assert.equal(body._source, "local");
    assert.ok(Array.isArray(body.entries));
    assert.equal(body.entries.length, 1);
    assert.equal(body.stale, false);
    assert.equal(body.fetchedAt, "2026-07-01T00:00:00.000Z");
    assert.equal(hub.calls[0], "fetchAvailableReleases");
    assert.equal(live.calls.length, 0);
  });
});

test("route: hub_install_editor missing version -> missing_parameter, local source", async () => {
  await withTmp("router-hub-install-missing-", async (tmp) => {
    const live = makeFakeLive();
    const hub = makeFakeHubBackend();
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream(), undefined, undefined, hub);
    const result = await router.route("unity_open_mcp_hub_install_editor", {});
    assert.equal(result.isError, true);
    assert.equal(errorCode(result), "missing_parameter");
    assert.equal(parseBody(result)._source, "local");
    // Missing param short-circuits before the backend is touched.
    assert.equal(hub.calls.length, 0);
    assert.equal(live.calls.length, 0);
  });
});

test("route: hub_install_editor returns mutation envelope with gate skipped (success)", async () => {
  await withTmp("router-hub-install-envelope-", async (tmp) => {
    const live = makeFakeLive();
    const hub = makeFakeHubBackend({ openOk: true });
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream(), undefined, undefined, hub);
    const result = await router.route("unity_open_mcp_hub_install_editor", {
      version: "6000.3.18f1",
      changeset: "5ebeb53e4c07",
    });
    const body = parseBody(result) as Record<string, any>;
    assert.equal(body._source, "local");
    assert.ok(body.mutation, "must carry a mutation envelope");
    assert.equal(body.mutation.success, true);
    assert.equal(body.gate.skipped, true);
    assert.ok(Array.isArray(body.agentNextSteps));
    assert.equal(hub.calls[0], "openInstallDeepLink:6000.3.18f1");
    assert.equal(live.calls.length, 0);
  });
});

test("route: hub_install_editor surfaces a failed deep-link open in the envelope", async () => {
  await withTmp("router-hub-install-fail-", async (tmp) => {
    const live = makeFakeLive();
    const hub = makeFakeHubBackend({ openOk: false });
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream(), undefined, undefined, hub);
    const result = await router.route("unity_open_mcp_hub_install_editor", {
      version: "6000.3.18f1",
    });
    const body = parseBody(result) as Record<string, any>;
    assert.equal(result.isError, true);
    assert.equal(body.mutation.success, false);
    assert.equal(body.mutation.error.code, "deep_link_open_failed");
  });
});

test("route: hub_get_install_path returns local source with source field", async () => {
  await withTmp("router-hub-getpath-", async (tmp) => {
    const live = makeFakeLive();
    const hub = makeFakeHubBackend({
      installPath: { path: "/fake/Hub/Editor", source: "filesystem", error: null },
    });
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream(), undefined, undefined, hub);
    const result = await router.route("unity_open_mcp_hub_get_install_path", {});
    const body = parseBody(result);
    assert.equal(body._source, "local");
    assert.equal(body.path, "/fake/Hub/Editor");
    assert.equal(body.source, "filesystem");
    assert.equal(hub.calls[0], "getInstallPath");
    assert.equal(live.calls.length, 0);
  });
});

test("route: hub_set_install_path missing path -> missing_parameter", async () => {
  await withTmp("router-hub-setpath-missing-", async (tmp) => {
    const live = makeFakeLive();
    const hub = makeFakeHubBackend();
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream(), undefined, undefined, hub);
    const result = await router.route("unity_open_mcp_hub_set_install_path", {});
    assert.equal(result.isError, true);
    assert.equal(errorCode(result), "missing_parameter");
    assert.equal(hub.calls.length, 0);
    assert.equal(live.calls.length, 0);
  });
});

test("route: hub_set_install_path returns mutation envelope", async () => {
  await withTmp("router-hub-setpath-envelope-", async (tmp) => {
    const live = makeFakeLive();
    const hub = makeFakeHubBackend({ setOk: true });
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream(), undefined, undefined, hub);
    const result = await router.route("unity_open_mcp_hub_set_install_path", {
      path: "/some/install/path",
    });
    const body = parseBody(result) as Record<string, any>;
    assert.equal(body._source, "local");
    assert.ok(body.mutation, "must carry a mutation envelope");
    assert.equal(body.mutation.success, true);
    assert.equal(body.gate.skipped, true);
    assert.equal(hub.calls[0], "setInstallPath:/some/install/path");
    assert.equal(live.calls.length, 0);
  });
});

test("route: hub_install_modules returns mutation envelope with modulesRequested", async () => {
  await withTmp("router-hub-modules-", async (tmp) => {
    const live = makeFakeLive();
    const hub = makeFakeHubBackend({ openOk: true });
    const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream(), undefined, undefined, hub);
    const result = await router.route("unity_open_mcp_hub_install_modules", {
      version: "6000.3.18f1",
      modules: ["android", "ios"],
    });
    const body = parseBody(result) as Record<string, any>;
    assert.equal(body._source, "local");
    assert.equal(body.mutation.success, true);
    assert.deepEqual(body.mutation.output.modulesRequested, ["android", "ios"]);
    assert.equal(body.gate.skipped, true);
    assert.equal(hub.calls[0], "openInstallDeepLink:6000.3.18f1");
    assert.equal(live.calls.length, 0);
  });
});

// ---------------------------------------------------------------------------
// M23 Plan 3 — per-request port override routing (routeOverride).
//
// When a per-call `_meta.port` override is present, the router must dispatch
// through the OVERRIDE LiveClient, not the default one. This is the parallel-
// safe primitive: multiple agents sharing one MCP process each target their
// own bridge instance via the override, bypassing shared session state.
// ---------------------------------------------------------------------------

test("routeOverride: dispatches through the override LiveClient, not the default", async () => {
  const defaultLive = makeFakeLive({ result: { content: [{ type: "text", text: JSON.stringify({ from: "default" }) }], isError: false } });
  const overrideLive = makeFakeLive({ result: { content: [{ type: "text", text: JSON.stringify({ from: "override" }) }], isError: false } });
  const batch = makeFakeBatch({ batchTools: new Set() }); // force live path
  const router = makeRouter(defaultLive, batch, "/proj", makeFakeEventStream());

  const result = await router.routeOverride(
    "unity_open_mcp_ping",
    {},
    overrideLive as unknown as LiveClient,
  );

  // The override client was used, not the default.
  assert.equal((overrideLive as unknown as { calls: LiveCall[] }).calls.length, 1);
  assert.equal((defaultLive as unknown as { calls: LiveCall[] }).calls.length, 0);
  assert.equal(parseBody(result).from, "override");
});

test("routeOverride: find_references uses the override client for the live hop", async () => {
  const defaultLive = makeFakeLive();
  const overrideLive = makeFakeLive({
    result: { content: [{ type: "text", text: JSON.stringify({ referencedBy: ["Assets/a.prefab"], totalCount: 1 }) }], isError: false },
  });
  const batch = makeFakeBatch();
  const router = makeRouter(defaultLive, batch, "/proj", makeFakeEventStream());

  await router.routeOverride(
    "unity_open_mcp_find_references",
    { asset_path: "Assets/a.prefab" },
    overrideLive as unknown as LiveClient,
  );

  assert.equal((overrideLive as unknown as { calls: LiveCall[] }).calls.length, 1);
  assert.equal((defaultLive as unknown as { calls: LiveCall[] }).calls.length, 0);
});

test("default route (no override) still uses the default LiveClient", async () => {
  // Regression guard: the common single-bridge path must be unchanged.
  const defaultLive = makeFakeLive();
  const overrideLive = makeFakeLive();
  const batch = makeFakeBatch({ batchTools: new Set() });
  const router = makeRouter(defaultLive, batch, "/proj", makeFakeEventStream());

  await router.route("unity_open_mcp_ping", {});

  assert.equal((defaultLive as unknown as { calls: LiveCall[] }).calls.length, 1);
  assert.equal((overrideLive as unknown as { calls: LiveCall[] }).calls.length, 0);
});


// ---------------------------------------------------------------------------
// M31 Plan 3 — restart_editor (reactive kill) + resource_pressure (proactive
// fd-usage prediction). Both are local-routed operator surfaces that act on
// the OS process, not the bridge. Tests inject a fake process scanner (for the
// PID), a fake ProcessKiller (no real signal), and a fake FdProbe (no real
// lsof / /proc) — same seam pattern as the bridge_status cold-Safe-Mode tests.
// ---------------------------------------------------------------------------

// A log body that carries the editor_fd_exhaustion signature (the exact line
// read_compile_errors matches on). Planting this in <tmp>/Logs/Editor.log
// makes restart_editor's signature check pass.
const FD_EXHAUSTION_LOG =
  "Unhandled exception during build: System.NotSupportedException: " +
  "Could not register to wait for file descriptor 1194\n" +
  "  at System.IOSelector.Add(intptr,System.IOSelectorJob)\n";

// A log body with a plain compile error but NO fd-exhaustion signature — used
// to prove restart_editor refuses on a fixable dead_bridge.
const PLAIN_COMPILE_ERROR_LOG =
  "Assets/Scripts/Bridge.cs(10,14): error CS0118: 'T' is a namespace but is used like a type\n";

/** Helper: write <tmp>/Logs/Editor.log with the given content. */
async function plantEditorLog(tmp: string, content: string): Promise<void> {
  await mkdir(join(tmp, "Logs"), { recursive: true });
  await writeFile(join(tmp, "Logs", "Editor.log"), content);
}

/** Fake LiveClient that answers scene_get_dirty_summary with canned scenes. */
function makeDirtySummaryFakeLive(opts: {
  available?: boolean;
  dirtyScenes?: Array<{ name: string; path: string; isDirty: boolean }>;
}): LiveClient & { calls: LiveCall[] } {
  const calls: LiveCall[] = [];
  const available = opts.available ?? true;
  return {
    calls,
    async isLiveAvailable() {
      return available;
    },
    async route(tool: string, args: Record<string, unknown>) {
      calls.push({ tool, args });
      const body =
        tool === "unity_open_mcp_scene_get_dirty_summary"
          ? { scenes: opts.dirtyScenes ?? [] }
          : { ok: true };
      return {
        content: [{ type: "text", text: JSON.stringify(body) }],
        isError: false,
      } as CallToolResult;
    },
    async listBridgeTools() {
      return { tools: new Set<string>(), groups: [] };
    },
  } as unknown as LiveClient & { calls: LiveCall[] };
}

// --- restart_editor: dry-run (confirm absent) -----------------------------

test("route: restart_editor dry-run returns the diagnosis + wouldKillPid without side effect", async () => {
  await withTmp("router-restart-dryrun-", async (tmp) => {
    await setupProject(tmp);
    await plantEditorLog(tmp, FD_EXHAUSTION_LOG);
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        return [{ pid: 4242, projectPath: tmp }];
      },
    });
    const restoreKiller = setProcessKillerForTest({
      async kill() {
        throw new Error("dry-run must not call the killer");
      },
    });
    try {
      const router = makeRouter(
        makeFakeLive(),
        makeFakeBatch(),
        tmp,
        makeFakeEventStream(),
      );
      const result = await router.route("unity_open_mcp_restart_editor", {});
      const body = parseBody(result);
      assert.equal(result.isError, false, "dry-run is not an error");
      assert.equal(body.confirm, false);
      assert.equal(body.dryRun, true);
      assert.equal(body.signaturePresent, true);
      assert.equal(body.wouldKillPid, 4242);
    } finally {
      restoreKiller();
      restoreScan();
    }
  });
});

// --- restart_editor: refuses without confirmation when called with confirm:false ---

test("route: restart_editor refuses to kill when confirm is false", async () => {
  await withTmp("router-restart-refuse-confirm-", async (tmp) => {
    await setupProject(tmp);
    await plantEditorLog(tmp, FD_EXHAUSTION_LOG);
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        return [{ pid: 4242, projectPath: tmp }];
      },
    });
    let killerCalled = false;
    const restoreKiller = setProcessKillerForTest({
      async kill() {
        killerCalled = true;
        return { terminated: true, pid: 4242, method: "sigterm", elapsedMs: 1 };
      },
    });
    try {
      const router = makeRouter(
        makeFakeLive(),
        makeFakeBatch(),
        tmp,
        makeFakeEventStream(),
      );
      const result = await router.route("unity_open_mcp_restart_editor", {
        confirm: false,
      });
      const body = parseBody(result);
      assert.equal(result.isError, false, "refusal is a structured dry-run, not an error");
      assert.equal(body.dryRun, true);
      assert.equal(killerCalled, false, "killer must not run without confirmation");
    } finally {
      restoreKiller();
      restoreScan();
    }
  });
});

// --- restart_editor: refuses when signature absent (plain compile error) ---

test("route: restart_editor refuses with restart_signature_absent on a plain dead_bridge log", async () => {
  await withTmp("router-restart-no-sig-", async (tmp) => {
    await setupProject(tmp);
    await plantEditorLog(tmp, PLAIN_COMPILE_ERROR_LOG);
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        return [{ pid: 4242, projectPath: tmp }];
      },
    });
    let killerCalled = false;
    const restoreKiller = setProcessKillerForTest({
      async kill() {
        killerCalled = true;
        return { terminated: true, pid: 4242, method: "sigterm", elapsedMs: 1 };
      },
    });
    try {
      const router = makeRouter(
        makeFakeLive(),
        makeFakeBatch(),
        tmp,
        makeFakeEventStream(),
      );
      const result = await router.route("unity_open_mcp_restart_editor", {
        confirm: true,
      });
      const body = parseBody(result);
      assert.equal(result.isError, true, "signature-absent refusal is an error");
      const err = body.error as { code?: string; message?: string };
      assert.equal(err.code, "restart_signature_absent");
      assert.equal(killerCalled, false, "must not kill on a fixable compile failure");
    } finally {
      restoreKiller();
      restoreScan();
    }
  });
});

// --- restart_editor: refuses when no Unity process matches ---

test("route: restart_editor refuses with unity_process_not_found when the scanner finds no match", async () => {
  await withTmp("router-restart-no-proc-", async (tmp) => {
    await setupProject(tmp);
    await plantEditorLog(tmp, FD_EXHAUSTION_LOG);
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        return [];
      },
    });
    const restoreKiller = setProcessKillerForTest({
      async kill() {
        throw new Error("must not be called");
      },
    });
    try {
      const router = makeRouter(
        makeFakeLive(),
        makeFakeBatch(),
        tmp,
        makeFakeEventStream(),
      );
      const result = await router.route("unity_open_mcp_restart_editor", {
        confirm: true,
      });
      const body = parseBody(result);
      assert.equal(result.isError, true);
      const err = body.error as { code?: string };
      assert.equal(err.code, "unity_process_not_found");
    } finally {
      restoreKiller();
      restoreScan();
    }
  });
});

// --- restart_editor: happy path (signature + confirm + PID) kills + reports ---

test("route: restart_editor kills the Editor and reports the killed PID when signature + confirm are present", async () => {
  await withTmp("router-restart-kill-", async (tmp) => {
    await setupProject(tmp);
    await plantEditorLog(tmp, FD_EXHAUSTION_LOG);
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        return [{ pid: 4242, projectPath: tmp }];
      },
    });
    let killCall: { pid: number; graceMs: number } | null = null;
    const restoreKiller = setProcessKillerForTest({
      async kill(pid, graceMs) {
        killCall = { pid, graceMs };
        return { terminated: true, pid, method: "sigterm", elapsedMs: 250 };
      },
    });
    try {
      const router = makeRouter(
        makeFakeLive(),
        makeFakeBatch(),
        tmp,
        makeFakeEventStream(),
      );
      const result = await router.route("unity_open_mcp_restart_editor", {
        confirm: true,
      });
      const body = parseBody(result);
      assert.equal(result.isError, false, "successful kill is not an error");
      assert.equal(body.killed, true);
      assert.equal(body.pid, 4242);
      assert.equal(body.method, "sigterm");
      assert.equal(body.elapsedMs, 250);
      assert.equal(body.confirm, true);
      assert.ok(Array.isArray(body.nextSteps), "nextSteps surfaced for relaunch");
      const captured = killCall as { pid: number; graceMs: number } | null;
      assert.ok(
        captured !== null && captured.pid === 4242,
        "killer dispatched with the resolved PID",
      );
    } finally {
      restoreKiller();
      restoreScan();
    }
  });
});

// --- restart_editor: surfaces dirty scenes when the bridge is still reachable ---

test("route: restart_editor surfaces dirtyScenesWarning when the bridge reports unsaved scenes", async () => {
  await withTmp("router-restart-dirty-", async (tmp) => {
    await setupProject(tmp);
    await plantEditorLog(tmp, FD_EXHAUSTION_LOG);
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        return [{ pid: 4242, projectPath: tmp }];
      },
    });
    const restoreKiller = setProcessKillerForTest({
      async kill(pid) {
        return { terminated: true, pid, method: "sigterm", elapsedMs: 1 };
      },
    });
    try {
      const live = makeDirtySummaryFakeLive({
        available: true,
        dirtyScenes: [
          { name: "Main", path: "Assets/Scenes/Main.unity", isDirty: true },
          { name: "UI", path: "Assets/Scenes/UI.unity", isDirty: false },
        ],
      });
      const router = makeRouter(live, makeFakeBatch(), tmp, makeFakeEventStream());
      const result = await router.route("unity_open_mcp_restart_editor", {
        confirm: true,
      });
      const body = parseBody(result);
      assert.equal(body.killed, true);
      const warning = body.dirtyScenesWarning as
        | Array<{ name: string; path: string; isDirty: boolean }>
        | undefined;
      assert.ok(Array.isArray(warning), "dirtyScenesWarning surfaced");
      assert.equal(warning!.length, 1, "only the dirty scene is reported");
      assert.equal(warning![0].name, "Main");
    } finally {
      restoreKiller();
      restoreScan();
    }
  });
});

// --- restart_editor: kill failure surfaces an error + the reason ---

test("route: restart_editor surfaces a failed kill with the killer's reason", async () => {
  await withTmp("router-restart-fail-", async (tmp) => {
    await setupProject(tmp);
    await plantEditorLog(tmp, FD_EXHAUSTION_LOG);
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        return [{ pid: 4242, projectPath: tmp }];
      },
    });
    const restoreKiller = setProcessKillerForTest({
      async kill(pid) {
        return {
          terminated: false,
          pid,
          reason: "timeout",
          message: "fake: did not exit",
        };
      },
    });
    try {
      const router = makeRouter(
        makeFakeLive(),
        makeFakeBatch(),
        tmp,
        makeFakeEventStream(),
      );
      const result = await router.route("unity_open_mcp_restart_editor", {
        confirm: true,
      });
      const body = parseBody(result);
      assert.equal(result.isError, true, "failed kill is an error");
      assert.equal(body.killed, false);
      assert.equal(body.reason, "timeout");
    } finally {
      restoreKiller();
      restoreScan();
    }
  });
});

// --- restart_editor: tool is registered + always-visible ------------------

test("route: restart_editor is registered in ALL_TOOLS and always-visible", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(
    names.includes("unity_open_mcp_restart_editor"),
    "restart_editor is in ALL_TOOLS",
  );
  const visible = filterVisibleTools(ALL_TOOLS, new ToolSessionState()).map(
    (t) => t.name,
  );
  assert.ok(
    visible.includes("unity_open_mcp_restart_editor"),
    "restart_editor is always-visible (no group assignment)",
  );
});

// --- resource_pressure: happy path with a resolved PID --------------------

test("route: resource_pressure samples fd usage and reports headroom against the Mono ceiling", async () => {
  await withTmp("router-rp-sample-", async (tmp) => {
    await setupProject(tmp);
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        return [{ pid: 7777, projectPath: tmp }];
      },
    });
    const restoreProbe = setFdProbeForTest({
      count() {
        return { count: 900, method: "lsof", approximate: false };
      },
    });
    try {
      const router = makeRouter(
        makeFakeLive(),
        makeFakeBatch(),
        tmp,
        makeFakeEventStream(),
      );
      const result = await router.route("unity_open_mcp_resource_pressure", {});
      const body = parseBody(result);
      assert.equal(result.isError, false);
      assert.equal(body.pid, 7777);
      assert.equal(body.fdCount, 900);
      assert.equal(body.fdMethod, "lsof");
      assert.equal(body.ceiling, 1024);
      // 900/1024 ≈ 0.879 → warn band (≥0.8, <0.9).
      assert.equal(body.state, "warn");
      assert.equal(body.reliable, true);
      const ratio = Number(body.pressureRatio);
      assert.ok(ratio >= 0.8 && ratio < 0.9, `pressureRatio in warn band, got ${ratio}`);
      // The sample is recorded in the session ring for trend detection.
      assert.equal(body.sampleCount, 1);
      assert.ok(
        typeof body.launchContextCaveat === "string" &&
          body.launchContextCaveat.includes("Mono"),
        "launch-context caveat surfaced",
      );
      // warn state surfaces an agentNextSteps array.
      assert.ok(Array.isArray(body.agentNextSteps));
      const warning = body.warning as { level?: string } | undefined;
      assert.equal(warning!.level, "warn");
    } finally {
      restoreProbe();
      restoreScan();
    }
  });
});

// --- resource_pressure: critical state at ≥90% ----------------------------

test("route: resource_pressure reports critical state at ≥90% of the ceiling", async () => {
  await withTmp("router-rp-critical-", async (tmp) => {
    await setupProject(tmp);
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        return [{ pid: 7777, projectPath: tmp }];
      },
    });
    const restoreProbe = setFdProbeForTest({
      count() {
        return { count: 980, method: "lsof", approximate: false };
      },
    });
    try {
      const router = makeRouter(
        makeFakeLive(),
        makeFakeBatch(),
        tmp,
        makeFakeEventStream(),
      );
      const result = await router.route("unity_open_mcp_resource_pressure", {});
      const body = parseBody(result);
      assert.equal(body.state, "critical");
      const warning = body.warning as { level?: string };
      assert.equal(warning.level, "critical");
    } finally {
      restoreProbe();
      restoreScan();
    }
  });
});

// --- resource_pressure: ok state stays silent (no warning block) ----------

test("route: resource_pressure ok state omits the warning block", async () => {
  await withTmp("router-rp-ok-", async (tmp) => {
    await setupProject(tmp);
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        return [{ pid: 7777, projectPath: tmp }];
      },
    });
    const restoreProbe = setFdProbeForTest({
      count() {
        return { count: 100, method: "proc", approximate: false };
      },
    });
    try {
      const router = makeRouter(
        makeFakeLive(),
        makeFakeBatch(),
        tmp,
        makeFakeEventStream(),
      );
      const result = await router.route("unity_open_mcp_resource_pressure", {});
      const body = parseBody(result);
      assert.equal(body.state, "ok");
      assert.equal(body.warning, undefined, "no warning on a healthy fd count");
      assert.equal(body.agentNextSteps, undefined);
    } finally {
      restoreProbe();
      restoreScan();
    }
  });
});

// --- resource_pressure: trend detection across successive samples ---------

test("route: resource_pressure detects a leaking trend across successive samples", async () => {
  await withTmp("router-rp-trend-", async (tmp) => {
    await setupProject(tmp);
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        return [{ pid: 7777, projectPath: tmp }];
      },
    });
    // Climb 600 → 700 → 800 → 900: monotonic, delta 300 ≥ 10% of 1024.
    const counts = [600, 700, 800, 900];
    const restoreProbe = setFdProbeForTest({
      count() {
        const next = counts.shift();
        if (next === undefined) return { count: 900, method: "lsof", approximate: false };
        return { count: next, method: "lsof", approximate: false };
      },
    });
    try {
      const router = makeRouter(
        makeFakeLive(),
        makeFakeBatch(),
        tmp,
        makeFakeEventStream(),
      );
      // First three calls build the trend (no leak warning yet on the early
      // samples — the trend needs ≥3 to call it leaking).
      for (let i = 0; i < 3; i++) {
        await router.route("unity_open_mcp_resource_pressure", {});
      }
      // The fourth call (900 fds) is itself warn-level; the trend across the
      // four samples is `leaking`.
      const result = await router.route("unity_open_mcp_resource_pressure", {});
      const body = parseBody(result);
      const trend = body.trend as { state: string; delta: number; sampleCount: number };
      assert.equal(trend.state, "leaking");
      assert.equal(trend.delta, 300);
      assert.equal(trend.sampleCount, 4);
      // leaking surfaces a warning even when the absolute count would only be
      // warn-level — the trend is the actionable signal.
      const warning = body.warning as { level?: string };
      assert.equal(warning.level, "leaking");
    } finally {
      restoreProbe();
      restoreScan();
    }
  });
});

// --- resource_pressure: probe failure surfaces unknown state + reason -----

test("route: resource_pressure surfaces probe failure with fdCount:null + probeReason", async () => {
  await withTmp("router-rp-fail-", async (tmp) => {
    await setupProject(tmp);
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        return [{ pid: 7777, projectPath: tmp }];
      },
    });
    const restoreProbe = setFdProbeForTest({
      count() {
        return {
          count: null,
          method: "lsof",
          reason: "not_found",
          message: "no such process",
        };
      },
    });
    try {
      const router = makeRouter(
        makeFakeLive(),
        makeFakeBatch(),
        tmp,
        makeFakeEventStream(),
      );
      const result = await router.route("unity_open_mcp_resource_pressure", {});
      const body = parseBody(result);
      assert.equal(result.isError, false, "probe failure is not a tool error");
      assert.equal(body.fdCount, null);
      assert.equal(body.state, "unknown");
      assert.equal(body.probeReason, "not_found");
      assert.equal(body.reliable, false);
    } finally {
      restoreProbe();
      restoreScan();
    }
  });
});

// --- resource_pressure: explicit pid arg skips the scan -------------------

test("route: resource_pressure honors an explicit pid arg and skips the process scan", async () => {
  await withTmp("router-rp-explicit-", async (tmp) => {
    await setupProject(tmp);
    let scanCalled = false;
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        scanCalled = true;
        return [];
      },
    });
    const restoreProbe = setFdProbeForTest({
      count() {
        return { count: 50, method: "lsof", approximate: false };
      },
    });
    try {
      const router = makeRouter(
        makeFakeLive(),
        makeFakeBatch(),
        tmp,
        makeFakeEventStream(),
      );
      const result = await router.route("unity_open_mcp_resource_pressure", {
        pid: 9999,
      });
      const body = parseBody(result);
      assert.equal(body.pid, 9999);
      assert.equal(scanCalled, false, "explicit pid skips the scan");
    } finally {
      restoreProbe();
      restoreScan();
    }
  });
});

// --- resource_pressure: refuses when no PID resolves ----------------------

test("route: resource_pressure refuses with unity_process_not_found when no scan match + no pid arg", async () => {
  await withTmp("router-rp-nopid-", async (tmp) => {
    await setupProject(tmp);
    const restoreScan = setUnityProcessScannerForTest({
      scan() {
        return [];
      },
    });
    const restoreProbe = setFdProbeForTest({
      count() {
        throw new Error("probe must not run without a PID");
      },
    });
    try {
      const router = makeRouter(
        makeFakeLive(),
        makeFakeBatch(),
        tmp,
        makeFakeEventStream(),
      );
      const result = await router.route("unity_open_mcp_resource_pressure", {});
      const body = parseBody(result);
      assert.equal(result.isError, true);
      const err = body.error as { code?: string };
      assert.equal(err.code, "unity_process_not_found");
    } finally {
      restoreProbe();
      restoreScan();
    }
  });
});

// --- resource_pressure: tool is registered + always-visible --------------

test("route: resource_pressure is registered in ALL_TOOLS and always-visible", () => {
  const names = ALL_TOOLS.map((t) => t.name);
  assert.ok(
    names.includes("unity_open_mcp_resource_pressure"),
    "resource_pressure is in ALL_TOOLS",
  );
  const visible = filterVisibleTools(ALL_TOOLS, new ToolSessionState()).map(
    (t) => t.name,
  );
  assert.ok(
    visible.includes("unity_open_mcp_resource_pressure"),
    "resource_pressure is always-visible (no group assignment)",
  );
});

// ---------------------------------------------------------------------------
// M31-optimizations Plan 1 / H2 — live find_references single-parse pipeline.
//
// The live path used to chain three CallToolResult transforms
// (foldFindReferencesLive → tagSource → injectRouteMeta), each doing its own
// JSON.parse + JSON.stringify on the same payload. The refactor collapses
// that into ONE parse + ONE stringify via transformLiveResult. The contract
// is preserved bit-for-bit: the golden-string test pins the EXACT JSON the
// router emits for a representative payload so any future change to key
// order, dropped fields, or the fold/tag/route stamps is caught.
//
// These tests do not inspect the implementation (the parse/stringify count);
// they pin the OBSERVABLE output shape, which is the contract callers rely
// on. The "single parse" property is a means to that end.
// ---------------------------------------------------------------------------

test("H2: live find_references default profile (no profile arg) preserves the full list + stamps _source/_route", async () => {
  // With no `profile` arg, readProfileAndDetail resolves detail:"summary" but
  // profile:undefined → foldFindReferencesLiveBody takes the balanced/full
  // branch (keeps referencedBy, adds totalCount). _source=live + _route are
  // stamped once. Pin the exact body shape.
  const live = makeFakeLive({
    available: true,
    result: {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            referencedBy: [
              "Assets/Prefabs/Player.prefab",
              "Assets/Materials/Player.mat",
              "Assets/Scripts/Player.cs",
            ],
          }),
        },
      ],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_find_references", {
    guid: "deadbeef",
  });
  assert.equal(live.calls.length, 1);
  const body = parseBody(result);
  assert.equal(body._source, "live");
  assert.deepEqual(body._route, { route: "live" });
  assert.equal(body.totalCount, 3);
  assert.deepEqual(body.referencedBy, [
    "Assets/Prefabs/Player.prefab",
    "Assets/Materials/Player.mat",
    "Assets/Scripts/Player.cs",
  ]);
});

test("H2: live find_references explicit compact profile drops referencedBy + derives byKind", async () => {
  // Explicit profile:"compact" → foldFindReferencesLiveBody drops referencedBy
  // (sets it to []) and derives byKind + totalCount. _source/_route stamped.
  const live = makeFakeLive({
    available: true,
    result: {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            referencedBy: [
              "Assets/Prefabs/Player.prefab",
              "Assets/Materials/Player.mat",
              "Assets/Scripts/Player.cs",
            ],
          }),
        },
      ],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_find_references", {
    guid: "deadbeef",
    profile: "compact",
  });
  const body = parseBody(result);
  assert.equal(body._source, "live");
  assert.deepEqual(body._route, { route: "live" });
  assert.equal(body.totalCount, 3);
  assert.deepEqual(body.referencedBy, []);
  assert.deepEqual(body.byKind, { prefab: 1, mat: 1, cs: 1 });
});

test("H2: live find_references balanced profile with paging keeps page + pagination", async () => {
  const live = makeFakeLive({
    available: true,
    result: {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            referencedBy: ["Assets/A.prefab", "Assets/B.prefab", "Assets/C.prefab"],
          }),
        },
      ],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_find_references", {
    guid: "deadbeef",
    profile: "balanced",
    page_size: 2,
  });
  const body = parseBody(result);
  assert.equal(body._source, "live");
  assert.deepEqual(body._route, { route: "live" });
  assert.equal(body.totalCount, 3);
  assert.deepEqual(body.referencedBy, ["Assets/A.prefab", "Assets/B.prefab"]);
  assert.deepEqual(body.pagination, {
    page_size: 2,
    cursor: null,
    next_cursor: "find_references:2",
    truncated: 1,
  });
});

test("H2: live find_references balanced profile without paging keeps the full list + totalCount", async () => {
  const live = makeFakeLive({
    available: true,
    result: {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            referencedBy: ["Assets/A.prefab", "Assets/B.prefab"],
            bridgeExtraField: "passes-through-untouched",
          }),
        },
      ],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_find_references", {
    guid: "deadbeef",
    profile: "balanced",
  });
  const body = parseBody(result);
  // _source + _route stamped exactly once each (no duplication from the old
  // triple-cycle chain).
  assert.equal(body._source, "live");
  assert.deepEqual(body._route, { route: "live" });
  assert.equal(body.totalCount, 2);
  assert.deepEqual(body.referencedBy, ["Assets/A.prefab", "Assets/B.prefab"]);
  // Bridge-supplied extra fields pass through the single-parse pipeline.
  assert.equal(body.bridgeExtraField, "passes-through-untouched");
});

test("H2: live dependencies single-parse preserves _source + _route (no fold)", async () => {
  // routeDependencies has no fold step; transformLiveResult just stamps
  // _source + _route. Pin the output so the refactor keeps the contract.
  const live = makeFakeLive({
    available: true,
    result: {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            forward: [{ asset: "Assets/A.prefab", guid: "aaa" }],
            reverse: [{ asset: "Assets/B.mat", guid: "bbb" }],
            forwardCount: 1,
            reverseCount: 1,
          }),
        },
      ],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_dependencies", {
    asset_path: "Assets/Foo.prefab",
  });
  const body = parseBody(result);
  assert.equal(body._source, "live");
  assert.deepEqual(body._route, { route: "live" });
  // Body is otherwise untouched (no fold step on this path).
  assert.equal(body.forwardCount, 1);
  assert.equal(body.reverseCount, 1);
});

test("H2: verify route (validate_edit) single-parse keeps _route stamping byte-identical", async () => {
  // routeVerifyResult used to do injectRouteMeta → parseResultBody →
  // withResultBody (2 parses + 2 stringifies). The refactor stamps _route on
  // the parsed body before folding, then stringifies once. Verify the output
  // shape is unchanged: _route present, fold applied (issueCount +
  // issuesBySeverity derived, issues preserved on balanced profile).
  const live = makeFakeLive({
    available: true,
    result: {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            passed: true,
            rulesApplied: ["missing_references"],
            issues: [
              { severity: "error", ruleId: "missing_references", assetPath: "Assets/X.prefab" },
              { severity: "warn", ruleId: "missing_references", assetPath: "Assets/Y.mat" },
            ],
          }),
        },
      ],
      isError: false,
    },
  });
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_validate_edit", {
    paths: ["Assets"],
    profile: "balanced",
  });
  const body = parseBody(result);
  assert.deepEqual(body._route, { route: "live" });
  assert.equal(body.passed, true);
  assert.equal(body.issueCount, 2);
  assert.deepEqual(body.issuesBySeverity, { error: 1, warn: 1 });
  assert.equal(Array.isArray(body.issues) && body.issues.length, 2);
});

test("H2: live find_references body that fails to parse passes through untouched", async () => {
  // Regression guard: when the bridge returns a non-JSON body, the
  // transformLiveResult path must leave the result verbatim (no stamps), the
  // same no-op behavior tagSource/injectRouteMeta had.
  const live = makeFakeLive({
    available: true,
    result: {
      content: [{ type: "text", text: "<<<not json>>>" }],
      isError: true,
    },
  });
  const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());

  const result = await router.route("unity_open_mcp_find_references", {
    guid: "deadbeef",
  });
  assert.equal(result.isError, true);
  // Body is the raw text — no _source / _route stamp on a non-JSON body.
  const text = (result.content[0] as { text: string }).text;
  assert.equal(text, "<<<not json>>>");
});

// ---------------------------------------------------------------------------
// M31-optimizations Plan 1 / L14 — route-logging env gate.
//
// The per-dispatch `[unity-open-mcp] Route: <tool> -> live` trace was
// unconditional. It is gated behind UNITY_OPEN_MCP_DEBUG / UNITY_OPEN_MCP_VERBOSE
// (resolved once at module load into ROUTE_LOG_ENABLED). Default-off; opt-in
// via env. Genuine error-path logging stays unconditional — but the happy-
// path route line is silent unless the flag is set.
//
// Two halves of the contract:
//   1. resolveRouteLogEnabled (pure helper) — tested directly for env
//      combinations (set / unset / both / neither / wrong value).
//   2. ROUTE_LOG_ENABLED (captured at module load) — the default-off posture
//      is asserted end-to-end by capturing console.error during a happy-path
//      dispatch in the test process, which does not set the env.
// ---------------------------------------------------------------------------

test("L14: resolveRouteLogEnabled returns false for an empty env", () => {
  assert.equal(resolveRouteLogEnabled({}), false);
});

test("L14: resolveRouteLogEnabled returns true when UNITY_OPEN_MCP_DEBUG=1", () => {
  assert.equal(resolveRouteLogEnabled({ UNITY_OPEN_MCP_DEBUG: "1" }), true);
});

test("L14: resolveRouteLogEnabled returns true when UNITY_OPEN_MCP_VERBOSE=1", () => {
  assert.equal(resolveRouteLogEnabled({ UNITY_OPEN_MCP_VERBOSE: "1" }), true);
});

test("L14: resolveRouteLogEnabled returns false for non-'1' values (e.g. '0', 'true', unset)", () => {
  // Strict equality with "1" — anything else is treated as off. This is the
  // documented opt-in posture ("set UNITY_OPEN_MCP_DEBUG=1"); a stray "true"
  // or "yes" must NOT accidentally enable the trace.
  assert.equal(resolveRouteLogEnabled({ UNITY_OPEN_MCP_DEBUG: "0" }), false);
  assert.equal(resolveRouteLogEnabled({ UNITY_OPEN_MCP_DEBUG: "true" }), false);
  assert.equal(resolveRouteLogEnabled({ UNITY_OPEN_MCP_DEBUG: "yes" }), false);
  assert.equal(resolveRouteLogEnabled({ UNITY_OPEN_MCP_VERBOSE: "0" }), false);
});

test("L14: ROUTE_LOG_ENABLED captured at module load reflects the test process env", () => {
  // The test runner does NOT set UNITY_OPEN_MCP_DEBUG / UNITY_OPEN_MCP_VERBOSE,
  // so the captured boolean is false (default-off). This is the production
  // posture for any consumer that has not opted in.
  assert.equal(
    process.env.UNITY_OPEN_MCP_DEBUG ?? "",
    "",
    "test harness must not set UNITY_OPEN_MCP_DEBUG (would invalidate this assertion)",
  );
  assert.equal(
    process.env.UNITY_OPEN_MCP_VERBOSE ?? "",
    "",
    "test harness must not set UNITY_OPEN_MCP_VERBOSE (would invalidate this assertion)",
  );
  assert.equal(ROUTE_LOG_ENABLED_AT_LOAD, false);
});

test("L14: with the debug env UNSET, a happy-path dispatch emits no [unity-open-mcp] Route: line", async () => {
  // End-to-end assertion of the default-off posture: capture console.error
  // during a live dispatch and verify zero Route lines are emitted.
  const original = console.error;
  const captured: string[] = [];
  console.error = (...args: unknown[]) => {
    captured.push(args.map(String).join(" "));
  };
  try {
    const live = makeFakeLive({ available: true });
    const router = makeRouter(live, makeFakeBatch(), "/proj", makeFakeEventStream());
    await router.route("unity_open_mcp_editor_get_tags", {});
    const routeLines = captured.filter((line) =>
      line.includes("[unity-open-mcp] Route:"),
    );
    assert.equal(
      routeLines.length,
      0,
      "happy-path route trace must be silent by default (set UNITY_OPEN_MCP_DEBUG=1 to re-enable)",
    );
  } finally {
    console.error = original;
  }
});

