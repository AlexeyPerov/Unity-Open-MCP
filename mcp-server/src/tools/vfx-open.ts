import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 7 / T20.7.2 — VFX Graph open. Compile-gated + auto-activating
// (see vfx-list.ts). Opens the .vfx in the package's VFX Graph window (or
// focuses / closes it: `window`) and returns a structured summary (context count, block count,
// exposed property count, property names). Non-mutating state change —
// Gate = Off, no paths_hint. The summary reads off the public runtime
// VisualEffectAsset type and the stable serialized file format, so it is
// version-stable without editor-model reflection.
export const vfxOpen = makeTool(
  "unity_open_mcp_vfx_open",
  "Open a VisualEffectGraph (.vfx) asset in the VFX Graph editor window (or " +
    "focus the window already showing it) and return a structured summary " +
    "(context count, block count, exposed property count, property names) plus " +
    "window {action, open, title}. window: open (default) | none (summary only, " +
    "no window) | close (close the graph's window). Writes no asset — Gate is " +
    "Off, no paths_hint. Refuses editor_window_unavailable in batch mode (use " +
    "window none) and graph_has_unsaved_changes when closing a window whose " +
    "graph has unsaved edits. The summary reads off the public runtime " +
    "VisualEffectAsset type and the serialized file format, so it is stable " +
    "across package versions. Requires com.unity.visualeffectgraph.",
  {
    required: ["asset_path"],
        properties: {
          asset_path: {
            type: "string",
            description:
              "VisualEffectGraph asset path ('Assets/.../*.vfx'). Highest and only " +
              "resolver.",
          },
          window: {
            type: "string",
            enum: ["open", "none", "close"],
            default: "open",
            description:
              "open (default): open the graph in its VFX Graph window, or focus the window showing it. " +
              "none: the summary only. close: close the graph's window (refused while it has unsaved edits).",
          },
        },
  },
);
