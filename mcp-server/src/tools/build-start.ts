import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// M16 Plan 9 — typed player build trigger. Mutating + DESTRUCTIVE:
// BuildPipeline.BuildPlayer. BuildPlayer is on the default deny list
// (BridgeDenyList.DefaultCSharpPatterns), so this typed tool mirrors the
// destructive-menu pattern: it refuses UNLESS the request passes
// gate: "off" + confirm_bypass: true. When bypassed, the full gate path
// still runs so the response carries any post-build asset-reference fallout.
//
// paths_hint: the build writes a binary player outside Assets/ and may touch
// ProjectSettings assets. Agents typically scope paths_hint to the build
// output folder (and/or "ProjectSettings").
export const buildStart: Tool = {
  name: "unity_open_mcp_build_start",
  description:
    "Mutating + DESTRUCTIVE: trigger a player build via BuildPipeline.BuildPlayer. " +
    "BuildPlayer is on the default deny list (it is multi-minute and writes a binary player " +
    "outside Assets/), so this tool REFUSES unless the request passes BOTH gate: \"off\" AND " +
    "confirm_bypass: true. When bypassed, the full gate path still runs and the response " +
    "carries any post-build asset-reference fallout. Uses the scenes enabled in build settings " +
    "(EditorBuildSettings.scenes) and the active build target; pass output_path to choose the " +
    "destination (defaults to Builds/<target>/Build). development: true adds the Development " +
    "build option; allow_debugging: true adds AllowDebugging. Returns the BuildReport summary " +
    "(result, total time, size, errors/warnings, per-step durations). `paths_hint` should scope " +
    "to the build output folder (and/or \"ProjectSettings\" since build settings can be touched).",
  inputSchema: {
    type: "object",
    required: ["paths_hint"],
    properties: {
      output_path: {
        type: "string",
        description:
          "Destination for the player build. Defaults to 'Builds/<activeTarget>/Build' when " +
          "omitted. The folder is created if missing.",
      },
      development: {
        type: "boolean",
        default: false,
        description: "Add the Development build option (BuildOptions.Development).",
      },
      allow_debugging: {
        type: "boolean",
        default: false,
        description: "Add the AllowDebugging build option (BuildOptions.AllowDebugging).",
      },
      paths_hint: {
        type: "array",
        items: { type: "string" },
        description:
          "Mutation scope — pass the build output folder (e.g. [\"Builds/StandaloneWindows64\"]) " +
          "and/or [\"ProjectSettings\"]. There is no whole-project fallback.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description:
          "Gate mode. Must be \"off\" for the build to proceed (BuildPlayer is on the deny list).",
      },
      confirm_bypass: {
        type: "boolean",
        default: false,
        description:
          "Required to proceed. Bypass the deny heuristic for BuildPipeline.BuildPlayer. " +
          "Requires gate: \"off\" as well — BOTH flags must be set. The bypass is audited.",
      },
    },
    additionalProperties: false,
  },
};
