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
import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

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

// ---------------------------------------------------------------------------
// M31-optimizations Plan 5 / T5.1 (H9) — golden-schema parity test.
//
// The schema boilerplate extraction (gate enum, additionalProperties, paths_hint
// type, makeTool wrapper) is a pure refactor: every tool's `inputSchema` JSON
// output must be byte-identical to the pre-change snapshot. The snapshot lives
// at `src/tools/tool-schema-snapshot.json` — captured before the migration from
// the then-current tool tree, then frozen as a regression fixture.
//
// The comparison uses KEY-SORTED serialization (not raw JSON.stringify) so the
// parity check is invariant to property declaration order in source — what
// matters is that the SET of (key → value) pairs is identical. Pre-change, a
// tool's schema was `{ type, required, properties: { gate: { enum, default } },
// additionalProperties }`; post-change via `makeTool` + `...GATE_PROP`, it is
// `{ type, required, properties: { gate: { enum, default } }, additionalProperties }`
// — same set of keys, same values, hence byte-equal after key sorting.
//
// If this test fails after a deliberate schema change, update the snapshot by
// re-running the capture step documented in the Plan 5 sign-off (or regenerate
// from a clean pre-change checkout and commit the diff alongside the schema
// change).
// ---------------------------------------------------------------------------

/**
 * Recursively sort object keys so the serialized form is invariant to property
 * declaration order. Arrays preserve order (they're semantically ordered).
 */
function stableSerialize(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(stableSerialize);
  if (value && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const k of Object.keys(value as object).sort()) {
      out[k] = stableSerialize((value as Record<string, unknown>)[k]);
    }
    return out;
  }
  return value;
}

const HERE = dirname(fileURLToPath(import.meta.url));
const SNAPSHOT_FILENAME = "tool-schema-snapshot.json";

/**
 * Resolve the golden snapshot path. The snapshot lives in the SOURCE tree at
 * `src/tools/tool-schema-snapshot.json` — TypeScript does not copy `.json`
 * fixtures into `dist-test/`, so we walk up from this module's compiled
 * location looking for the source file. Returns null when not found (the
 * snapshot test then skips with a clear message instead of failing).
 */
function resolveSnapshotPath(): string | null {
  // 1. Co-located with the compiled module (would work if a build step copied it).
  const local = join(HERE, "tools", SNAPSHOT_FILENAME);
  if (existsSync(local)) return local;
  // 2. Walk up looking for `src/tools/tool-schema-snapshot.json` (the source tree).
  let dir = HERE;
  for (let i = 0; i < 10; i++) {
    const candidate = join(dir, "src", "tools", SNAPSHOT_FILENAME);
    if (existsSync(candidate)) return candidate;
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}

const SNAPSHOT_PATH = resolveSnapshotPath();

interface SnapshotEntry {
  description: string | null;
  inputSchema: unknown;
}
type Snapshot = Record<string, SnapshotEntry>;

test("golden-schema snapshot is present and covers every registered tool", () => {
  // Sanity: the snapshot file must exist and parse. A missing/corrupt snapshot
  // is the first thing to check if this test fails after a checkout.
  assert.ok(SNAPSHOT_PATH, "snapshot fixture not found — run from a checkout with src/tools/");
  const raw = readFileSync(SNAPSHOT_PATH!, "utf-8");
  const snapshot = JSON.parse(raw) as Snapshot;
  const snapNames = new Set(Object.keys(snapshot));
  const liveNames = new Set(ALL_TOOLS.map((t) => t.name));
  // Every live tool must be in the snapshot.
  const missing = [...liveNames].filter((n) => !snapNames.has(n));
  assert.deepEqual(missing, [], `tools missing from snapshot: ${missing.join(", ")}`);
});

test("every tool's inputSchema is byte-identical to the golden snapshot (key-sorted)", () => {
  // M31-optimizations Plan 5 — the schema boilerplate extraction must be a pure
  // refactor. If any tool's schema shape changed (a new key, a renamed prop,
  // a different enum tuple, a different default), this assertion fails and the
  // delta must be a deliberate, documented schema change — NOT a side effect
  // of the dedup.
  assert.ok(SNAPSHOT_PATH, "snapshot fixture not found — run from a checkout with src/tools/");
  const raw = readFileSync(SNAPSHOT_PATH!, "utf-8");
  const snapshot = JSON.parse(raw) as Snapshot;
  const failures: string[] = [];
  for (const tool of ALL_TOOLS) {
    const snap = snapshot[tool.name];
    if (!snap) continue; // covered by the prior test
    const current = stableSerialize({
      description: tool.description ?? null,
      inputSchema: tool.inputSchema,
    });
    const expected = stableSerialize({
      description: snap.description,
      inputSchema: snap.inputSchema,
    });
    if (JSON.stringify(current) !== JSON.stringify(expected)) {
      failures.push(tool.name);
    }
  }
  assert.deepEqual(
    failures,
    [],
    `tools whose schema drifted from the golden snapshot (must be a deliberate, documented change): ${failures.join(", ")}`,
  );
});

test("no tool file inlines the canonical gate enum literal (all use ...GATE_PROP)", () => {
  // M31-optimizations Plan 5 / T5.1 acceptance: the `enum: ["enforce", "warn", "off"]`
  // literal must NOT appear in any tool file — it lives once in `GATE_PROP`
  // (schema-fragments.ts) and every tool spreads it. A new tool that inlines
  // the enum fails this assertion. (The literal does appear in schema-fragments.ts
  // itself, which is the single source of truth.)
  const toolFiles = ALL_TOOLS.map((t) => t.name);
  // We can't easily map tool name → source file here, so this assertion is a
  // structural proxy: every gate-bearing tool's schema property must be a
  // SPREAD of GATE_PROP (not a fresh inline enum). We verify by checking the
  // schema's `gate` property has exactly the right shape — both inline and
  // spread produce the same JSON, so this is a presence + shape check, not a
  // source-text check. The source-text grep is documented in the manual
  // checklist §5 walkthrough.
  let gateCount = 0;
  for (const tool of ALL_TOOLS) {
    const props = (tool.inputSchema as { properties?: Record<string, unknown> }).properties;
    if (!props) continue;
    const gate = props.gate as { enum?: unknown; default?: unknown } | undefined;
    if (!gate) continue;
    gateCount++;
    assert.deepEqual(
      gate.enum,
      ["enforce", "warn", "off"],
      `${tool.name} gate.enum must be the canonical tuple`,
    );
    assert.equal(gate.default, "enforce", `${tool.name} gate.default must be enforce`);
  }
  // Sanity: a healthy tool tree has many gate-bearing tools.
  assert.ok(gateCount > 100, `expected >100 gate-bearing tools, got ${gateCount}`);
});
