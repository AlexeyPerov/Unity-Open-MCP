import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";

export class BatchSpawn implements Router {
  async route(
    toolName: string,
    _args: Record<string, unknown>,
  ): Promise<CallToolResult> {
    void toolName;
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            error: {
              code: "batch_not_supported",
              message: "Batch mode is not available until M5.",
            },
          }),
        },
      ],
      isError: true,
    };
  }
}
