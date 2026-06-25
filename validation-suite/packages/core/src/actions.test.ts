/**
 * Tests for the action runner (phase-2 deliverable) using a stub backend.
 * Run with: `npm run test:core`
 */

import test from "node:test";
import assert from "node:assert/strict";

import { resetStep, resetTest, runStep, type ActionBackend, type ActionContext } from "./actions.ts";
import type { ManifestRef, Scenario, StepManifest } from "./types.ts";

const CTX: ActionContext = {
  projectRoot: "/proj",
  fixtureRoot: "/proj/Assets/_ValidationSuite/m9-x",
  // Minimal profile: only companions/toolPrefix matter for the runner.
  profile: {
    id: "unity",
    displayName: "Unity",
    mcpCliBinary: "unity-open-mcp",
    paths: {
      fixtureRoot: "Assets/_ValidationSuite/<test-id>/",
      stateRoot: "UserSettings/ValidationSuite/",
      stateFile: "UserSettings/ValidationSuite/.state.json",
      actualsDir: "UserSettings/ValidationSuite/actuals/",
      exportsDir: "UserSettings/ValidationSuite/exports/",
    },
    markers: { dirs: ["Assets"], files: [] },
    companions: [{ primary: "*.prefab", companion: "*.prefab.meta" }],
    placeholders: ["{fixtureRoot}", "{projectRoot}"],
    toolNamePrefix: "unity_open_mcp_",
  },
};

/** A recording stub backend for deterministic runner tests. */
function makeBackend(): ActionBackend & { calls: string[]; manifests: Map<string, StepManifest> } {
  const calls: string[] = [];
  const manifests = new Map<string, StepManifest>();
  let counter = 0;
  return {
    calls,
    manifests,
    async fsCopy(args) {
      calls.push(`fsCopy ${args.from}→${args.to}`);
      return {
        ok: true,
        summary: `copied ${args.from}→${args.to}`,
        logs: [{ level: "info", message: `copied ${args.from}→${args.to}` }],
        entries: [{ kind: "created", path: args.to }],
      };
    },
    async fsPatch(args) {
      if (args.snapshotOverride !== undefined) {
        calls.push(`restore ${args.path}`);
        return { ok: true, summary: `restored ${args.path}`, logs: [], entries: [] };
      }
      calls.push(`fsPatch ${args.path}`);
      return {
        ok: true,
        summary: `patched ${args.path}`,
        logs: [],
        entries: [{ kind: "modified", path: args.path, snapshot: "orig" }],
      };
    },
    async fsDelete(args) {
      calls.push(`fsDelete ${args.paths.join(",")}`);
      return { ok: true, summary: `deleted ${args.paths.length} path(s)`, logs: [], entries: [] };
    },
    async mcpTool(args) {
      calls.push(`mcpTool ${args.tool}`);
      return {
        ok: true,
        summary: `ran ${args.tool}`,
        logs: [],
        entries: [],
        mcp: { isError: false, result: { ok: true } },
      };
    },
    async saveManifest(manifest) {
      counter += 1;
      const id = `m${counter}`;
      manifests.set(id, manifest);
      calls.push(`saveManifest(${id})`);
      return id;
    },
    async loadManifest(id) {
      return manifests.get(id ?? "") ?? null;
    },
    async deleteManifest(id) {
      manifests.delete(id ?? "");
      calls.push(`deleteManifest(${id})`);
    },
  };
}

// patch the stub to typecheck (the inline `ok` helper above didn't take mcp)
// (kept simple: tests below rely on mcpTool returning a result via closure)

function scenario(steps: Scenario["steps"], reset?: Scenario["reset"]): Scenario {
  return {
    id: "m9-x",
    title: "T",
    milestone: "m9",
    engineId: "unity",
    order: 0,
    requirementLevel: "required-core",
    steps,
    reset,
  };
}

// ── runStep ──────────────────────────────────────────────────────────────────

test("runStep expands placeholders and records a manifest id", async () => {
  const backend = makeBackend();
  const scn = scenario([
    {
      id: "setup",
      type: "setup",
      actions: [
        { action: "fs_copy", from: "Assets/P.prefab", to: "{fixtureRoot}/P.prefab" },
      ],
    },
  ]);
  const res = await runStep(scn, scn.steps[0], CTX, backend);
  assert.equal(res.ok, true);
  assert.equal(backend.calls[0], `fsCopy Assets/P.prefab→${CTX.fixtureRoot}/P.prefab`);
  assert.equal(res.manifestId, "m1");
  assert.equal(backend.manifests.get("m1")!.entries.length, 1);
});

