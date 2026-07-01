import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const applyFix: Tool = {
  name: "unity_open_mcp_apply_fix",
  description:
    "Apply a verify rule fix action. Supports dry_run (default true) to preview the fix before applying. " +
    "Returns gate envelope when dry_run is false (dry_run short-circuits the gate entirely). " +
    "Implemented fixes: remove_missing_script (safe), relink_broken_guid (unsafe — needs target_guid), " +
    "remove_orphan_meta (safe), fix_duplicate_guid (unsafe), " +
    "reassign_missing_texture (unsafe — needs target_texture), reassign_missing_shader (unsafe — needs target_shader). " +
    "Safe auto-fix rollback: a non-dry-run apply that fails or introduces new errors under enforce is " +
    "restored to its pre-fix state and the response carries a top-level `rollback` block " +
    "({rolledBack, reason, restoredPaths}).",
  inputSchema: {
    type: "object",
    required: ["issue_id"],
    properties: {
      fix_id: {
        type: "string",
        description:
          "Fix action id from issue payload (e.g. remove_missing_script, relink_broken_guid). " +
          "If omitted, the response lists every fix that can resolve the given issue_id.",
      },
      issue_id: {
        type: "string",
        description: "Issue key from validate_edit or scan_paths (format: ruleId|severity|assetPath|issueCode)",
      },
      target_guid: {
        type: "string",
        description:
          "Optional. Replacement GUID for relink_broken_guid — the chosen target out of the " +
          "candidates the dry_run preview advertises. Ignored by other fixes.",
      },
      target_texture: {
        type: "string",
        description:
          "Optional. The chosen texture for reassign_missing_texture — an asset path or 32-hex GUID " +
          "out of the candidates the dry_run preview advertises. Ignored by other fixes.",
      },
      target_shader: {
        type: "string",
        description:
          "Optional. The chosen shader for reassign_missing_shader — a shader name (e.g. 'Standard') " +
          "or asset path, out of the candidates the dry_run preview advertises. Ignored by other fixes.",
      },
      dry_run: {
        type: "boolean",
        default: true,
        description:
          "Preview the fix without applying. Default true. dry_run skips the gate entirely.",
      },
      gate: {
        enum: ["enforce", "warn", "off"],
        default: "enforce",
        description: "Gate mode when dry_run is false. Ignored for dry_run.",
      },
    },
    additionalProperties: false,
  },
};
