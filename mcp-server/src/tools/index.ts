import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { ping } from "./ping.js";
import { executeCsharp } from "./execute-csharp.js";
import { invokeMethod } from "./invoke-method.js";
import { executeMenu } from "./execute-menu.js";
import { findMembers } from "./find-members.js";
import { editorStatus } from "./editor-status.js";

export const M2_TOOLS: Tool[] = [
  ping,
  executeCsharp,
  invokeMethod,
  executeMenu,
  findMembers,
];

export const M2_5_TOOLS: Tool[] = [editorStatus];

export const ALL_TOOLS: Tool[] = [...M2_TOOLS, ...M2_5_TOOLS];
