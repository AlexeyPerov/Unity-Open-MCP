// Tests for agent-identity: per-request port override + agent-id extraction.
// Pure function checks over argument objects.

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  PROCESS_AGENT_ID,
  extractRouting,
  parsePort,
  MIN_PORT,
  MAX_PORT,
} from "./agent-identity.js";

// ---------------------------------------------------------------------------
// PROCESS_AGENT_ID shape.
// ---------------------------------------------------------------------------

test("PROCESS_AGENT_ID has the agent-<pid>-<6hex> shape", () => {
  assert.match(
    PROCESS_AGENT_ID,
    /^agent-\d+-[0-9a-f]{6}$/,
    `unexpected PROCESS_AGENT_ID: ${PROCESS_AGENT_ID}`,
  );
});

test("PROCESS_AGENT_ID embeds the current pid", () => {
  assert.ok(PROCESS_AGENT_ID.startsWith(`agent-${process.pid}-`));
});

// ---------------------------------------------------------------------------
// parsePort.
// ---------------------------------------------------------------------------

test("parsePort accepts an integer in range", () => {
  assert.equal(parsePort(24678), 24678);
  assert.equal(parsePort(1), 1);
  assert.equal(parsePort(65535), 65535);
});

test("parsePort rejects out-of-range integers", () => {
  assert.equal(parsePort(0), undefined);
  assert.equal(parsePort(-1), undefined);
  assert.equal(parsePort(65536), undefined);
  assert.equal(parsePort(100000), undefined);
});

test("parsePort rejects non-integer numbers", () => {
  assert.equal(parsePort(24678.5), undefined);
  assert.equal(parsePort(NaN), undefined);
  assert.equal(parsePort(Infinity), undefined);
});

test("parsePort accepts a clean numeric string", () => {
  assert.equal(parsePort("24678"), 24678);
});

test("parsePort rejects a string with trailing junk", () => {
  // parseInt("12abc") === 12 — we must NOT accept that.
  assert.equal(parsePort("12abc"), undefined);
  assert.equal(parsePort("port"), undefined);
  assert.equal(parsePort(""), undefined);
});

test("parsePort rejects wrong types", () => {
  assert.equal(parsePort(undefined), undefined);
  assert.equal(parsePort(null), undefined);
  assert.equal(parsePort(true), undefined);
  assert.equal(parsePort({}), undefined);
});

test("MIN_PORT/MAX_PORT constants bracket the valid range", () => {
  assert.equal(MIN_PORT, 1);
  assert.equal(MAX_PORT, 65535);
});

// ---------------------------------------------------------------------------
// extractRouting.
// ---------------------------------------------------------------------------

test("extractRouting: no meta → no override, process agent id, args unchanged", () => {
  const args = { gate: "enforce", paths_hint: ["Assets"] };
  const r = extractRouting(args);
  assert.equal(r.portOverride, undefined);
  assert.equal(r.agentId, PROCESS_AGENT_ID);
  assert.deepEqual(r.strippedArgs, args);
  // Original object is not mutated.
  assert.deepEqual(args, { gate: "enforce", paths_hint: ["Assets"] });
});

test("extractRouting: _meta.port (number) → port override, _meta stripped", () => {
  const args = { _meta: { port: 24678 }, gate: "enforce" };
  const r = extractRouting(args);
  assert.equal(r.portOverride, 24678);
  assert.equal(r.agentId, PROCESS_AGENT_ID);
  assert.ok(!("_meta" in r.strippedArgs), "_meta should be stripped");
  assert.deepEqual(r.strippedArgs, { gate: "enforce" });
});

test("extractRouting: _meta.port (numeric string) → port override", () => {
  const r = extractRouting({ _meta: { port: "24678" } });
  assert.equal(r.portOverride, 24678);
});

test("extractRouting: _meta.port invalid → ignored (no override)", () => {
  const r = extractRouting({ _meta: { port: 99999 } });
  assert.equal(r.portOverride, undefined);
});

test("extractRouting: top-level port → port override, stripped from args", () => {
  const r = extractRouting({ port: 24678, gate: "off" });
  assert.equal(r.portOverride, 24678);
  assert.ok(!("port" in r.strippedArgs));
  assert.deepEqual(r.strippedArgs, { gate: "off" });
});

test("extractRouting: _meta.port wins over top-level port", () => {
  const r = extractRouting({ _meta: { port: 24678 }, port: 11111 });
  assert.equal(r.portOverride, 24678);
});

test("extractRouting: _meta.agentId → agent override", () => {
  const r = extractRouting({ _meta: { agentId: "agent-explicit-1" } });
  assert.equal(r.agentId, "agent-explicit-1");
  assert.ok(!("_meta" in r.strippedArgs));
});

test("extractRouting: _meta.agentId empty → falls back to process id", () => {
  const r = extractRouting({ _meta: { agentId: "" } });
  assert.equal(r.agentId, PROCESS_AGENT_ID);
});

test("extractRouting: _meta non-object → ignored gracefully", () => {
  const r = extractRouting({ _meta: "not-an-object", port: 24678 });
  assert.equal(r.portOverride, 24678); // falls back to top-level port
  assert.equal(r.agentId, PROCESS_AGENT_ID);
  assert.ok(!("_meta" in r.strippedArgs));
});

test("extractRouting: both port + agentId overrides", () => {
  const r = extractRouting({
    _meta: { port: 24678, agentId: "agent-x" },
    gate: "enforce",
  });
  assert.equal(r.portOverride, 24678);
  assert.equal(r.agentId, "agent-x");
  assert.deepEqual(r.strippedArgs, { gate: "enforce" });
});

test("extractRouting: strips _meta but preserves nested tool args named 'port' is NOT stripped", () => {
  // Only the TOP-LEVEL `port` key is stripped. A nested field named port inside
  // a real arg must survive.
  const args = { component_types: ["Rigidbody"], value: { port: 8080 } };
  const r = extractRouting(args);
  assert.equal(r.portOverride, undefined); // not a top-level port
  assert.deepEqual(r.strippedArgs, args);
});
