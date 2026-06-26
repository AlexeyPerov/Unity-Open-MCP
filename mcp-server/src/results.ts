// Shared CallToolResult error factory.
//
// This used to live as three near-identical private helpers in batch-spawn.ts,
// compressible-router.ts, and live-client.ts — with two different positional
// argument orders (the compressible-router copy had `message, code` while the
// others used `code, message`). That made it easy to swap the arguments by
// mistake at any call site. The single named-argument factory below removes the
// ambiguity: code, message, and the optional `detail` override are named.
//
// Wire shape is unchanged. When `detail` is omitted (or nullish), the result
// body is `{ error: { code, message } }` — exactly what every downstream parser
// (and every existing test asserting `body.error.code`) expects. When `detail`
// is supplied, it replaces the error envelope entirely (callers that build a
// richer custom body rely on this).

import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

export interface ErrorResultInput {
  /** Stable machine-readable code (e.g. `bridge_error`, `batch_not_supported`). */
  code: string;
  /** Human-readable explanation. */
  message: string;
  /**
   * Optional custom body. When provided (non-nullish), it replaces the default
   * `{ error: { code, message } }` envelope. Use this when a caller needs to
   * surface richer structured data (e.g. HTTP status + body from the bridge).
   */
  detail?: unknown;
}

export function makeErrorResult(input: ErrorResultInput): CallToolResult {
  const body = input.detail ?? { error: { code: input.code, message: input.message } };
  return {
    content: [{ type: "text", text: JSON.stringify(body) }],
    isError: true,
  };
}
