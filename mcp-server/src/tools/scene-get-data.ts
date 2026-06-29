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
    "hierarchy_describe into the `detail` modes below. Default (`detail: 'summary'`) returns a " +
    "cheap overview — scene name/path/isDirty/isLoaded/rootCount/buildIndex + a root-GameObject " +
    "roster with name + childCount + components (no nested children). `detail: 'normal'` adds " +
    "the first `depth` levels of nested children with per-node active/tag/layer/component set. " +
    "`detail: 'verbose'` adds per-node transform (position/rotation/scale) and instance_id so the " +
    "agent can chain gameobject_modify / component_modify without an extra lookup. " +
    "Use scene_list_opened to enumerate scenes. Prefer this over read_asset on the .unity file " +
    "(which is YAML-heavy and does not reflect unsaved editor state).",
  inputSchema: {
    type: "object",
    properties: {
      name: {
        type: "string",
        description:
          "Opened scene name. Omit (or empty) to read the active scene. Use scene_list_opened " +
          "to enumerate opened scene names.",
      },
      detail: {
        type: "string",
        enum: ["summary", "normal", "verbose"],
        default: "summary",
        description:
          "Output detail. 'summary' (default): scene overview + root-GameObject roster " +
          "(name/childCount/components, no nested children). 'normal': + nested children to " +
          "`depth` with active/tag/layer/component set. 'verbose': + per-node instance_id and " +
          "transform (position/rotation/scale).",
      },
      depth: {
        type: "integer",
        default: 3,
        minimum: 0,
        description:
          "Max hierarchy depth to emit when detail is 'normal' or 'verbose'. 0 = roots only. " +
          "Nodes past the cap are counted in `moreHidden` per-root, not emitted.",
      },
      max_nodes: {
        type: "integer",
        default: 200,
        minimum: 1,
        description:
          "Hard cap on total nodes emitted across all roots (controls token budget). Remaining " +
          "nodes are counted in `truncated`. Defaults to 200.",
      },
    },
    additionalProperties: false,
  },
};
