// Playtest — device matrix (server-local; group `playtest`).
//
// Puts the running game on each Device Simulator device (× orientation) of a
// list or preset in turn, checks it there — the current screen's detectors and
// one frame, or a whole UI crawl per device — records it into the caller's open
// playtest session and puts the original Game view back. Runs as a jobs job.
// See src/playtest/devices.ts.
import { makeTool } from "./schema-fragments.js";
import { GAME_VIEW_ORIENTATIONS } from "./game-view.js";
import { playtestExplore } from "./playtest-explore.js";

export const PLAYTEST_DEVICES_TOOL = "unity_open_mcp_playtest_devices";
export const DEVICE_PRESETS = ["common", "phones", "tablets", "notch", "all"] as const;
export const DEVICE_ORIENTATIONS = GAME_VIEW_ORIENTATIONS;

const exploreProps = { ...(playtestExplore.inputSchema.properties as Record<string, unknown>) };
delete exploreProps.allow_unsandboxed;

export const playtestDevices = makeTool(
  PLAYTEST_DEVICES_TOOL,
  "Check the game on several phones and tablets in one call: for each Device Simulator device (× orientation) it " +
    "switches the view (game_view set_device, waits for the game to settle) and checks it there, then puts the view it " +
    "found back. Runs as a jobs job and returns its job_id: jobs wait until terminal, jobs cancel stops it (the view is " +
    "still put back). DEVICES: `devices` (ids or unique substrings from game_view list_devices) or `preset`: common " +
    "(default — the smallest phone, the tallest phone, the phone with the deepest notch, the smallest tablet), phones, " +
    "tablets (diagonal ≥ 7\"), notch, all; max_devices (default 8, ≤ 20). `orientations` (default [\"portrait\"]; " +
    "landscape = landscape_left); a rotation the game does not follow is checked once. SCREENS: current (default) = " +
    "the screen the game shows now, per device: the snapshot detectors at that device's dpi (text overflow, outside " +
    "the safe area — real on notch devices —, overlapping controls, placeholder text, touch targets under min_touch_dp, " +
    "zero-size controls, console errors) and one composed frame; explore = a whole UI crawl per device (the " +
    "playtest_explore options go in `explore`; the game is restarted between devices; needs an isolated session " +
    "and play mode). REQUIRES your open playtest_session (no_open_session); each device check is recorded into it " +
    "with its frame, and the report gets a Devices section. Result: entries[] (device, orientation, screen, " +
    "safe_area, insets, counts, findings, frame), matrix[] (each finding with the devices that show it: on 2 of 4 " +
    "devices), known[] (findings the project profile Playtests/profile.json accepts; its devices / preset, orientations " +
    "and detector settings apply when the call sets none), skipped[] (orientation unsupported / not applied, not_settled), restored, finding_counts. Unity 6 " +
    "Editor (Device Simulator).",
  {
    properties: {
      devices: {
        type: "array", minItems: 1, maxItems: 20, items: { type: "string", minLength: 1 },
        description: "Device ids or unique substrings (game_view list_devices). Exclusive with preset.",
      },
      preset: { type: "string", enum: [...DEVICE_PRESETS], description: "common (default) | phones | tablets | notch | all." },
      max_devices: { type: "integer", minimum: 1, maximum: 20, description: "At most this many devices (default 8)." },
      orientations: {
        type: "array", minItems: 1, maxItems: 4, items: { type: "string", enum: [...DEVICE_ORIENTATIONS, "landscape"] },
        description: "Orientations per device (default [\"portrait\"]; landscape = landscape_left).",
      },
      screens: {
        type: "string", enum: ["current", "explore"],
        description: "current (default) = check the screen the game shows on each device; explore = a UI crawl per device.",
      },
      explore: {
        type: "object", properties: exploreProps, additionalProperties: false,
        description: "[screens: explore] The crawl's options, as playtest_explore takes them (budgets apply per device; restart also runs between devices).",
      },
      placeholder_pattern: { type: "string", maxLength: 500, description: "Regex (.NET) for unresolved-looking texts; default the localisation-key pattern; \"\" turns it off." },
      min_touch_dp: { type: "number", minimum: 0, maximum: 200, description: "Smallest acceptable control in dp at each device's dpi (default 44; 0 = off)." },
      capture: { type: "boolean", description: "One composed frame per device check (default true)." },
      settle_timeout_ms: { type: "integer", minimum: 1000, maximum: 20000, description: "How long each device switch may take to settle (default 10000)." },
      allow_unsandboxed: {
        type: "boolean",
        description: "[screens: explore] Crawl although the session has no player-state snapshot and the project's playtest.begin did not run.",
      },
    },
  },
);
