import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

export type { CallToolResult };

export interface Router {
  route(
    toolName: string,
    args: Record<string, unknown>,
  ): Promise<CallToolResult>;
}
