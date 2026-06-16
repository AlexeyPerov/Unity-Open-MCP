import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { knownClientKeys } from "../skill/client-paths.js";

// The `clients` enum is derived from the single-source manifest at
// `skills/client-paths.json`. Do not hand-maintain a literal array
// here — edit the manifest instead.
const CLIENT_ENUM = knownClientKeys();
const clientListDoc = CLIENT_ENUM.map((c) => `\`${c}\``).join(", ");

export const generateSkill: Tool = {
  name: "unity_agent_generate_skill",
  description:
    "Generate a project-specific agent skill file (SKILL.md) that reflects the " +
    "actual project state: Unity version, installed packages (including bridge/verify " +
    "versions), available tools and verify rules, key MonoBehaviour/ScriptableObject " +
    "types, and the mutate→gate→fix workflow. Set write=true to persist the file into " +
    "the client skill directories derived from skills/client-paths.json. Regenerate " +
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
          enum: CLIENT_ENUM,
        },
        description:
          "Which client skill directories to write to. Only used when write=true. " +
          `Defaults to ["claude"]. Allowed values: ${clientListDoc}. Each entry writes ` +
          "to the project-relative path declared for that client in skills/client-paths.json.",
      },
    },
    additionalProperties: false,
  },
};
