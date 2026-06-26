import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M20 Plan 1 / T20.1.2 — Editor window screenshot. Captures any EditorWindow
// (Console, Hierarchy, Inspector, Project, Scene, Game, or a custom window).
// Full-fidelity capture via the Win32 PrintWindow API is Windows-only; on
// macOS/Linux a best-effort screen-rect readback is used and the response
// carries platformLimited: true so the agent knows the capture may be partial
// when the window is occluded. Returns the saved PNG file path.
export const screenshotWindow: Tool = {
  name: "unity_senses_screenshot_window",
  description:
    "Capture a Unity Editor window (Console, Hierarchy, Inspector, Project, " +
    "Scene, Game, or any custom EditorWindow) to a PNG file. Full-fidelity " +
    "capture (occlusion-proof, no focus stealing) is Windows-only via the " +
    "Win32 PrintWindow API; on macOS/Linux a best-effort screen-rect readback " +
    "is used and the response carries platformLimited: true, so the capture " +
    "may be partial or stale when the window is hidden behind others. Provide " +
    "either window_title (visible tab text, e.g. \"Console\") or window_type " +
    "(EditorWindow type name, e.g. \"UnityEditor.ConsoleWindow\"). Returns " +
    "the saved PNG file path. Requires a live Unity Editor connection.",
  inputSchema: {
    type: "object",
    properties: {
      window_title: {
        type: "string",
        description:
          "Visible tab title of the target window (e.g. \"Console\", " +
          "\"Hierarchy\", \"Inspector\", \"Project\", \"Scene\", \"Game\"). " +
          "Common titles map to their built-in EditorWindow types; custom " +
          "titles match any open window with that title.",
      },
      window_type: {
        type: "string",
        description:
          "EditorWindow type name (simple or fully-qualified, e.g. " +
          "\"ConsoleWindow\" or \"UnityEditor.ConsoleWindow\"). Takes " +
          "precedence over window_title when both are given.",
      },
      width: {
        type: "integer",
        default: 1280,
        minimum: 64,
        maximum: 4096,
        description:
          "Maximum capture width in pixels. The actual capture is clamped to " +
          "the window's on-screen width.",
      },
      height: {
        type: "integer",
        default: 720,
        minimum: 64,
        maximum: 4096,
        description:
          "Maximum capture height in pixels. The actual capture is clamped to " +
          "the window's on-screen height.",
      },
    },
    additionalProperties: false,
  },
};
