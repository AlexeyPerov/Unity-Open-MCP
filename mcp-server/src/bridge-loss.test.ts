// Editor.log evidence that a lost bridge cannot come back on its own — the only
// thing that turns a stopped heartbeat into bridge_compile_failed.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  bridgeCompileFailedBody,
  bridgeLossFromLog,
  readBridgeLossEvidence,
  resetBridgeLossMemoForTest,
} from "./bridge-loss.js";

const BEGIN = "Reloading assemblies after forced synchronous recompile.\nBegin MonoManager ReloadAssembly\n- Loaded All Assemblies, in  0.680 seconds\n";
const BRIDGE_UP = "[Unity Open MCP Bridge] Listening on http://127.0.0.1:27916/\nUnityEngine.Debug:Log (object)\n\n";
const END = "Mono: successfully reloaded assembly\n- Finished resetting the current domain, in  4.007 seconds\nDomain Reload Profiling: 4689ms\n";
const BRIDGE_ERROR = "Packages/com.alexeyperov.unity-open-mcp-bridge/Editor/Bridge/BridgeHttpServer.cs(12,5): error CS1002: ; expected\n";
const USER_ERROR = "Assets/Scripts/Broken.cs(3,1): error CS0246: The type or namespace name 'Nope' could not be found\n";
const TUNDRA_FAILED = "*** Tundra build failed (1.20 seconds), 3 items updated, 120 evaluated\n";
const TUNDRA_OK = "*** Tundra build success (2.10 seconds), 5 items updated, 120 evaluated\n";

/** A healthy session: one reload that started the bridge. */
const HEALTHY = "Unity Editor version:    6000.3.28f1\n" + BEGIN + BRIDGE_UP + END;

test("bridgeLossFromLog: a healthy reload, or one still running, is no evidence — whatever errors the log holds", () => {
  assert.equal(bridgeLossFromLog(HEALTHY), null);
  // A reload in progress (begun, not finished): the bridge comes back with
  // the last good assemblies, so even the bridge's own compile errors are not
  // evidence (Unity does not reload after a failed compile).
  assert.equal(bridgeLossFromLog(HEALTHY + BRIDGE_ERROR + TUNDRA_FAILED + BEGIN), null);
  assert.equal(bridgeLossFromLog(HEALTHY + USER_ERROR + TUNDRA_FAILED + BEGIN, { cold: false }), null);
  assert.equal(bridgeLossFromLog(""), null);
});

test("bridgeLossFromLog: Safe Mode after the bridge last started", () => {
  const tail = HEALTHY + USER_ERROR + TUNDRA_FAILED + "Safe Mode: Only loading a subset of assemblies\n" + BEGIN;
  const evidence = bridgeLossFromLog(tail);
  assert.equal(evidence?.kind, "safe_mode");
  assert.match(evidence!.detail, /Safe Mode: Only loading a subset of assemblies/);
  // Safe Mode left behind: a later reload started the bridge again.
  assert.equal(bridgeLossFromLog(tail + BRIDGE_UP + END), null);
});

test("bridgeLossFromLog: a reload that finished without the bridge starting, with the bridge's own errors first", () => {
  const evidence = bridgeLossFromLog(HEALTHY + USER_ERROR + BRIDGE_ERROR + TUNDRA_FAILED + BEGIN + END);
  assert.equal(evidence?.kind, "bridge_not_loaded");
  assert.match(evidence!.detail, /finished without the bridge starting/);
  assert.equal(evidence!.errors?.[0].file, "Packages/com.alexeyperov.unity-open-mcp-bridge/Editor/Bridge/BridgeHttpServer.cs");
  assert.equal(evidence!.errors?.length, 2);
  // Without errors it is still evidence (an assembly that failed to load, a removed package).
  assert.equal(bridgeLossFromLog(HEALTHY + BEGIN + END)?.kind, "bridge_not_loaded");
});

test("bridgeLossFromLog: no lock yet (cold) — the startup compile's current errors are evidence; superseded ones are not", () => {
  const startup = "Unity Editor version:    6000.3.28f1\n" + USER_ERROR + TUNDRA_FAILED;
  const evidence = bridgeLossFromLog(startup, { cold: true });
  assert.equal(evidence?.kind, "startup_compile_errors");
  assert.equal(evidence!.errors?.[0].code, "CS0246");
  // A starting Editor without errors (still importing / compiling) is not.
  assert.equal(bridgeLossFromLog("Unity Editor version:    6000.3.28f1\n" + BEGIN, { cold: true }), null);
  // Errors a later successful build superseded are historical.
  assert.equal(bridgeLossFromLog(startup + TUNDRA_OK, { cold: true }), null);
  // Mid-session (a lock exists) the same errors are not evidence by themselves.
  assert.equal(bridgeLossFromLog(startup, { cold: false }), null);
});

test("readBridgeLossEvidence: reads the project's Editor.log, memoizes it, and fails open", () => {
  const dir = mkdtempSync(join(tmpdir(), "bl-"));
  try {
    resetBridgeLossMemoForTest();
    const log = join(dir, "Editor.log");
    const project = join(dir, "NoSuchProject");
    writeFileSync(log, HEALTHY + "Safe Mode: Only loading a subset of assemblies\n");
    const evidence = readBridgeLossEvidence(project, { globalLogPathOverride: log });
    assert.equal(evidence?.kind, "safe_mode");
    assert.equal(evidence?.logPath, log);
    assert.equal(readBridgeLossEvidence(project, { globalLogPathOverride: log }), evidence, "same log → memoized answer");
    writeFileSync(log, HEALTHY + "Safe Mode: Only loading a subset of assemblies\n" + BEGIN + BRIDGE_UP + END + "more\n");
    assert.equal(readBridgeLossEvidence(project, { globalLogPathOverride: log }), null, "a changed log is re-read");
    assert.equal(readBridgeLossEvidence(undefined, { globalLogPathOverride: log }), null);
  } finally {
    resetBridgeLossMemoForTest();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("bridgeCompileFailedBody: carries the evidence and points at read_compile_errors", () => {
  const body = bridgeCompileFailedBody({ kind: "safe_mode", detail: "Editor.log shows Unity in Safe Mode.", logPath: "/l" }, { dispatched: null });
  const error = body.error as { code: string; message: string };
  assert.equal(error.code, "bridge_compile_failed");
  assert.match(error.message, /read_compile_errors/);
  assert.match(error.message, /Safe Mode/);
  assert.equal((body.evidence as { kind: string }).kind, "safe_mode");
  assert.equal(body.dispatched, null);
});
