// Tests for withSchemaDefaults — the single source of truth that makes a tool's
// documented `timeout_ms` default actually reach downstream layers when an MCP
// client omits the field. See schema-defaults.ts for the bug this fixes.

import { test } from "node:test";
import assert from "node:assert/strict";
import type { Tool } from "@modelcontextprotocol/sdk/types.js";

import { withSchemaDefaults, missingRequiredArgs } from "./schema-defaults.js";
import { runTests } from "./tools/run-tests.js";
import { regressionCheck } from "./tools/regression-check.js";

function toolWith(properties: Record<string, Record<string, unknown>>): Tool {
  return {
    name: "test_tool",
    description: "d",
    inputSchema: { type: "object", properties },
  };
}

test("withSchemaDefaults: fills missing timeout_ms from schema default", () => {
  const tool = toolWith({
    timeout_ms: { type: "integer", default: 60000, minimum: 1000, maximum: 600000 },
  });
  const out = withSchemaDefaults(tool, {});
  assert.equal(out.timeout_ms, 60000);
});

test("withSchemaDefaults: preserves caller-supplied timeout_ms", () => {
  const tool = toolWith({
    timeout_ms: { type: "integer", default: 60000, minimum: 1000, maximum: 600000 },
  });
  const out = withSchemaDefaults(tool, { timeout_ms: 120000 });
  assert.equal(out.timeout_ms, 120000);
});

test("withSchemaDefaults: explicit null/undefined override default", () => {
  const tool = toolWith({
    timeout_ms: { type: "integer", default: 60000 },
  });
  const outNull = withSchemaDefaults(tool, { timeout_ms: null });
  assert.equal(outNull.timeout_ms, null, "explicit null must be preserved");
});

test("withSchemaDefaults: does not mutate the input args", () => {
  const tool = toolWith({
    timeout_ms: { type: "integer", default: 60000 },
  });
  const args: Record<string, unknown> = {};
  withSchemaDefaults(tool, args);
  assert.ok(!("timeout_ms" in args), "input object must be untouched");
});

test("withSchemaDefaults: applies multiple scalar defaults", () => {
  const tool = toolWith({
    timeout_ms: { type: "integer", default: 60000 },
    play_mode: { type: "boolean", default: false },
    label: { type: "string", default: "x" },
  });
  const out = withSchemaDefaults(tool, { timeout_ms: 5 });
  assert.equal(out.timeout_ms, 5);
  assert.equal(out.play_mode, false);
  assert.equal(out.label, "x");
});

test("withSchemaDefaults: ignores object/array defaults (no deep merge)", () => {
  const tool = toolWith({
    opts: { type: "object", default: { a: 1 } },
    list: { type: "array", default: [1, 2] },
    timeout_ms: { type: "integer", default: 60000 },
  });
  const out = withSchemaDefaults(tool, {});
  assert.ok(!("opts" in out), "object default must not be injected");
  assert.ok(!("list" in out), "array default must not be injected");
  assert.equal(out.timeout_ms, 60000);
});

test("withSchemaDefaults: returns copy when no properties declared", () => {
  const tool = toolWith({});
  const args = { foo: 1 };
  const out = withSchemaDefaults(tool, args);
  assert.deepEqual(out, { foo: 1 });
  assert.notEqual(out, args);
});

test("withSchemaDefaults: handles tool with no properties field", () => {
  const tool: Tool = {
    name: "bare",
    description: "d",
    inputSchema: { type: "object" },
  };
  const out = withSchemaDefaults(tool, { foo: 1 });
  assert.deepEqual(out, { foo: 1 });
});

// Regression for the actual reported bug: an agent calling run_tests with no
// arguments must now receive the documented 60s default, not 30s.
test("withSchemaDefaults: run_tests schema default is 60000", () => {
  const out = withSchemaDefaults(runTests, {});
  assert.equal(
    out.timeout_ms,
    60000,
    "run_tests must default to 60s — the documented value that previously never reached the bridge",
  );
});

// ---------------------------------------------------------------------------
// M15 — missingRequiredArgs: the SDK does not validate `required`; this does.
// ---------------------------------------------------------------------------

function toolRequired(required: string[], properties: Record<string, Record<string, unknown>>): Tool {
  return {
    name: "test_tool",
    description: "d",
    inputSchema: { type: "object", properties, required },
  };
}

test("missingRequiredArgs: returns [] when there is no `required` array", () => {
  const tool = toolWith({ timeout_ms: { type: "integer", default: 60000 } });
  assert.deepEqual(missingRequiredArgs(tool, {}), []);
});

test("missingRequiredArgs: lists every missing required field", () => {
  const tool = toolRequired(["baseline_path", "threshold", "label"], {
    baseline_path: { type: "string" },
    threshold: { type: "integer" },
    label: { type: "string" },
  });
  assert.deepEqual(
    missingRequiredArgs(tool, {}),
    ["baseline_path", "threshold", "label"],
  );
});

test("missingRequiredArgs: treats null and undefined as missing, keeps present values", () => {
  const tool = toolRequired(["a", "b", "c"], {
    a: { type: "string" },
    b: { type: "integer" },
    c: { type: "string" },
  });
  assert.deepEqual(
    missingRequiredArgs(tool, { a: "x", b: null }),
    ["b", "c"],
  );
  assert.deepEqual(
    missingRequiredArgs(tool, { a: "x", b: 0, c: "" }),
    [],
    "falsy-but-present values (0, empty string) are NOT missing",
  );
});

test("M15: regression_check without baseline_path is flagged (was spawned as --baseline-path undefined)", () => {
  // The exact defect from the finding: regression_check declares baseline_path
  // as required; without validation the literal `undefined` reached the Unity
  // spawn line. missingRequiredArgs must catch it before dispatch.
  assert.deepEqual(missingRequiredArgs(regressionCheck, {}), ["baseline_path"]);
  assert.deepEqual(
    missingRequiredArgs(regressionCheck, { baseline_path: "CI/baseline.json" }),
    [],
  );
});

