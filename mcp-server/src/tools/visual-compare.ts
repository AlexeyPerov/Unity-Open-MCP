import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// Visual regression compare — the one transferable idea worth taking from
// unity-biome-mcp's "visual baseline/diff" feature. Capture a named reference
// snapshot from a view (scene / game / isolated), then compare later captures
// against it. The bridge returns pixelDiffPercent / mismatchedPixels /
// perceptualDistance (8x8 aHash Hamming distance) / match, plus an inline diff
// image (mismatched pixels in red over the current frame) when the diff is
// non-zero. The diff image is carried as an `inlineImage` field the MCP server
// unwraps into an MCP image content block (same mechanism as capture_inline).
//
// "reference snapshot" / "visual compare" is used instead of "baseline" — that
// name is already taken by the verify-issue baseline tools (baseline_create,
// regression_check), which compare compiler-error / project-health snapshots,
// not images.
export const visualCompare = makeTool(
  "unity_senses_visual_compare",
  "Visual regression compare. Capture a named reference snapshot from a view " +
    "(scene / game / isolated), then compare later captures against it to check " +
    "whether an edit visually broke anything. Returns pixelDiffPercent, " +
    "mismatchedPixels, perceptualDistance (8x8 average-hash Hamming distance — " +
    "tolerates minor anti-aliasing drift), and match (true when pixelDiffPercent " +
    "<= sensitivity * 100). On a non-zero diff, returns an inline diff image " +
    "(mismatched pixels highlighted red over the current frame) as an MCP image " +
    "content block. Actions: 'save' (capture + store a named reference), " +
    "'compare' (capture + diff vs the named reference), 'list' (enumerate saved " +
    "references), 'delete' (remove a reference). References persist under " +
    "~/.unity-open-mcp/screenshots/references/. Requires a live Unity Editor " +
    "connection.",
  {
    properties: {
      action: {
        type: "string",
        enum: ["save", "compare", "list", "delete"],
        description:
          "save: capture + store a named reference. compare: capture + diff vs " +
          "the named reference. list: enumerate saved references. delete: remove " +
          "a named reference.",
      },
      name: {
        type: "string",
        description:
          "Reference name (required for save / compare / delete). Flat filename " +
          "only — no path separators or traversal. Stored as " +
          "<name>.png + <name>.meta.json.",
      },
      view: {
        type: "string",
        enum: ["scene", "game", "isolated"],
        default: "game",
        description:
          "Capture target. 'scene' = Scene view camera, 'game' = main game " +
          "camera (default — visual regression usually targets what the player " +
          "sees), 'isolated' = clean 2x2 composite of one GameObject. Ignored " +
          "for list / delete.",
      },
      width: {
        type: "integer",
        default: 1280,
        minimum: 64,
        maximum: 4096,
        description:
          "Capture width in pixels. For 'isolated', this is per-quadrant. The " +
          "compare resamples the current capture to the reference dimensions if " +
          "they differ, so the diff is resolution-stable.",
      },
      height: {
        type: "integer",
        default: 720,
        minimum: 64,
        maximum: 4096,
        description:
          "Capture height in pixels. For 'isolated', this is per-quadrant.",
      },
      object_path: {
        type: "string",
        description:
          "Required for 'isolated' view. Hierarchy path of the target " +
          "GameObject (e.g. \"Player\" or \"Enemies/Goblin\").",
      },
      background: {
        type: "string",
        enum: ["transparent", "solid", "skybox"],
        default: "skybox",
        description: "Background for 'isolated' view. Ignored for scene/game.",
      },
      sensitivity: {
        type: "number",
        default: 0.01,
        minimum: 0,
        maximum: 1,
        description:
          "[compare] Match threshold as a fraction of mismatched pixels (0–1). " +
          "match is true when pixelDiffPercent <= sensitivity * 100. Default 0.01 " +
          "= 1% pixel diff. Raise for noisier scenes (dynamic backgrounds, " +
          "particle systems); 0 = require an exact match.",
      },
      include_diff_image: {
        type: "boolean",
        default: true,
        description:
          "[compare] When true (default) and the diff is non-zero, return the " +
          "diff image (current frame with mismatched pixels set to red) as an " +
          "inline MCP image content block. Set false to skip the image and " +
          "receive metrics only.",
      },
    },
    required: ["action"],
  },
);
