import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M20 Plan 7 / T20.7.2 — VFX Graph list. Compile-gated in the bridge
// (UNITY_OPEN_MCP_EXT_VFX on com.unity.visualeffectgraph) + auto-activating
// (the `vfx` group auto-activates when the package is present). Read-only:
// enumerates every VisualEffectGraph (.vfx) asset under Assets/. Gate = Off,
// no paths_hint. Uses the public runtime VisualEffectAsset type so the path is
// version-stable.
export const vfxList = makeTool(
  "unity_open_mcp_vfx_list",
  "List every VisualEffectGraph (.vfx) asset under Assets/. Read-only (Gate " +
    "is Off, no paths_hint). Returns each asset's path, name, and file size. " +
    "Optionally filter by name/path substring (filter) and cap results " +
    "(max_results, default 100, max 500). Requires the " +
    "com.unity.visualeffectgraph package installed.",
  {
    properties: {
          filter: {
            type: "string",
            description:
              "Optional substring filter on asset path (case-insensitive).",
          },
          max_results: {
            type: "integer",
            description:
              "Maximum number of assets to return (default 100, clamped to 500).",
            minimum: 1,
            maximum: 500,
          },
        },
  },
);
