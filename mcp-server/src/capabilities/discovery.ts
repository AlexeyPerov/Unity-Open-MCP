import { ALWAYS_BATCH_TOOLS } from "../batch-spawn.js";
import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { buildCapabilities } from "./build-capabilities.js";
import { INTENT_TAGS } from "./intent-groups.js";
import { filterVisibleTools, type ToolSessionState } from "../tool-session-state.js";
import { applyPaging } from "../output-profile.js";

const LOCAL = new Set(["capabilities", "manage_tools", "generate_skill", "list_rules", "bridge_status", "restart_editor", "resource_pressure", "read_compile_errors"] .map(n => `unity_open_mcp_${n}`));

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

export function discoverTools(tools: Tool[], batch: ReadonlySet<string>, session: ToolSessionState, inventory: ReadonlySet<string> | undefined, args: Record<string, any>) {
  const visible = new Set(filterVisibleTools(tools, session).map(t => t.name));
  const built = buildCapabilities({ tools, batchToolNames: batch, rules: [], fixes: [], availableBridgeTools: inventory }, { kind: "tools", includePlanned: false });
  const rows = (built.tools ?? []).map(t => {
    const local = LOCAL.has(t.name) || t.name.startsWith("unity_open_mcp_hub_") || t.name === "unity_senses_pull_events";
    const tags = INTENT_TAGS.filter(tag => t.group && tag.groups.includes(t.group)).map(tag => tag.tag);
    return { ...t, routePolicy: local ? "local" : ALWAYS_BATCH_TOOLS.has(t.name) ? "batch" : t.routePolicy,
      mutating: !!t.inputSchema.properties?.gate || ["unity_open_mcp_restart_editor", "unity_open_mcp_hub_install_editor", "unity_open_mcp_hub_install_modules", "unity_open_mcp_hub_set_install_path"].includes(t.name),
      active: visible.has(t.name), available: local || t.routePolicy === "offline" || t.routePolicy === "offline-first" ? true : inventory ? inventory.has(t.name) : null,
      tags, example: exampleFor(t.inputSchema) };
  }).filter(t => (!args.tool_name || t.name === args.tool_name)
    && (!args.query || `${t.name} ${t.description}`.toLowerCase().includes(String(args.query).toLowerCase()))
    && (!args.group || t.group === args.group) && (!args.groups || args.groups.includes(t.group))
    && (!args.tag || t.tags.includes(args.tag))
    && (args.available === undefined || t.available === args.available)
    && (args.active === undefined || t.active === args.active)
    && (!args.route || t.routePolicy === args.route)
    && (args.mutating === undefined || t.mutating === args.mutating))
    .sort((a, b) => a.name.localeCompare(b.name));
  const page = applyPaging(rows, "tool-discovery", { page_size: args.tool_name ? 1 : Math.min(args.page_size ?? 20, 1000), cursor: args.tool_name ? undefined : args.cursor });
  return { tools: page.page.map(t => {
    if (args.tool_name || args.profile === "full") return t;
    const { inputSchema, description, ...compact } = t;
    return args.profile === "balanced" ? { ...compact, description } : compact;
  }), pagination: page.block, ...(args.tool_name && !rows.length ? { error: { code: "unknown_tool", message: `Unknown tool ${args.tool_name}` } } : {}) };
}

/** Compact activation schemas retain validation constraints, dropping field prose. */
export function compactDiscoverySchema(value: any): any {
  if (Array.isArray(value)) return value.map(compactDiscoverySchema);
  if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).filter(([key]) => key !== "description").map(([key, v]) => [key, compactDiscoverySchema(v)]));
  return value;
}
