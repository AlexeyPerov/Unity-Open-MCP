import test from "node:test";
import assert from "node:assert/strict";

import { ALL_RESOURCES } from "./index.js";

test("ALL_RESOURCES has exactly three resources", () => {
  assert.equal(ALL_RESOURCES.length, 3);
});

test("health/summary resource is registered with correct URI", () => {
  const r = ALL_RESOURCES.find(
    (r) => r.uri === "unity-open-mcp://health/summary",
  );
  assert.ok(r, "health/summary must be in ALL_RESOURCES");
  assert.equal(r!.mimeType, "application/json");
  assert.ok(r!.name);
});

test("health/baseline resource is registered with correct URI", () => {
  const r = ALL_RESOURCES.find(
    (r) => r.uri === "unity-open-mcp://health/baseline",
  );
  assert.ok(r, "health/baseline must be in ALL_RESOURCES");
  assert.equal(r!.mimeType, "application/json");
  assert.ok(r!.name);
});

test("bridge/status resource is registered with correct URI", () => {
  const r = ALL_RESOURCES.find(
    (r) => r.uri === "unity-open-mcp://bridge/status",
  );
  assert.ok(r, "bridge/status must be in ALL_RESOURCES");
  assert.equal(r!.mimeType, "application/json");
  assert.ok(r!.name);
});

test("resource URIs are stable and deterministic across reads", () => {
  const uris1 = ALL_RESOURCES.map((r) => r.uri).sort();
  const uris2 = ALL_RESOURCES.map((r) => r.uri).sort();
  assert.deepEqual(uris1, uris2);
  assert.deepEqual(uris1, [
    "unity-open-mcp://bridge/status",
    "unity-open-mcp://health/baseline",
    "unity-open-mcp://health/summary",
  ]);
});

test("no unintended URIs are exposed", () => {
  const allowed = new Set([
    "unity-open-mcp://health/summary",
    "unity-open-mcp://health/baseline",
    "unity-open-mcp://bridge/status",
  ]);
  for (const r of ALL_RESOURCES) {
    assert.ok(allowed.has(r.uri), `unexpected URI: ${r.uri}`);
  }
});
