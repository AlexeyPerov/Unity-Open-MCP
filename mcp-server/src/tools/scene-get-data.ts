import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 3 — read-only scene get data. Gate-free. The structured scene
// hierarchy read that supersedes the standalone M10 scene snapshot (T3.8),
// unifying summarize + hierarchy_describe into `detail`/`summary` modes.
// Bounded output drives see-edit-verify loops.
export const sceneGetData: Tool = {
  name: "unity_open_mcp_scene_get_data",
  description:
    "Read the hierarchy of an opened scene as compact, drill-down structured data. Read-only " +
    "(gate-free). Supersedes the standalone M10 scene snapshot, unifying summarize + " +
    "hierarchy_describe into the `detail`/`profile` modes below. Default (`profile: 'compact'`) returns a " +
    "cheap overview — scene name/path/isDirty/isLoaded/rootCount/buildIndex + a root-GameObject " +
    "roster with name + childCount + components (no nested children). `profile: 'balanced'` adds " +
    "the first `depth` levels of nested children with per-node active/tag/layer/component set. " +
    "`profile: 'full'` adds per-node transform (position/rotation/scale) and instance_id so the " +
    "agent can chain gameobject_modify / component_modify without an extra lookup. " +
    "Page large hierarchies with page_size/cursor. " +
    "Scene identity is path-first: provide `path` to resolve an opened scene by its asset path, " +
    "or `name` to resolve by display name; when both are supplied, `path` wins. " +
    "Use scene_list_opened to enumerate scenes. Prefer this over read_asset on the .unity file " +
    "(which is YAML-heavy and does not reflect unsaved editor state).",
  inputSchema: {
    type: "object",
    properties: {
      path: {
        type: "string",
        description:
          "Opened scene asset path (e.g. 'Assets/Scenes/Foo.unity') to read. Resolves the scene " +
          "by its asset path — the authoritative identity. When supplied, takes precedence over " +
          "`name`. Omit (and omit `name`) to read the active scene.",
      },
      name: {
        type: "string",
        description:
          "Opened scene name (fallback when `path` is not supplied). Omit (and omit `path`) to " +
          "read the active scene. Use scene_list_opened to enumerate opened scene names.",
      },
      profile: {
        enum: ["compact", "balanced", "full"],
        default: "compact",
        description:
          "Token-budget output profile (M22). 'compact' (default): scene overview + root-GameObject roster " +
          "(name/childCount/components, no nested children). 'balanced': + nested children to `depth` with " +
          "active/tag/layer/component set. 'full': + per-node instance_id and transform (position/rotation/scale). " +
          "An explicit profile wins over the legacy `detail` param; the two map onto the same axis.",
      },
      page_size: {
        type: "integer",
        minimum: 1,
        description:
          "Page the flattened node stream (M22 uniform paging). When set, the response carries a flat `nodes` " +
          "array for the current page + a `pagination` block with a `next_cursor` to resume. Omit to receive the " +
          "structural root/hierarchy view in one response.",
      },
      cursor: {
        type: "string",
        description:
          "Opaque continuation token from a previous response's `pagination.next_cursor`. Page the node stream.",
      },
      detail: {
        type: "string",
        enum: ["summary", "normal", "verbose"],
        default: "summary",
        description:
          "Legacy detail level (alias for `profile`: summary=compact, normal=balanced, verbose=full). " +
          "Prefer `profile`; ignored when `profile` is set.",
      },
      depth: {
        type: "integer",
        default: 3,
        minimum: 0,
        description:
          "Max hierarchy depth to emit when detail is 'normal'/'verbose' (profile balanced/full). 0 = roots only. " +
          "Nodes past the cap are counted in `moreHidden` per-root, not emitted.",
      },
      max_nodes: {
        type: "integer",
        default: 200,
        minimum: 1,
        description:
          "Hard cap on total nodes emitted across all roots (controls token budget). Remaining " +
          "nodes are counted in `truncated`. Legacy alias; defaults to 200.",
      },
    },
    additionalProperties: false,
  },
};
