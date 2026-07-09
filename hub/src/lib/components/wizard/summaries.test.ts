import { test } from "node:test";
import assert from "node:assert/strict";

import {
  bridgeSummary,
  describePingErrorMessage,
  launchSummary,
  mcpSummary,
  packagesSummary,
  pingStatusTone,
  type Step5ItemState,
} from "./summaries.ts";
import type { ProjectState } from "../../services/config.ts";

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

const HEUR = {
  cursor: false,
  claudeDesktop: false,
  opencodeGlobal: false,
  opencodeProject: false,
  zcodeGlobal: false,
  zcodeProject: false,
};

// ---- packagesSummary ----

test("packagesSummary: unknown when no detection", () => {
  assert.deepEqual(packagesSummary(null), {
    tone: "missing",
    label: "unknown",
  });
});

test("packagesSummary: installed when both packages present", () => {
  assert.deepEqual(packagesSummary(detection()), {
    tone: "ok",
    label: "installed",
  });
});

test("packagesSummary: partial when only one package", () => {
  assert.deepEqual(
    packagesSummary(detection({ verifyInstalled: false })),
    { tone: "warn", label: "partial" },
  );
});

test("packagesSummary: not installed when neither present", () => {
  assert.deepEqual(
    packagesSummary(
      detection({ bridgeInstalled: false, verifyInstalled: false }),
    ),
    { tone: "missing", label: "not installed" },
  );
});

// ---- mcpSummary ----

test("mcpSummary: not detected when no detection", () => {
  assert.deepEqual(
    mcpSummary({
      detection: null,
      mcpWritten: false,
      mcpClient: "cursor",
    }),
    { tone: "muted", label: "not detected" },
  );
});

test("mcpSummary: configured when heuristic flag set", () => {
  assert.deepEqual(
    mcpSummary({
      detection: detection({ mcpConfigured: { ...HEUR, cursor: true } }),
      mcpWritten: false,
      mcpClient: "cursor",
    }),
    { tone: "ok", label: "configured" },
  );
});

test("mcpSummary: written when just wrote config", () => {
  assert.deepEqual(
    mcpSummary({
      detection: detection({ mcpConfigured: HEUR }),
      mcpWritten: true,
      mcpClient: "cursor",
    }),
    { tone: "ok", label: "written" },
  );
});

test("mcpSummary: claude-code → cli command tone", () => {
  assert.deepEqual(
    mcpSummary({
      detection: detection({ mcpConfigured: HEUR }),
      mcpWritten: false,
      mcpClient: "claude-code",
    }),
    { tone: "warn", label: "cli command" },
  );
});

test("mcpSummary: manual → manual tone", () => {
  assert.deepEqual(
    mcpSummary({
      detection: detection({ mcpConfigured: HEUR }),
      mcpWritten: false,
      mcpClient: "manual",
    }),
    { tone: "warn", label: "manual" },
  );
});

// ---- launchSummary ----

function launchInput(
  launch: Step5ItemState,
  ping: Step5ItemState,
  extra: Partial<Parameters<typeof launchSummary>[0]> = {},
) {
  return launchSummary({
    launch,
    ping,
    bridgeStatus: { kind: "notChecked" },
    launchPid: null,
    ...extra,
  });
}

test("launchSummary: ok when launch + ping ok", () => {
  assert.deepEqual(launchInput("ok", "ok"), { tone: "ok", label: "ok" });
});

test("launchSummary: pending bridge when launch ok but ping not ok", () => {
  assert.deepEqual(launchInput("ok", "pending"), {
    tone: "warn",
    label: "ok · bridge pending",
  });
});

test("launchSummary: failed when launch failed", () => {
  assert.deepEqual(launchInput("failed", "failed"), {
    tone: "warn",
    label: "failed",
  });
});

test("launchSummary: not run when never launched", () => {
  assert.deepEqual(launchInput("pending", "pending"), {
    tone: "muted",
    label: "not run",
  });
});

test("launchSummary: pending when launched but no result yet", () => {
  assert.deepEqual(launchInput("pending", "pending", { launchPid: 99 }), {
    tone: "muted",
    label: "pending",
  });
});

// ---- bridgeSummary ----

test("bridgeSummary: connected vs responded", () => {
  assert.deepEqual(
    bridgeSummary({
      kind: "ok",
      connected: true,
      projectPath: null,
      compiling: false,
      isPlaying: false,
    }),
    { tone: "ok", label: "connected" },
  );
  assert.deepEqual(
    bridgeSummary({
      kind: "ok",
      connected: false,
      projectPath: null,
      compiling: false,
      isPlaying: false,
    }),
    { tone: "ok", label: "responded" },
  );
});

test("bridgeSummary: failed", () => {
  assert.deepEqual(bridgeSummary({ kind: "failed", message: "x" }), {
    tone: "warn",
    label: "failed",
  });
});

test("bridgeSummary: not checked", () => {
  assert.deepEqual(bridgeSummary({ kind: "notChecked" }), {
    tone: "muted",
    label: "not checked",
  });
});

// ---- pingStatusTone ----

test("pingStatusTone: maps each item state", () => {
  assert.equal(pingStatusTone("ok"), "ok");
  assert.equal(pingStatusTone("failed"), "warn");
  assert.equal(pingStatusTone("running"), "info");
  assert.equal(pingStatusTone("pending"), "muted");
});

// ---- describePingErrorMessage ----

test("describePingErrorMessage: null → dash", () => {
  assert.equal(describePingErrorMessage(null), "—");
});

test("describePingErrorMessage: ok result carries duration", () => {
  assert.equal(
    describePingErrorMessage({
      port: 42100,
      ok: true,
      connected: true,
      projectPath: "/p",
      compiling: false,
      isPlaying: false,
      durationMs: 42,
      errorKind: "",
      errorMessage: "",
    }),
    "connected=true in 42 ms",
  );
});
