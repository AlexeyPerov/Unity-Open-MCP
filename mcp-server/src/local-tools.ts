// Single source for "which tools the MCP server answers itself".
//
// Three consumers used to keep their own copy of this list (capabilities
// discovery, the reload probe in the router, the coverage-matrix script) and
// they had already drifted. Discovery and routing import from here; the
// coverage-matrix script (scripts/gen-mcp-coverage-matrix.mjs) parses the
// LOCAL_TOOL_NAMES literal below straight out of this file, so keep the set a
// plain array of string literals.

/** Server-only tools: no live bridge hop, no headless spawn. */
export const LOCAL_TOOL_NAMES: ReadonlySet<string> = new Set([
  "unity_open_mcp_capabilities",
  "unity_open_mcp_manage_tools",
  "unity_open_mcp_generate_skill",
  "unity_open_mcp_list_rules",
  "unity_open_mcp_bridge_status",
  "unity_open_mcp_restart_editor",
  "unity_open_mcp_resource_pressure",
  "unity_open_mcp_read_compile_errors",
  "unity_open_mcp_jobs",
  "unity_senses_pull_events",
]);

export function isLocalTool(name: string): boolean {
  return LOCAL_TOOL_NAMES.has(name) || name.startsWith("unity_open_mcp_hub_");
}

/**
 * Tools that must never wait out a reloading Editor before dispatch: local
 * tools, offline disk reads, and the project-command catalog probe, whose own
 * handler reports `catalog_unavailable` when the bridge is away.
 */
const RELOAD_PROBE_EXEMPT: ReadonlySet<string> = new Set([
  "unity_open_mcp_list_assets",
  "unity_open_mcp_project_commands",
]);

export function skipsReloadProbe(name: string): boolean {
  return isLocalTool(name) || RELOAD_PROBE_EXEMPT.has(name);
}
