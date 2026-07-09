import { test } from "node:test";
import assert from "node:assert/strict";

import {
  diagTone,
  diagnosticsRows,
  mcpConfiguredSummary,
  splitDiagnosticsGroups,
  type DiagRow,
} from "./diagnostics.ts";
import type { NodeProbe, ProjectState } from "../../services/config.ts";

function detection(overrides: Partial<ProjectState> = {}): ProjectState {
  return {
    path: "/p",
    name: "p",
    isValidUnityProject: true,
    unityVersion: "6000.0.0f1",
    meetsMinUnityVersion: true,
    meetsRecommendedUnityVersion: true,
    manifestPresent: true,
    bridgeInstalled: true,
    verifyInstalled: true,
    mcpConfigured: {
      cursor: false,
      claudeDesktop: false,
      opencodeGlobal: false,
      opencodeProject: false,
      zcodeGlobal: false,
      zcodeProject: false,
    },
    anySkillInstalled: false,
    manifestWritable: true,
    hasSpacesInPath: false,
    bridgeStatus: { kind: "notChecked" },
    unityDomainDeps: [],
    ...overrides,
  };
}

function okNode(): NodeProbe {
  return {
    ok: true,
    version: "v20.0.0",
    major: 20,
    requiredMajor: 18,
    error: null,
  };
}

function rowById(rows: DiagRow[], id: string): DiagRow {
  const r = rows.find((x) => x.id === id);
  assert.ok(r, `expected a row with id=${id}`);
  return r;
}

test("diagnosticsRows: includes the core gate rows", () => {
  const rows = diagnosticsRows({
    detection: detection(),
    nodeProbe: okNode(),
    nodeProbing: false,
    step5BridgeStatus: { kind: "notChecked" },
    step5LaunchPid: null,
  });
  const ids = rows.map((r) => r.id);
  assert.ok(ids.includes("unity-project"));
  assert.ok(ids.includes("unity-version"));
  assert.ok(ids.includes("node"));
  assert.ok(ids.includes("manifest-writable"));
  assert.ok(ids.includes("bridge-installed"));
  assert.ok(ids.includes("verify-installed"));
  assert.ok(ids.includes("mcp-configured"));
});

test("diagnosticsRows: omits bridge-reachable row until Step 5 runs", () => {
  const rows = diagnosticsRows({
    detection: detection(),
    nodeProbe: okNode(),
    nodeProbing: false,
    step5BridgeStatus: { kind: "notChecked" },
    step5LaunchPid: null,
  });
  assert.equal(rows.some((r) => r.id === "bridge-reachable"), false);
});

test("diagnosticsRows: adds bridge-reachable row once Step 5 launched", () => {
  const rows = diagnosticsRows({
    detection: detection(),
    nodeProbe: okNode(),
    nodeProbing: false,
    step5BridgeStatus: { kind: "notChecked" },
    step5LaunchPid: 12345,
  });
  const r = rowById(rows, "bridge-reachable");
  assert.equal(r.ok, false);
  assert.equal(r.detail, "pending");
});

test("diagnosticsRows: bridge-reachable ok when status is ok", () => {
  const rows = diagnosticsRows({
    detection: detection(),
    nodeProbe: okNode(),
    nodeProbing: false,
    step5BridgeStatus: {
      kind: "ok",
      connected: true,
      projectPath: "/p",
      compiling: false,
      isPlaying: false,
    },
    step5LaunchPid: 1,
  });
  const r = rowById(rows, "bridge-reachable");
  assert.equal(r.ok, true);
  assert.equal(r.detail, "connected");
});

test("diagnosticsRows: node row surfaces probe-in-progress detail", () => {
  const rows = diagnosticsRows({
    detection: detection(),
    nodeProbe: null,
    nodeProbing: true,
    step5BridgeStatus: { kind: "notChecked" },
    step5LaunchPid: null,
  });
  const r = rowById(rows, "node");
  assert.equal(r.detail, "probing…");
  assert.equal(r.ok, false);
});

test("diagnosticsRows: failed gate row carries a remediation hint", () => {
  const rows = diagnosticsRows({
    detection: detection({ isValidUnityProject: false }),
    nodeProbe: okNode(),
    nodeProbing: false,
    step5BridgeStatus: { kind: "notChecked" },
    step5LaunchPid: null,
  });
  const r = rowById(rows, "unity-project");
  assert.equal(r.ok, false);
  assert.ok(r.remediation && r.remediation.length > 0);
});

test("diagTone: passing gate → ok", () => {
  assert.equal(
    diagTone({ id: "x", label: "x", ok: true }),
    "ok",
  );
});

test("diagTone: failing gate → warn", () => {
  assert.equal(
    diagTone({ id: "x", label: "x", ok: false }),
    "warn",
  );
});

test("diagTone: informational passing → ok; informational failing → muted", () => {
  assert.equal(
    diagTone({ id: "x", label: "x", ok: true, info: true }),
    "ok",
  );
  assert.equal(
    diagTone({ id: "x", label: "x", ok: false, info: true }),
    "muted",
  );
});

// ---- mcpConfiguredSummary ----

test("mcpConfiguredSummary: empty heuristic → not detected", () => {
  assert.equal(
    mcpConfiguredSummary({
      cursor: false,
      claudeDesktop: false,
      opencodeGlobal: false,
      opencodeProject: false,
      zcodeGlobal: false,
      zcodeProject: false,
    }),
    "not detected",
  );
});

test("mcpConfiguredSummary: lists every configured client", () => {
  assert.equal(
    mcpConfiguredSummary({
      cursor: true,
      claudeDesktop: false,
      opencodeGlobal: true,
      opencodeProject: false,
      zcodeGlobal: false,
      zcodeProject: false,
    }),
    "yes (Cursor, OpenCode (global))",
  );
});

// ---- splitDiagnosticsGroups (Plan 2 Preflight ownership) ----

test("splitDiagnosticsGroups: gate rows are blocking, informational rows are status", () => {
  const rows = diagnosticsRows({
    detection: detection(),
    nodeProbe: okNode(),
    nodeProbing: false,
    step5BridgeStatus: { kind: "notChecked" },
    step5LaunchPid: null,
  });
  const { blocking, status } = splitDiagnosticsGroups(rows);
  // The four true environment gates.
  const blockingIds = blocking.map((r) => r.id);
  assert.deepEqual(
    [...blockingIds].sort(),
    ["manifest-writable", "node", "unity-project", "unity-version"].sort(),
  );
  // The later-step informational rows.
  const statusIds = status.map((r) => r.id);
  assert.deepEqual(
    [...statusIds].sort(),
    ["bridge-installed", "mcp-configured", "verify-installed"].sort(),
  );
});

test("splitDiagnosticsGroups: defaults ungrouped rows to blocking", () => {
  const { blocking, status } = splitDiagnosticsGroups([
    { id: "a", label: "a", ok: true },
    { id: "b", label: "b", ok: false, group: "status" },
  ]);
  assert.equal(blocking.length, 1);
  assert.equal(blocking[0].id, "a");
  assert.equal(status.length, 1);
  assert.equal(status[0].id, "b");
});

test("splitDiagnosticsGroups: bridge-reachable lands in status when present", () => {
  const rows = diagnosticsRows({
    detection: detection(),
    nodeProbe: okNode(),
    nodeProbing: false,
    step5BridgeStatus: { kind: "notChecked" },
    step5LaunchPid: 1,
  });
  const { status } = splitDiagnosticsGroups(rows);
  assert.ok(status.some((r) => r.id === "bridge-reachable"));
});
