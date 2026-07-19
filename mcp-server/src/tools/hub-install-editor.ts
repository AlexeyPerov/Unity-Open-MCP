import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M26 Plan 2 — Unity Hub control: install editor. Local-routed (fires the
// unityhub:// deep link via the OS handler; never hits the Unity bridge).
//
// MUTATING — installs software at the system level. This is NOT a project-
// asset mutation, so paths_hint is N/A and the call is gate-free (the gate
// validates project-asset-reference fallout, which does not apply). The
// response still carries the gate-consistent mutation/agentNextSteps envelope
// shape so agents parse it uniformly. The install itself happens inside Unity
// Hub (single instance, native progress UI); there is no in-call completion
// detection — poll hub_list_editors afterwards to confirm.
export const hubInstallEditor = makeTool(
  "unity_open_mcp_hub_install_editor",
  "Install a specific Unity Editor version by opening Unity Hub at its " +
    "native install dialog via the unityhub:// deep link. The Hub must be " +
    "installed and registered as the system handler for the unityhub:// " +
    "scheme. Optionally pass the build changeset (available from " +
    "hub_available_releases) to pin the exact build for archived versions. " +
    "Mutating: this installs software (a multi-GB editor download) — it is a " +
    "system-level operation, not a project-asset mutation, so paths_hint is " +
    "N/A and the call is gate-free. The install runs inside the Hub with its " +
    "own progress UI; this call returns once the deep link is accepted, NOT " +
    "when the download completes. There is no in-call completion detection " +
    "(the Hub owns the process) — poll hub_list_editors after the Hub " +
    "finishes to confirm the new editor. Cross-platform: uses the OS URL " +
    "handler (open on macOS, xdg-open on Linux, start on Windows).",
  {
    required: ["version"],
        properties: {
          version: {
            type: "string",
            description:
              "Unity version to install (e.g. '2022.3.20f1', '6000.3.18f1'). " +
              "Discover available versions + their changesets via " +
              "hub_available_releases.",
          },
          changeset: {
            type: "string",
            description:
              "Optional build changeset hash (e.g. '88b47c5e7076'). When present, " +
              "the deep link is unityhub://<version>/<changeset>, which pins the " +
              "exact build (required for some archived versions). Omit for a " +
              "changeset-less install (older Hub versions may ignore these).",
          },
        },
  },
);
