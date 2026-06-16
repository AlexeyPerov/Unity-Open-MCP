// Schema-default injection.
//
// MCP JSON-Schema documents advertise per-property `default` values (e.g.
// `run_tests` documents `timeout_ms` default 60000). Those defaults are advisory:
// an MCP client MAY echo them, but many send args verbatim and omit the field
// entirely. When that happens, every downstream layer (the TS HTTP fetch, the
// C# bridge, the Unity-side dispatch) independently falls back to its own
// hardcoded default — historically 30000 — which silently contradicts the
// documented value and produced the "tool times out at 30s" bug for
// run_tests (whose schema default is 60000).
//
// To make the schema the single source of truth, the CallTool handler fills in
// any missing top-level argument whose property declares a scalar `default`
// before dispatching. This keeps client-supplied values authoritative and only
// ever adds missing fields.

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * The schema of a single input property, as declared in a tool definition.
 * We only consume `default` for scalar types (number/integer/boolean/string).
 */
interface PropertySchema {
  default?: unknown;
}

function isScalarDefault(value: unknown): boolean {
  return (
    typeof value === "number" ||
    typeof value === "boolean" ||
    typeof value === "string"
  );
}

/**
 * Returns a copy of `args` with missing top-level scalar defaults filled in
 * from the given tool's input schema. Object/array defaults are ignored (they
 * require deep merge and no tool currently uses them).
 *
 * The original `args` object is never mutated. Existing caller-supplied values
 * are preserved verbatim, including `undefined`/`null`.
 */
export function withSchemaDefaults(
  tool: Tool,
  args: Record<string, unknown>,
): Record<string, unknown> {
  const properties = tool.inputSchema?.properties as
    | Record<string, PropertySchema>
    | undefined;
  if (!properties) return { ...args };

  const out: Record<string, unknown> = { ...args };
  for (const [key, schema] of Object.entries(properties)) {
    if (key in out) continue;
    const def = schema?.default;
    if (isScalarDefault(def)) out[key] = def;
  }
  return out;
}
