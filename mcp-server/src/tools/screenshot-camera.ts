import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 1 / T20.1.1 — arbitrary-pose screenshot. Renders from a transient
// camera at the requested world-space pose without moving the scene/game
// camera. Returns the saved PNG file path (use capture_inline for an inline
// base64 image instead of a file).
export const screenshotCamera: Tool = {
  name: "unity_senses_screenshot_camera",
  description:
    "Render a screenshot from an arbitrary world-space camera pose (position + " +
    "rotation in degrees + field of view) without moving the scene or game " +
    "camera. Uses a transient Camera positioned at the requested pose, so the " +
    "live editor view is never disturbed. Returns the saved PNG file path. " +
    "Requires a live Unity Editor connection.",
  inputSchema: {
    type: "object",
    properties: {
      position: {
        type: "string",
        default: "0,0,0",
        description:
          "World-space camera position as \"x,y,z\" (e.g. \"5,10,-7\").",
      },
      rotation: {
        type: "string",
        default: "0,0,0",
        description:
          "Camera rotation as Euler angles in degrees \"x,y,z\" (e.g. " +
          "\"30,-45,0\" for a 30° down-tilt facing south-east).",
      },
      fov: {
        type: "number",
        default: 60,
        exclusiveMinimum: 0,
        exclusiveMaximum: 180,
        description: "Field of view in degrees. Must be strictly between 0 and 180.",
      },
      width: {
        type: "integer",
        default: 1280,
        minimum: 64,
        maximum: 4096,
        description: "Capture width in pixels.",
      },
      height: {
        type: "integer",
        default: 720,
        minimum: 64,
        maximum: 4096,
        description: "Capture height in pixels.",
      },
      background: {
        type: "string",
        enum: ["transparent", "solid", "skybox"],
        default: "skybox",
        description:
          "Background. 'skybox' = skybox (default), 'solid' = solid color, " +
          "'transparent' = alpha-clear. A nonexistent main camera falls back " +
          "to a sensible default culling mask / clip range.",
      },
    },
    additionalProperties: false,
  },
};