test("runStep stops at first failure and returns ok:false", async () => {
  const backend = makeBackend();
  // Force fsCopy to fail.
  backend.fsCopy = async () => {
    throw new Error("disk full");
  };
  const scn = scenario([
    {
      id: "setup",
      type: "setup",
      actions: [
        { action: "fs_copy", from: "a", to: "b" },
        { action: "fs_delete", paths: ["c"] },
      ],
    },
  ]);
  const res = await runStep(scn, scn.steps[0], CTX, backend);
  assert.equal(res.ok, false);
  // Second action (fs_delete) never ran — execution stopped at the failure.
  assert.ok(backend.calls.every((c) => !c.startsWith("fsDelete")), "second action did not run");
  assert.match(res.logs[0].message, /disk full/);
  // No manifest recorded for a failed run.
  assert.equal(res.manifestId, null);
});

test("runStep manual action records an info log and no manifest entry", async () => {
  const backend = makeBackend();
  const scn = scenario([
    {
      id: "setup",
      type: "setup",
      actions: [{ action: "manual", note: "stop the bridge" }],
    },
  ]);
  const res = await runStep(scn, scn.steps[0], CTX, backend);
  assert.equal(res.ok, true);
  assert.equal(res.manifestId, null); // nothing mutated
  assert.match(res.logs[0].message, /stop the bridge/);
});

// ── resetStep ────────────────────────────────────────────────────────────────

test("resetStep restores created artifacts then runs declared reset actions", async () => {
  const backend = makeBackend();
  const scn = scenario(
    [
      {
        id: "setup",
        type: "setup",
        actions: [{ action: "fs_copy", from: "a", to: "{fixtureRoot}/P.prefab" }],
      },
    ],
    {
      afterStep: {
        setup: { actions: [{ action: "fs_delete", paths: ["{fixtureRoot}"] }] },
      },
    },
  );
  const runRes = await runStep(scn, scn.steps[0], CTX, backend);
  const res = await resetStep(scn, scn.steps[0], CTX, backend, runRes.manifestId);
  // created artifact deleted, declared fs_delete ran, manifest consumed.
  assert.ok(res.warnings.length === 0, JSON.stringify(res.warnings));
  // The manifest entry holds the expanded (absolute) dest path.
  assert.ok(backend.calls.includes(`fsDelete ${CTX.fixtureRoot}/P.prefab`));
  assert.ok(backend.calls.includes(`fsDelete ${CTX.fixtureRoot}`));
  assert.ok(backend.calls.includes("deleteManifest(m1)"));
});

test("resetStep restores a modified file from its snapshot", async () => {
  const backend = makeBackend();
  const scn = scenario([
    {
      id: "setup",
      type: "setup",
      actions: [{ action: "fs_patch", path: "{fixtureRoot}/P.prefab", patches: [{ op: "trim_trailing_whitespace" }] }],
    },
  ]);
  const runRes = await runStep(scn, scn.steps[0], CTX, backend);
  await resetStep(scn, scn.steps[0], CTX, backend, runRes.manifestId);
  assert.ok(backend.calls.some((c) => c.startsWith("restore ")));
});

test("resetStep warns (does not crash) when manifest is missing", async () => {
  const backend = makeBackend();
  const scn = scenario([
    { id: "setup", type: "setup", actions: [{ action: "manual", note: "x" }] },
  ]);
  const res = await resetStep(scn, scn.steps[0], CTX, backend, "no-such-manifest");
  assert.equal(res.ok, false);
  assert.match(res.warnings[0], /No manifest recorded|Could not load/);
});

// ── resetTest ────────────────────────────────────────────────────────────────

test("resetTest reverts setup steps in reverse order", async () => {
  const backend = makeBackend();
  const scn = scenario([
    {
      id: "setup-a",
      type: "setup",
      actions: [{ action: "fs_copy", from: "a", to: "{fixtureRoot}/a" }],
    },
    {
      id: "setup-b",
      type: "setup",
      actions: [{ action: "fs_copy", from: "b", to: "{fixtureRoot}/b" }],
    },
  ]);
  const idA = (await runStep(scn, scn.steps[0], CTX, backend)).manifestId;
  const idB = (await runStep(scn, scn.steps[1], CTX, backend)).manifestId;
  const refs: Record<string, ManifestRef> = { "setup-a": idA, "setup-b": idB };
  await resetTest(scn, CTX, backend, refs);
  // Reverse order: setup-b's delete before setup-a's delete.
  const deleteCalls = backend.calls.filter((c) => c.startsWith("fsDelete "));
  const aIdx = deleteCalls.findIndex((c) => c.endsWith("/a"));
  const bIdx = deleteCalls.findIndex((c) => c.endsWith("/b"));
  assert.ok(aIdx > -1 && bIdx > -1, `both deletes ran: ${JSON.stringify(deleteCalls)}`);
  assert.ok(bIdx < aIdx, `later setup reverted first: ${JSON.stringify(deleteCalls)}`);
});
