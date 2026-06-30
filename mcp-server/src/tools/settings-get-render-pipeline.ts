import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 9 / T20.9.3 — Project Settings remainder. Read-only: detect the
// active Scriptable Render Pipeline (Built-in / URP / HDRP). No setter —
// switching SRP is a package-level operation.
export const settingsGetRenderPipeline: Tool = {
  name: "unity_open_mcp_settings_get_render_pipeline",
  description:
    "Read-only: detect the active Scriptable Render Pipeline. Reports " +
    "{ pipeline, defaultShader, renderPipelineAsset, hasSetter, note }. pipeline " +
    "is \"Built-in\", \"URP\", \"HDRP\", or the render-pipeline-asset type name. " +
    "hasSetter is always false — switching SRP is a package-level operation " +
    "(install/swap com.unity.render-pipelines.universal or " +
    "com.unity.render-pipelines.high-definition via package_add / package_remove), " +
    "not a settings tweak. Gate-free.",
  inputSchema: {
    type: "object",
    properties: {},
    additionalProperties: false,
  },
};
