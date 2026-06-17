// M13 T4.4 — bridge event stream client.
//
// The MCP server runs behind a stdio transport, so it can't directly forward
// bridge SSE to MCP notifications without a background SSE reader. This module
// owns that reader: a single SSE subscription per server process, parsed into
// events and pushed into an in-memory queue. The `unity_agent_pull_events`
// tool drains the queue per call so an agent can poll incremental console /
// compile-state events without burning an HTTP round-trip on every check.
//
// Lifecycle:
//   - ensureSubscription() is called on every tool dispatch; it starts the SSE
//     reader once and is a no-op afterwards.
//   - The reader reconnects with the last subscriber id so it keeps its cursor
//     across disconnects (a 10-minute SSE timeout or a Unity domain reload).
//   - On bridge-unavailable (connection refused), ensureSubscription records
//     the failure and the tool surfaces a `bridge_unavailable` error to the
//     agent instead of hanging.
//
// No new runtime deps — uses only `node:crypto`, the global `fetch`, and the
// `node:events` API.

import { randomBytes } from "node:crypto";

export interface BridgeEvent {
  seq: number;
  ts: string;
  type: "log" | "editor_state" | "ready" | "missed" | "close";
  logType?: string;
  message?: string;
  stack?: string;
  state?: string;
  isCompiling?: boolean;
  isPlaying?: boolean;
}

export interface PullResult {
  subscriberId: string;
  events: BridgeEvent[];
  /** Count of events dropped from the queue before this pull (overflow). */
  dropped: number;
  /** Whether the SSE reader is currently connected. */
  connected: boolean;
  /** Whether this pull started the subscription (first call). */
  started: boolean;
  /** Last reconnect failure reason, when `connected` is false. */
  lastError: string | null;
}

const QUEUE_CAPACITY = 500;

export class BridgeEventStream {
  private subscriberId: string;
  private queue: BridgeEvent[] = [];
  private dropped = 0;
  private connected = false;
  private lastError: string | null = null;
  private abortController: AbortController | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private started = false;

  constructor(
    private readonly baseUrl: string,
    subscriberId?: string,
    private readonly authToken?: string,
  ) {
    this.subscriberId =
      subscriberId ?? randomBytes(16).toString("hex");
  }

  /**
   * Start the SSE reader if it isn't running. Safe to call repeatedly; returns
   * true when a fresh subscription was started this call.
   */
  ensureSubscription(): boolean {
    if (this.started) return false;
    this.started = true;
    this.connect();
    return true;
  }

  private connect(): void {
    if (this.abortController) return;
    this.abortController = new AbortController();
    const url = `${this.baseUrl}/events?subscriber=${encodeURIComponent(
      this.subscriberId,
    )}&max_per_poll=100`;

    // SSE read is streaming; consume body manually so we can split on
    // double-newline event boundaries. M14 — carry the bearer token so the
    // stream is gated the same way as tool/ping requests.
    const headers: Record<string, string> = { Accept: "text/event-stream" };
    if (this.authToken) headers["Authorization"] = `Bearer ${this.authToken}`;
    fetch(url, {
      method: "GET",
      headers,
      signal: this.abortController.signal,
    })
      .then((res) => {
        if (!res.ok || !res.body) {
          throw new Error(`HTTP ${res.status}`);
        }
        this.connected = true;
        this.lastError = null;
        this.pump(res.body);
      })
      .catch((err: unknown) => {
        this.connected = false;
        const message =
          err instanceof Error ? err.message : String(err);
        // AbortError means we intentionally stopped; treat as clean disconnect.
        this.lastError = message.includes("abort") ? null : message;
        this.scheduleReconnect();
      });
  }

