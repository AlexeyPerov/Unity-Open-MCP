import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M26 Plan 2 — Unity Hub control: install modules. Local-routed (fires the
// unityhub:// deep link; never hits the Unity bridge). Like hub_install_editor
// this opens the Hub's native install dialog — the Hub does not expose a
// module-only deep link, so the same version deep link is used and the module
// selection happens in the Hub UI. MUTATING system-level op, gate-free.
export const hubInstallModules: Tool = {
  name: "unity_open_mcp_hub_install_modules",
  description:
    "Add platform build modules to an already-installed Unity Editor " +
    "version by opening Unity Hub at its install/module dialog via the " +
    "unityhub:// deep link. The Hub must be installed and registered as the " +
    "unityhub:// handler. Mutating: this installs software (platform module " +
    "downloads) at the system level — paths_hint is N/A and the call is " +
    "gate-free. The module install runs inside the Hub; this call returns " +
    "once the deep link is accepted, not when the download completes. Poll " +
    "hub_list_editors (which scans Data/PlaybackEngines) afterwards to " +
    "confirm the new modules. Note: the unityhub:// scheme does not expose a " +
    "module-specific deep link, so the Hub opens at the version's install " +
    "dialog where the operator selects the modules — the modules argument is " +
    "informational and surfaced in the response for the operator to act on.",
  inputSchema: {
    type: "object",
    required: ["version"],
    properties: {
      version: {
        type: "string",
        description: "Target already-installed Unity version (e.g. '6000.3.18f1').",
      },
      modules: {
        type: "array",
        items: { type: "string" },
        description:
          "Modules to add (informational — surfaced in the response for the " +
          "operator to select in the Hub dialog): android, ios, webgl, " +
          "linux-il2cpp, mac-il2cpp, windows-il2cpp, etc.",
      },
      changeset: {
        type: "string",
        description:
          "Optional build changeset (from hub_available_releases) to pin the " +
          "exact build. Forwarded as the deep-link changeset segment.",
      },
    },
    additionalProperties: false,
  },
};
