import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 3 — typed scene unload. Mutating: removes an opened scene from
// the editor (without saving). Runs the full gate path.
//
// Scene identity (path-first): `path` resolves an opened scene by its asset
// path; `name` resolves by display name. Precedence: when both are supplied,
// `path` wins; `name` is the fallback when only it is supplied.
export const sceneUnload: Tool = {
  name: "unity_open_mcp_scene_unload",
  description:
    "Unload an opened scene from the Unity Editor (without saving — call scene_save first if " +
    "the scene has unsaved changes you want to keep). Mutating: runs the full gate path; " +
    "`paths_hint` should be the scene's asset path. Scene identity is path-first: provide `path` " +
    "(the scene's `Assets/.../*.unity` asset path) to resolve unambiguously, or `name` to resolve " +
    "by display name. When both are supplied, `path` wins and `name` is ignored. Returns the " +
    "post-unload list of opened scenes (name/path/isDirty/isLoaded/rootCount/buildIndex/isActive). " +
    "Prefer this over raw execute_csharp SceneManager.UnloadSceneAsync.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      path: {
        type: "string",
        description:
          "Opened scene asset path (e.g. 'Assets/Scenes/Foo.unity') to unload. Resolves the scene " +
          "by its asset path — the authoritative identity. When supplied, takes precedence over " +
          "`name`. Use scene_list_opened to enumerate opened scene paths.",
      },
      name: {
        type: "string",
        description:
          "Name of the opened scene to unload (fallback when `path` is not supplied). Must match " +
          "an opened scene's display name. Use scene_list_opened to enumerate opened scene names.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — the unloaded scene's asset path (or its name when it is not yet " +
          "asset-backed).",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
      },
    },
    additionalProperties: false,
  },
};
