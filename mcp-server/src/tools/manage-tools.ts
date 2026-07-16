import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// manage_tools meta-tool for per-session tool-group visibility.
//
// Server-only meta-tool for per-session tool-group visibility. The MCP server
// holds the session state (ToolSessionState); the bridge does not track it.
// Activating a group makes its tools appear in subsequent ListTools responses;
// deactivating removes them. reset() restores the catalog's default-on groups.
//
// The tool definition ships with every MCP-server build (always visible — it
// is in the ALWAYS_VISIBLE_TOOLS allow-list in tool-session-state.ts) so an
// agent can always reach it before any other group is active.
export const manageTools: Tool = {
  name: "unity_open_mcp_manage_tools",
  description:
    "Manage which tool groups are visible in this session. Sessions start " +
    "with two groups enabled: `core` and `gate-and-verify`; activate other " +
    "groups on demand to add their tools to your ListTools surface (and " +
    "deactivate to hide them). State is ephemeral and per-session — it resets " +
    "to those two defaults when the MCP server restarts. Actions: " +
    "`list_groups` (show every group with active flag, description, and tool " +
    "roster), `activate` (enable a group — its tools become visible), " +
    "`deactivate` (hide a group's tools), `reset` (restore the two default-on " +
    "groups). Always call `list_groups` first to discover group ids. " +
    "Group availability also depends on the Unity domain dependency being " +
    "compiled in — see `unity_open_mcp_capabilities` for compiled-state " +
    "availability per group. Some groups **auto-activate** when their Unity " +
    "package is installed (e.g. `shadergraph` when `com.unity.shadergraph` " +
    "is present) — these surface in `list_groups` with " +
    "`activationSource: \"auto\"` and require no manual call; deactivate to " +
    "hide them.",
  inputSchema: {
    type: "object",
    required: ["action"],
    properties: {
      action: {
        enum: ["list_groups", "activate", "deactivate", "reset"],
        description:
          "list_groups: enumerate every group with active flag + tool roster. " +
          "activate / deactivate: toggle one group (requires `group`). " +
          "reset: restore the two default-on groups.",
      },
      group: {
        type: "string",
        description:
          "Group id (required for activate / deactivate). Valid ids are " +
          "returned by list_groups. Unknown ids return a structured error.",
      },
    },
    additionalProperties: false,
  },
};
