import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 3 — read-only scene list opened. Gate-free. Enumerates every
// opened scene as a shallow snapshot.
export const sceneListOpened: Tool = {
  name: "unity_open_mcp_scene_list_opened",
  description:
    "List every scene currently opened in the Unity Editor as a shallow snapshot (name, path, " +
    "isDirty, isLoaded, rootCount, buildIndex, isActive). Read-only (gate-free). Use scene_get_data " +
    "for the deep hierarchy view of a specific scene. Prefer this over raw execute_csharp " +
    "SceneManager.sceneCount — structured output + addressing parity with the rest of the typed " +
    "scene surface.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
