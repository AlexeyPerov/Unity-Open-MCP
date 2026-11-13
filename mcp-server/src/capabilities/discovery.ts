import { ALWAYS_BATCH_TOOLS } from "../batch-spawn.js";
import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { buildCapabilities } from "./build-capabilities.js";
import { INTENT_TAGS } from "./intent-groups.js";
import { filterVisibleTools, type ToolSessionState } from "../tool-session-state.js";
import { applyPaging } from "../output-profile.js";
import { isLocalTool } from "../local-tools.js";
import { closeToolNames, toolStem } from "../tool-name-suggest.js";

const POTENTIALLY_MUTATING_LOCAL = new Set(["unity_open_mcp_jobs", "unity_open_mcp_restart_editor", "unity_open_mcp_hub_install_editor", "unity_open_mcp_hub_install_modules", "unity_open_mcp_hub_set_install_path"]);

function exampleValue(s: any, key: string): any {
  if (s.default !== undefined) return s.default;
  if (s.enum) return s.enum[0];
  if (s.type === "array") return [exampleValue(s.items ?? {}, key)];
  if (s.type === "object") return exampleFor(s);
  if (s.type === "boolean") return false;
  if (s.type === "number" || s.type === "integer") return s.minimum ?? 1;
  if (key === "asset_path" || key === "paths_hint" || key === "paths") return "Assets/Scenes/Main.unity";
  if (key === "game_object_path") return "Main Camera";
  if (key === "component_type") return "UnityEngine.Transform";
  if (key === "property_path") return "m_LocalPosition";
  return "example";
}
export function exampleFor(schema: any): Record<string, unknown> {
  const keys = new Set<string>(schema.required ?? []);
  for (const key of ["asset_path", "game_object_path", "component_type", "property_path"]) if (schema.properties?.[key]) keys.add(key);
  for (const clause of schema.allOf ?? []) for (const k of clause.anyOf?.[0]?.required ?? []) keys.add(k);
  if (schema.oneOf?.[0]?.required) for (const k of schema.oneOf[0].required) keys.add(k);
  return Object.fromEntries([...keys].filter(k => !schema.properties?.[k]?.deprecated).map(k => [k, exampleValue(schema.properties?.[k] ?? {}, k)]));
}

/**
 * `inventory` is the live bridge's compiled tool set (undefined when unknown).
 * Always-batch tools are never in it, so their availability is `headless`
 * instead (BatchSpawn.headlessAvailable; undefined when not probed).
 */
export function discoverTools(tools: Tool[], batch: ReadonlySet<string>, session: ToolSessionState, inventory: ReadonlySet<string> | undefined, args: Record<string, any>, headless?: boolean) {
  const terms = queryTerms(args.query);
  const visible = new Set(filterVisibleTools(tools, session).map(t => t.name));
  const built = buildCapabilities({ tools, batchToolNames: batch, rules: [], fixes: [], availableBridgeTools: inventory }, { kind: "tools", includePlanned: false });
  const rows = (built.tools ?? []).map(t => {
    const local = isLocalTool(t.name);
    const routePolicy = local ? "local" : ALWAYS_BATCH_TOOLS.has(t.name) ? "batch" : t.routePolicy;
    const tags = INTENT_TAGS.filter(tag => t.group && tag.groups.includes(t.group)).map(tag => tag.tag);
    return { ...t, routePolicy,
      mutating: !!t.inputSchema.properties?.gate || POTENTIALLY_MUTATING_LOCAL.has(t.name),
      active: visible.has(t.name),
      available: local || t.routePolicy === "offline" || t.routePolicy === "offline-first" ? true
        : routePolicy === "batch" ? headless ?? null : inventory ? inventory.has(t.name) : null,
      tags, example: exampleFor(t.inputSchema) };
  }).map(t => ({ ...t, queryScore: terms.length ? queryScore(t.name, t.description ?? "", terms) : 0 }))
    .filter(t => (!args.tool_name || t.name === args.tool_name)
    && (!terms.length || t.queryScore > 0)
    && (!args.group || t.group === args.group) && (!args.groups || args.groups.includes(t.group))
    && (!args.tag || t.tags.includes(args.tag))
    && (args.available === undefined || t.available === args.available)
    && (args.active === undefined || t.active === args.active)
    && (!args.route || t.routePolicy === args.route)
    && (args.mutating === undefined || t.mutating === args.mutating))
    .sort((a, b) => b.queryScore - a.queryScore || a.name.localeCompare(b.name))
    .map(({ queryScore: _score, ...t }) => t);
  const page = applyPaging(rows, "tool-discovery", { page_size: args.tool_name ? 1 : Math.min(args.page_size ?? 20, 1000), cursor: args.tool_name ? undefined : args.cursor });
  return { tools: page.page.map(t => {
    if (args.tool_name || args.profile === "full") return t;
    const { inputSchema, description, ...compact } = t;
    return args.profile === "balanced" ? { ...compact, description } : compact;
  }), pagination: page.block, ...(args.tool_name && !rows.length ? unknownToolError(String(args.tool_name), tools) : {}),
    ...(!args.tool_name && !rows.length ? { emptyReason: emptyReason(args, terms) } : {}) };
}

/**
 * `query` terms: split on whitespace, commas, semicolons and pipes so a list
 * of tool names (`"prefab_create component_add"`) finds every one of them.
 */
export function queryTerms(query: unknown): string[] {
  if (typeof query !== "string") return [];
  return [...new Set(query.toLowerCase().split(/[\s,;|]+/).filter(Boolean))];
}

/**
 * 0 = no term matched. A term naming the tool exactly (with or without its
 * prefix) outranks a name substring, which outranks a description hit, so
 * the tools an agent listed by name come first.
 */
function queryScore(name: string, description: string, terms: string[]): number {
  const lowerName = name.toLowerCase();
  const stem = toolStem(name);
  const lowerDescription = description.toLowerCase();
  let score = 0;
  for (const term of terms) {
    if (term === lowerName || term === stem) score += 100;
    else if (lowerName.includes(term)) score += 10;
    else if (lowerDescription.includes(term)) score += 1;
  }
  return score;
}

function emptyReason(args: Record<string, any>, terms: string[]): string {
  const filters = ["group", "groups", "tag", "available", "active", "route", "mutating"]
    .filter(key => args[key] !== undefined)
    .map(key => `${key}=${JSON.stringify(args[key])}`);
  const what = terms.length ? `No tool name or description contains any of ${terms.map(t => JSON.stringify(t)).join(", ")}` : "No tool matched";
  return `${what}${filters.length ? ` with ${filters.join(", ")}` : ""}. Drop a filter, or try a shorter term (e.g. "prefab").`;
}

function unknownToolError(name: string, tools: Tool[]) {
  const matches = closeToolNames(name, tools.map(t => t.name));
  return { error: { code: "unknown_tool", message: `Unknown tool ${name}${matches.length ? `. Did you mean ${matches.join(", ")}?` : ""}`, ...(matches.length ? { closeMatches: matches } : {}) } };
}

/** Compact activation schemas retain validation constraints, dropping field prose. */
export function compactDiscoverySchema(value: any): any {
  if (Array.isArray(value)) return value.map(compactDiscoverySchema);
  if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).filter(([key]) => key !== "description").map(([key, v]) => [key, compactDiscoverySchema(v)]));
  return value;
}
