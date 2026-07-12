import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 3 — typed scene set active. Mutating: marks an opened scene as
// the active scene. Runs the full gate path.
//
// Scene identity (path-first): `path` resolves an opened scene by its asset
// path; `name` resolves by display name. Precedence: when both are supplied,
// `path` wins (asset paths are the authoritative identity for an asset-centric
// MCP); `name` is the fallback when only it is supplied.
export const sceneSetActive: Tool = {
  name: "unity_open_mcp_scene_set_active",
  description:
    "Mark an opened scene as the editor's active scene (the scene new GameObjects are added to " +
    "and the default for many operations). Mutating: runs the full gate path; `paths_hint` should " +
    "be the scene's asset path. The target scene must already be opened (use scene_open first). " +
    "Scene identity is path-first: provide `path` (the scene's `Assets/.../*.unity` asset path) to " +
    "resolve unambiguously, or `name` to resolve by display name. When both are supplied, `path` " +
    "wins and `name` is ignored. Returns the post-set list of opened scenes (name/path/isDirty/" +
    "isLoaded/rootCount/buildIndex/isActive) so the agent can confirm which scene is now active. " +
    "Prefer this over raw execute_csharp EditorSceneManager.SetActiveScene.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      path: {
        type: "string",
        description:
          "Opened scene asset path (e.g. 'Assets/Scenes/Foo.unity') to mark as active. Resolves the " +
          "scene by its asset path — the authoritative identity. When supplied, takes precedence " +
          "over `name`. Use scene_list_opened to enumerate opened scene paths.",
      },
      name: {
        type: "string",
        description:
          "Name of the opened scene to mark as active (fallback when `path` is not supplied). Must " +
          "match an opened scene's display name. Use scene_list_opened to enumerate opened scene " +
          "names.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the scene's asset path.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
