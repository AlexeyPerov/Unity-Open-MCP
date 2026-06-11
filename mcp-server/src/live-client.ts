import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Router } from "./router.js";

export class LiveClient implements Router {
  private baseUrl: string;

  constructor(port: number) {
    this.baseUrl = `http://127.0.0.1:${port}`;
  }

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
            mutation: { success: false, output: null, error: null },
            gate: {
              mode: "off",
              skipped: true,
              validation: null,
              delta: null,
            },
            agentNextSteps: [
              "Live routing not yet connected. Implement in Task 2.",
            ],
          }),
        },
      ],
      isError: true,
    };
  }
}
