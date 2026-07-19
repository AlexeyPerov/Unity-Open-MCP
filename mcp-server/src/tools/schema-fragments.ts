// M31-optimizations Plan 5 / T5.1 (H9) — shared schema fragments + `makeTool`.
//
// Single source of truth for the schema boilerplate that was duplicated
// across ~265 tool files in this directory. The fragments are SPREAD into a
// per-tool property block; the JSON Schema each tool emits is byte-identical
// to the pre-change inline form (verified by the golden-snapshot test in
// `tool-schema-parity.test.ts`). Per-tool descriptions stay inline where
// they vary — only the truly shared boilerplate (enum/default/type) is
// centralized here.
//
// Design constraints:
//
//   1. **Byte-identical output.** The MCP client sees `inputSchema`
//      post-`JSON.stringify`. Spreading `{ ...GATE_PROP }` yields the same
//      property set + value types as the inline literal (verified by parity
//      test). The `as const` assertions lock the literal types so TypeScript
//      does not widen `enum` to `string[]`.
//
//   2. **Description text stays per-tool where it varies.** Most tools have
//      a unique `paths_hint` description ("Mutation scope — the .anim asset
//      path", etc.) — these are NOT centralized. Only the type/default
//      boilerplate is shared. `ignore_scene_dirty` / `confirm_bypass` carry
//      a small number of distinct wording variants; the base object captures
//      `type + default`, and each tool adds its description inline.
//
//   3. **`makeTool` always adds `additionalProperties: false`.** The project
//      norm (enforced by `tool-schema-parity.test.ts`) is that every tool
//      schema forbids additional properties. Centralizing it here removes
//      265 copies of the literal.
//
// Usage:
//   ```ts
//   export const myTool = makeTool("unity_open_mcp_my_tool", "Does the thing.", {
//     required: ["foo"],
//     properties: {
//       foo: { type: "string", description: "..." },
//       gate: { ...GATE_PROP },
//       paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — …" },
//     },
//   });
//   ```

import type { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * The `gate` property: enum + default. Inlined in 137 tool files pre-change;
 * 128 of those have NO description (pure enum + default), 9 add a per-tool
 * description via `{ ...GATE_PROP, description: "..." }`.
 *
 * `as const` locks the literal tuple so `enum` stays `["enforce","warn","off"]`
 * not `string[]` after the spread.
 */
export const GATE_PROP = {
  enum: ["enforce", "warn", "off"] as ["enforce", "warn", "off"],
  default: "enforce" as const,
};

/**
 * The base `paths_hint` property: array-of-string with NO description. Almost
 * every tool that declares `paths_hint` adds a per-tool description specific
 * to its mutation scope ("Mutation scope — the .anim asset path", etc.).
 * Spread the base and add the description inline:
 *
 *   ```ts
 *   paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — …" },
 *   ```
 *
 * A handful of tools (e.g. `execute-menu.ts`) declare `paths_hint` with no
 * description — for those, spread the base alone.
 */
export const PATHS_HINT_TYPE = {
  type: "array" as const,
  items: { type: "string" as const },
};

/**
 * Base for `ignore_scene_dirty`: boolean, default false, no description. The
 * ~10 tools that declare this property each carry a distinct wording variant
 * (some mention "recompile / scene switch", others mention "menu that can
 * disrupt the editor" or "entering play mode"). Spread the base and add the
 * per-tool description inline so the variants stay byte-identical to
 * pre-change:
 *
 *   ```ts
 *   ignore_scene_dirty: { ...IGNORE_SCENE_DIRTY_BASE, description: "…" },
 *   ```
 */
export const IGNORE_SCENE_DIRTY_BASE = {
  type: "boolean" as const,
  default: false as const,
};

/**
 * Base for `confirm_bypass`: boolean, default false, no description. Each of
 * the 3 tools that declares it (execute_csharp / execute_menu / build_start)
 * carries a distinct description listing the destructive patterns specific to
 * that tool. Spread the base and add the per-tool description inline.
 */
export const CONFIRM_BYPASS_BASE = {
  type: "boolean" as const,
  default: false as const,
};

/**
 * Shape of the per-tool schema body passed to {@link makeTool}. Omits the
 * outer `type: "object"` and `additionalProperties: false` (those are added
 * by `makeTool`) and the wrapper `name` / `description` / `inputSchema`
 * fields (those are added by `makeTool` from the named args).
 *
 * `oneOf` is optional — two tools (`dependencies`, `find_references`) use it
 * at the top level to express "asset_path XOR guid" variants; `makeTool`
 * passes it through to the schema unchanged.
 */
export interface ToolSchemaBody {
  /** Optional list of required property names. */
  required?: string[];
  /** The declared properties (each a JSON-Schema property descriptor). */
  properties: Record<string, object>;
  /** Optional JSON-Schema `oneOf` clause (used by XOR-shape tools). */
  oneOf?: object[];
}

/**
 * Build a {@link Tool} with the project-norm schema envelope: `type: "object"`
 * + `properties` + `additionalProperties: false`. The `additionalProperties`
 * literal lives here once instead of being copy-pasted into every tool file.
 *
 * The body's `properties` record is kept as-is (callers spread shared
 * fragments like `GATE_PROP` into it). The resulting `inputSchema` is
 * byte-identical to the pre-change inline form.
 */
export function makeTool(
  name: string,
  description: string,
  body: ToolSchemaBody,
): Tool {
  return {
    name,
    description,
    inputSchema: {
      type: "object",
      ...(body.required !== undefined ? { required: body.required } : {}),
      properties: body.properties,
      ...(body.oneOf !== undefined ? { oneOf: body.oneOf } : {}),
      additionalProperties: false,
    },
  };
}
