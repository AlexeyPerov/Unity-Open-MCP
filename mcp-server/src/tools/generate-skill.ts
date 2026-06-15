import type { Tool } from "@modelcontextprotocol/sdk/types.js";

export const generateSkill: Tool = {
  name: "unity_agent_generate_skill",
  description:
    "Generate a project-specific agent skill file (SKILL.md) that reflects the " +
    "actual project state: Unity version, installed packages (including bridge/verify " +
    "versions), available tools and verify rules, key MonoBehaviour/ScriptableObject " +
    "types, and the mutate→gate→fix workflow. Set write=true to persist the file into " +
    ".claude/skills/ (and optionally .cursor/skills/ or .opencode/skills/). Regenerate " +
    "after package or script changes to keep the skill current.",
  inputSchema: {
    type: "object",
    properties: {
      write: {
        type: "boolean",
        default: false,
        description:
          "When true, write the generated skill file to the client skill directories. " +
          "When false (default), return the skill content as a string for preview.",
      },
      clients: {
        type: "array",
        items: {
          enum: ["claude", "cursor", "opencode"],
        },
        description:
          "Which client skill directories to write to. Only used when write=true. " +
          "Defaults to [\"claude\"]. Each entry writes to the project's " +
          ".claude/skills/, .cursor/skills/, or .opencode/skills/ respectively.",
      },
    },
    additionalProperties: false,
  },
};
