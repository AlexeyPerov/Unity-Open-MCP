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
//
// Two intent-driven actions — `suggest` and `activate_for` — are the agent-
// first path over hand-picking group ids: the agent states what it is about
// to do (free-text `intent` and/or explicit `tags`) and the server maps that
// to the right groups. `suggest` is read-only (returns recommendations +
// reasons without changing state); `activate_for` activates the recommended
// set in one call and emits `notifications/tools/list_changed` when the
// visible surface changes. Unknown intent returns a structured empty
// recommendation (no invented groups) and points the caller at `list_groups`.
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
    "groups), `suggest` (recommend groups for a task intent and/or tags " +
    "WITHOUT changing state — the read-only path), `activate_for` (activate " +
    "the recommended groups for an intent and/or tags in one call). " +
    "Prefer `activate_for(intent=\"…\")` over hand-picking group ids: state " +
    "what you are about to do and the server brings the right groups online, " +
    "including `gate-intelligence` when the task looks mutating or verify-" +
    "related. Always call `list_groups` first to discover group ids when you " +
    "need to browse or hand-pick. " +
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
        enum: ["list_groups", "activate", "deactivate", "reset", "suggest", "activate_for"],
        description:
          "list_groups: enumerate every group with active flag + tool roster. " +
          "activate / deactivate: toggle one group (requires `group`). " +
          "reset: restore the two default-on groups. " +
          "suggest: recommend groups for an `intent` and/or `tags` WITHOUT " +
          "changing state (read-only). " +
          "activate_for: activate the recommended groups for an `intent` " +
          "and/or `tags` in one call (idempotent; emits list_changed when " +
          "the visible set changes).",
      },
      group: {
        type: "string",
        description:
          "Group id (required for activate / deactivate). Valid ids are " +
          "returned by list_groups. Unknown ids return a structured error.",
      },
      intent: {
        type: "string",
        description:
          "Free-text task intent for `suggest` / `activate_for` (e.g. " +
          "\"bake a NavMesh for the dungeon scene\", \"verify the prefab " +
          "references and run a scan\"). The text is tokenized and matched " +
          "against the intent/tag catalog; mutating/verify verbs additionally " +
          "surface `gate-intelligence`. Unknown intent returns an empty " +
          "recommendation (no invented groups).",
      },
      tags: {
        type: "array",
        items: { type: "string" },
        description:
          "Explicit tags for `suggest` / `activate_for`. Accepted forms: " +
          "canonical tag names (navigation, animation, audio, profiler, qa, " +
          "verify, risk, …), catalog keywords (navmesh, particles, build, …), " +
          "or group ids (terrain, vfx, …). Unknown tags are reported in " +
          "`unmatchedTags` and otherwise ignored. Combine with `intent` to " +
          "narrow the recommendation.",
      },
    },
    additionalProperties: false,
  },
};
