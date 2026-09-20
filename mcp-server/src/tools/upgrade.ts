import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_OFF_PROP, makeTool } from "./schema-fragments.js";

export const upgrade = makeTool(
  "unity_open_mcp_upgrade",
  "Preview or apply a coordinated update of the open Unity project to one trio version. " +
    "Scans project and home-scoped MCP client configs, agent-facing prose/examples, and the " +
    "bridge + verify UPM pins. Home configs are rewritten only when their UNITY_PROJECT_PATH " +
    "belongs exclusively to the open project. `dry_run` defaults true and returns the exact " +
    "per-file report without writing. With `dry_run:false`, config/prose files receive one-time " +
    "`.bak` backups before rewriting; a Git-installed bridge and verify pair are then re-pinned " +
    "atomically through Unity Package Manager, which may reload the Editor. Embedded/file bridge " +
    "installs are preserved and reported while selected config/prose updates remain available. " +
    "Omit `target_version` to resolve the latest published npm version explicitly for this call.",
  {
    properties: {
      target_version: {
        type: "string",
        pattern: "^\\d+\\.\\d+\\.\\d+$",
        description: "Target trio version (X.Y.Z). Omit to query the latest npm release.",
      },
      dry_run: {
        type: "boolean",
        default: true,
        description: "Preview only by default. Set false to write selected files and schedule UPM.",
      },
      update_upm: {
        type: "boolean",
        default: true,
        description: "Update the bridge + verify Git packages atomically when the bridge source is Git.",
      },
      update_project_configs: {
        type: "boolean",
        default: true,
        description: "Update MCP client configs found in this project or its repository ancestors.",
      },
      update_home_configs: {
        type: "boolean",
        default: true,
        description: "Update home-scoped configs only when the entry belongs exclusively to this project.",
      },
      update_prose: {
        type: "boolean",
        default: true,
        description: "Update allowlisted agent prose, installed core skills, and MCP config examples.",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        default: ["Packages/manifest.json"],
        description:
          "Audit scope materialized automatically. The gate is off because this workflow mutates " +
          "package/configuration pins rather than Unity assets; packages-lock.json is implicit.",
      },
      gate: {
        ...GATE_OFF_PROP,
        description: "Defaults off: project-asset validation does not model external configs or UPM pins.",
      },
    },
  },
);