  private async pump(body: ReadableStream<Uint8Array>): Promise<void> {
    const reader = body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    try {
      // eslint-disable-next-line no-constant-condition
      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        let sep: number;
        // SSE events are separated by a blank line.
        while ((sep = buffer.indexOf("\n\n")) >= 0) {
          const block = buffer.slice(0, sep);
          buffer = buffer.slice(sep + 2);
          this.handleBlock(block);
        }
      }
    } catch (err: unknown) {
      // Network drop mid-stream — schedule a reconnect.
      const message = err instanceof Error ? err.message : String(err);
      this.lastError = message;
    } finally {
      this.connected = false;
      try { await reader.cancel(); } catch { /* ignore */ }
      this.scheduleReconnect();
    }
  }

  private handleBlock(block: string): void {
    const evt = BridgeEventStream.parseSseBlock(block);
    if (evt) this.enqueue(evt);
  }

  /**
   * Parse one SSE block (the text between two blank-line separators) into a
   * BridgeEvent, or null when the block carries no data. Exposed for unit
   * testing — the SSE reader loop calls it via handleBlock.
   */
  static parseSseBlock(block: string): BridgeEvent | null {
    let eventName = "message";
    const dataLines: string[] = [];
    for (const line of block.split("\n")) {
      if (line.startsWith("event:")) {
        eventName = line.slice(6).trim();
      } else if (line.startsWith("data:")) {
        dataLines.push(line.slice(5).replace(/^ /, ""));
      }
    }
    if (dataLines.length === 0) return null;
    const data = dataLines.join("\n");

    let parsed: Record<string, unknown> = {};
    try {
      parsed = JSON.parse(data) as Record<string, unknown>;
    } catch {
      // Non-JSON payload (e.g. "close"); keep raw.
      parsed = { raw: data };
    }

    const evt: BridgeEvent = {
      seq: typeof parsed.seq === "number" ? parsed.seq : Date.now(),
      ts: typeof parsed.ts === "string" ? parsed.ts : new Date().toISOString(),
      type: eventName as BridgeEvent["type"],
    };

    if (eventName === "log") {
      evt.logType = typeof parsed.logType === "string" ? parsed.logType : "log";
      evt.message = typeof parsed.message === "string" ? parsed.message : "";
      evt.stack = typeof parsed.stack === "string" ? parsed.stack : undefined;
    } else if (eventName === "editor_state") {
      evt.state = typeof parsed.state === "string" ? parsed.state : "";
      evt.isCompiling = parsed.isCompiling === true;
      evt.isPlaying = parsed.isPlaying === true;
    } else if (eventName === "missed") {
      // SSE "missed" marker — surfaces as its own event so the agent sees the gap.
      evt.message = `missed=${parsed.missed ?? "unknown"}`;
    }

    return evt;
  }

  private enqueue(evt: BridgeEvent): void {
    this.queue.push(evt);
    while (this.queue.length > QUEUE_CAPACITY) {
      this.queue.shift();
      this.dropped++;
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer) return;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.abortController = null;
      this.connect();
    }, 2000);
  }

  /** Drain all queued events. The subscription keeps running. */
  drain(maxEvents: number): BridgeEvent[] {
    const cap = maxEvents > 0 && maxEvents <= 1000 ? maxEvents : 100;
    const out = this.queue.slice(0, cap);
    this.queue = this.queue.slice(cap);
    return out;
  }

  /** One-shot pull: start the subscription if needed and drain. */
  pull(maxEvents: number): PullResult {
    const started = this.ensureSubscription();
    const events = this.drain(maxEvents);
    return {
      subscriberId: this.subscriberId,
      events,
      dropped: this.dropped,
      connected: this.connected,
      started,
      lastError: this.lastError,
    };
  }

  /** Stop the reader and clear state. Idempotent; used in tests. */
  stop(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.abortController) {
      this.abortController.abort();
      this.abortController = null;
    }
    this.started = false;
    this.connected = false;
    this.queue = [];
    this.dropped = 0;
  }

  get isConnected(): boolean {
    return this.connected;
  }

  get id(): string {
    return this.subscriberId;
  }
}
