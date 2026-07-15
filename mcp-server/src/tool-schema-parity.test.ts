// Tool-schema invariants swept across the full ALL_TOOLS registry.
//
// T6.2 — the project norm is that every tool's `inputSchema` declares
// `additionalProperties: false` so unknown keys are rejected by any client that
// honors JSON Schema (and so a schema reader can trust the declared property
// set). Two schemas (delta, checkpoint_create) were missing it; this guard
// prevents silent drift back to the unguarded state.
//
// These tests treat the schema as the contract surface: they assert the
// declared shape, not runtime arg validation (the MCP server does not run an
// AJV-style validator — the schema is documentation + a client-side contract).

import { test } from "node:test";
import assert from "node:assert/strict";

import { ALL_TOOLS } from "./tools/index.js";
import { delta } from "./tools/delta.js";
import { checkpointCreate } from "./tools/checkpoint-create.js";

function schemaOf(tool: { inputSchema: unknown }): Record<string, unknown> {
  const schema = tool.inputSchema;
  assert.ok(schema && typeof schema === "object", "tool must declare an inputSchema object");
  return schema as Record<string, unknown>;
}

// The norm: every registered tool schema must forbid additional properties.
test("all tool schemas declare additionalProperties: false", () => {
  assert.ok(ALL_TOOLS.length > 250, "ALL_TOOLS should be the full registry");
  const missing: string[] = [];
  for (const tool of ALL_TOOLS) {
    const schema = schemaOf(tool);
    // `additionalProperties: false` is the project norm. A schema that omits it
    // silently accepts junk keys — flag it so the drift is caught here, not in a
    // downstream client that happens to validate.
    if (schema.additionalProperties !== false) {
      missing.push(tool.name);
    }
  }
  assert.deepEqual(missing, [], `tools missing additionalProperties:false: ${missing.join(", ")}`);
});

// Targeted regression for the two schemas T6.2 fixed (delta + checkpoint_create).
// delta requires checkpoint_id; checkpoint_create has NO `required` array by
// design (both fields optional), so without additionalProperties:false an empty
// object OR a junk object would be accepted — the explicit guard is what
// prevents that.
test("delta schema rejects unknown keys (additionalProperties: false)", () => {
  const schema = schemaOf(delta);
  assert.equal(schema.additionalProperties, false);
  assert.deepEqual(schema.required, ["checkpoint_id"]);
});

test("checkpoint_create schema rejects unknown keys despite no required fields", () => {
  const schema = schemaOf(checkpointCreate);
  assert.equal(schema.additionalProperties, false);
  // Both fields are optional — `required` is intentionally absent. The
  // additionalProperties guard is what prevents `{}` or `{ junk: 1 }` from
  // being silently accepted.
  assert.equal(schema.required, undefined, "checkpoint_create fields are all optional");
});

// Simulate a JSON-Schema-style unknown-key check so the contract is demonstrable
// without pulling in a runtime validator. Mirrors what a schema-honoring client
// would do: a key not in `properties` is rejected when additionalProperties is
// false.
test("unknown-key rejection logic matches additionalProperties: false", () => {
  const schema = schemaOf(delta);
  const properties = schema.properties as Record<string, unknown> | undefined;
  assert.ok(properties, "delta must declare properties");
  const allowed = new Set(Object.keys(properties));

  // A valid request (known keys only) is accepted.
  const valid = { checkpoint_id: "abc", paths: ["Assets/Foo.prefab"] };
  for (const key of Object.keys(valid)) {
    assert.ok(allowed.has(key), `valid key '${key}' should be in properties`);
  }

  // An unknown key is NOT in the property set → rejected by the contract.
  const invalid = { checkpoint_id: "abc", bogus: 1 };
  assert.ok(!allowed.has("bogus"), "unknown key 'bogus' must not be in properties");
  assert.equal(schema.additionalProperties, false, "schema forbids the unknown key");
});
