import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 10 / T6.6.2 — Navigation (NavMesh) extension tool. The heavy op.
// Bake runs synchronously inside the Editor and may take seconds on large
// scenes; EditorSettle makes the dispatcher wait for asset refresh so the
// baked NavMesh asset is on disk by the time the agent issues the next call.
export const navigationSurfaceBake: Tool = {
  name: "unity_open_mcp_navigation_surface_bake",
  description:
    "Bake the NavMesh for a NavMeshSurface. Runs the bake synchronously — " +
    "may take seconds on large scenes. The baked NavMesh asset is written " +
    "next to the scene. Returns durationMs + hasNavMeshData + navMeshDataInstanceId. " +
    "Mutating: runs the full gate path; paths_hint is the host scene path. " +
    "EditorSettle lifecycle waits for asset refresh before returning. Requires " +
    "the navigation extension pack.",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      instance_id: { type: "integer", default: 0, description: "Host GameObject instance ID." },
      path: { type: "string", description: "Host hierarchy path \"Root/Child\"." },
      name: { type: "string", description: "Host GameObject name (first match)." },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description: "Mutation scope — the scene path that contains the host.",
      },
      gate: { enum: ["enforce", "warn", "off"], default: "enforce" },
    },
    additionalProperties: false,
  },
};
