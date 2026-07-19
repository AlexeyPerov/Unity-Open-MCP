import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M26 Plan 2 — Unity Hub control: set install path. Local-routed (shells out
// to the Hub headless CLI; never hits the Unity bridge). This is the one Hub
// operation the unityhub:// deep link cannot perform, so it genuinely requires
// the Hub binary. MUTATING system-level op — paths_hint is N/A, gate-free.
export const hubSetInstallPath = makeTool(
  "unity_open_mcp_hub_set_install_path",
  "Set the default installation directory for Unity Editors managed by " +
    "Unity Hub, via the Hub headless CLI (`Unity Hub --headless install-path " +
    "--set <path>`). This is the one Hub operation the unityhub:// deep link " +
    "transport cannot perform, so it requires the Hub binary directly. " +
    "Mutating: this changes a Hub configuration setting at the system level — " +
    "paths_hint is N/A and the call is gate-free. Returns a structured " +
    "`hub_cli_not_found` error when the Hub binary cannot be found (set the " +
    "UNITY_HUB_PATH env var to resolve it). Cross-platform Hub CLI binary " +
    "locations: '/Applications/Unity Hub.app/Contents/MacOS/Unity Hub' " +
    "(macOS), 'C:\\Program Files\\Unity Hub\\Unity Hub.exe' (Windows), " +
    "'~/Unity Hub/Unity Hub' (Linux).",
  {
    required: ["path"],
        properties: {
          path: {
            type: "string",
            description: "New default installation directory for Unity Editors.",
          },
        },
  },
);
