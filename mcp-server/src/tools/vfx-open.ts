import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 7 / T20.7.2 — VFX Graph open. Compile-gated + auto-activating
// (see vfx-list.ts). Read-only window bring-up: opens the .vfx in the VFX Graph
// editor window and returns a structured summary (context count, block count,
// exposed property count, property names). Non-mutating state change —
// Gate = Off, no paths_hint. The summary reads off the public runtime
// VisualEffectAsset type and the stable serialized file format, so it is
// version-stable without editor-model reflection.
export const vfxOpen = makeTool(
  "unity_open_mcp_vfx_open",
  "Open a VisualEffectGraph (.vfx) asset in the VFX Graph editor window and " +
    "return a structured summary (context count, block count, exposed property " +
    "count, property names). Read-only window bring-up — Gate is Off, no " +
    "paths_hint. The summary reads off the public runtime VisualEffectAsset type " +
    "and the serialized file format, so it is stable across package versions. " +
    "Requires com.unity.visualeffectgraph.",
  {
    required: ["asset_path"],
        properties: {
          asset_path: {
            type: "string",
            description:
              "VisualEffectGraph asset path ('Assets/.../*.vfx'). Highest and only " +
              "resolver.",
          },
        },
  },
);
