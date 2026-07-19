import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { GATE_PROP, PATHS_HINT_TYPE, makeTool } from "./schema-fragments.js";

// M20 Plan 9 / T20.9.3 — Project Settings remainder. Mutating: set the active
// quality level (optionally per-platform). Writes ProjectSettings/Quality.asset
// and runs the full gate path.
export const settingsSetQualityLevel = makeTool(
  "unity_open_mcp_settings_set_quality_level",
  "Mutating: set the active quality level. Writes ProjectSettings/Quality.asset " +
    "and runs the full gate path; `paths_hint` must be " +
    "[\"ProjectSettings/Quality.asset\"]. quality_level may be a level name or " +
    "an index. platform (omit = all platforms) is a build-target name. NOTE: " +
    "Unity's public QualitySettings API does not expose a per-platform " +
    "active-level setter — when platform is given, the global level is set and " +
    "the requested platform scope is reported; use execute_csharp against the " +
    "internal QualitySettings API for precise per-platform control. Use " +
    "settings_get_quality to enumerate the levels first.",
  {
    required: ["quality_level", "paths_hint"],
        properties: {
          quality_level: {
            description:
              "The quality level to activate. Pass a level name (string) or an " +
              "index (int). Enumerate with settings_get_quality.",
          },
          platform: {
            type: "string",
            description:
              "Optional build-target name (e.g. \"StandaloneWindows64\", \"iOS\"). " +
              "Omit to switch the active level globally. The public API does not " +
              "expose a per-platform active-level setter; the global level is set " +
              "and the requested scope is reported.",
          },
          paths_hint: { ...PATHS_HINT_TYPE, description: "Mutation scope — pass [\"ProjectSettings/Quality.asset\"]. There " + "is no whole-project fallback." },
          gate: { ...GATE_PROP },
        },
  },
);
