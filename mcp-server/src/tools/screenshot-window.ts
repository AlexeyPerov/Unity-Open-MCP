import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 1 / T20.1.2 — Editor window screenshot. Captures any EditorWindow
// (Console, Hierarchy, Inspector, Project, Scene, Game, or a custom window).
// feedback #6 (2026-08-07): on macOS/Linux the screen-readback path reads from
// an unbound backbuffer during bridge dispatch and produces a blank frame, so
// the tool now returns a structured `not_supported` error there (use the OS
// screen-capture tool, or screenshot {view:"composed"} for the rendered Game
// view). On Windows a screen-rect readback is used and a post-capture
// solid-color check rejects blank frames with a `blank_capture` error instead
// of reporting status:ok. `resolution` reports the actual capture size.
export const screenshotWindow = makeTool(
  "unity_senses_screenshot_window",
  "Capture a Unity Editor window (Console, Hierarchy, Inspector, Project, " +
    "Scene, Game, or any custom EditorWindow) to a PNG file. Windows-only: on " +
    "macOS/Linux returns a `not_supported` error (the editor-window backbuffer " +
    "is not bound during bridge dispatch, so a blank frame would result — use " +
    "the OS screen-capture tool, or screenshot {view:\"composed\"} for the " +
    "rendered Game view). On Windows a screen-rect readback is used; a blank/" +
    "solid-color frame is rejected with a `blank_capture` error. `resolution` " +
    "reports the ACTUAL capture size (clamped to the window's on-screen rect), " +
    "not the requested maximum. Provide either window_title (visible tab text, " +
    "e.g. \"Console\") or window_type (EditorWindow type name, e.g. " +
    "\"UnityEditor.ConsoleWindow\"). Returns the saved PNG file path. Requires " +
    "a live Unity Editor connection.",
  {
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
  },
);
