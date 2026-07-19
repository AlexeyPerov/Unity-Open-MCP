import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { makeTool } from "./schema-fragments.js";

// M26 Plan 2 — Unity Hub control: get install path. Local-routed. Tries the
// Hub CLI (install-path) first, falling back to filesystem inference from the
// Hub install roots when the CLI is unavailable. Read-only, gate-free.
export const hubGetInstallPath = makeTool(
  "unity_open_mcp_hub_get_install_path",
  "Get the default installation directory for Unity Editors managed by " +
    "Unity Hub. Tries the Hub headless CLI (`Unity Hub --headless " +
    "install-path`) first; when the Hub CLI is not found, falls back to " +
    "inferring the path from the OS-default Hub install roots. The response " +
    "carries a `source` field (hub-cli | filesystem | none) so the caller can " +
    "tell how the value was resolved, and a structured `hub_cli_not_found` " +
    "error when neither path is available. Read-only, gate-free, no Unity " +
    "Editor or bridge connection required.",
  {
    properties: {},
  },
);
