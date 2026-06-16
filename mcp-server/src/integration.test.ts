import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm, mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import {
  ListResourcesRequestSchema,
  ReadResourceRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { ReadResourceResult } from "@modelcontextprotocol/sdk/types.js";

import { ALL_RESOURCES } from "./resources/index.js";
import { PingCache } from "./ping-cache.js";
import {
  ResourceRouter,
  type ResourceHandlerDeps,
} from "./resource-router.js";

const TEST_PORT = 19199;

function parseContent(result: ReadResourceResult): Record<string, unknown> {
  const first = result.contents[0];
  if (!first || !("text" in first) || typeof first.text !== "string") {
    throw new Error("expected a text content part");
  }
  return JSON.parse(first.text);
}

function makeMockLive(
  result: Record<string, unknown> = {
    status: "no_data",
    asOf: null,
    summary: null,
    nextStep:
      "Run unity_open_mcp_scan_paths or a gated mutation to populate the cache.",
  },
): ResourceHandlerDeps["live"] {
  return {
    readResource: async () => result,
  } as unknown as ResourceHandlerDeps["live"];
}

function createTestServer(
  projectPath: string,
  pingCache: PingCache = new PingCache(),
  liveResult?: Record<string, unknown>,
): Server {
  const server = new Server(
    { name: "unity-open-mcp", version: "0.1.0" },
    { capabilities: { tools: {}, resources: {} } },
  );

  const resourceRouter = new ResourceRouter({
    live: makeMockLive(liveResult),
    pingCache,
    projectPath,
    port: TEST_PORT,
  });

  server.setRequestHandler(ListResourcesRequestSchema, async () => ({
    resources: ALL_RESOURCES,
  }));

  server.setRequestHandler(ReadResourceRequestSchema, async (request) => {
    const { uri } = request.params;
    return resourceRouter.read(uri);
  });

  return server;
}

async function setupClient(
  projectPath: string,
  opts?: {
    pingCache?: PingCache;
    liveResult?: Record<string, unknown>;
  },
): Promise<{ client: Client; cleanup: () => Promise<void> }> {
  const server = createTestServer(
    projectPath,
    opts?.pingCache,
    opts?.liveResult,
  );
  const [clientTransport, serverTransport] =
    InMemoryTransport.createLinkedPair();
  await server.connect(serverTransport);

  const client = new Client(
    { name: "test-client", version: "0.1.0" },
    { capabilities: {} },
  );
  await client.connect(clientTransport);

  return {
    client,
    cleanup: async () => {
      await client.close();
      await server.close();
    },
  };
}

// ---------------------------------------------------------------------------
// Resource listing
// ---------------------------------------------------------------------------

test("integration: listResources returns exactly the three M6 URIs", async () => {
  const tmpDir = await mkdtemp(join(tmpdir(), "mcp-int-list-"));
  try {
    const { client, cleanup } = await setupClient(tmpDir);
    try {
      const { resources } = await client.listResources();

      const uris = resources.map((r) => r.uri).sort();
      assert.deepEqual(uris, [
        "unity-open-mcp://bridge/status",
        "unity-open-mcp://health/baseline",
        "unity-open-mcp://health/summary",
      ]);

      for (const r of resources) {
        assert.equal(r.mimeType, "application/json");
        assert.ok(r.name, `${r.uri} must have a name`);
      }
    } finally {
      await cleanup();
    }
  } finally {
    await rm(tmpDir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// health/summary — no_data when no bridge is running
// ---------------------------------------------------------------------------

test("integration: health/summary returns no_data in empty state", async () => {
  const tmpDir = await mkdtemp(join(tmpdir(), "mcp-int-sum-"));
  try {
    const { client, cleanup } = await setupClient(tmpDir);
    try {
      const { contents } = await client.readResource({
        uri: "unity-open-mcp://health/summary",
      });
      const body = parseContent({ contents });

      assert.equal(body.status, "no_data");
      assert.equal(body.asOf, null);
      assert.equal(body.summary, null);
      assert.ok(typeof body.nextStep === "string");
    } finally {
      await cleanup();
    }
  } finally {
    await rm(tmpDir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// health/summary — ok with data from bridge
// ---------------------------------------------------------------------------

test("integration: health/summary returns ok with cached summary", async () => {
  const tmpDir = await mkdtemp(join(tmpdir(), "mcp-int-sum-ok-"));
  try {
    const { client, cleanup } = await setupClient(tmpDir, {
      liveResult: {
        status: "ok",
        asOf: "2026-06-12T00:00:00Z",
        summary: { error: 0, warn: 1, info: 3 },
        source: "scan_paths",
      },
    });
    try {
      const { contents } = await client.readResource({
        uri: "unity-open-mcp://health/summary",
      });
      const body = parseContent({ contents });

      assert.equal(body.status, "ok");
      assert.equal(body.asOf, "2026-06-12T00:00:00Z");
      assert.deepEqual(body.summary, { error: 0, warn: 1, info: 3 });
      assert.equal(body.source, "scan_paths");
    } finally {
      await cleanup();
    }
  } finally {
    await rm(tmpDir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// health/baseline — no_baseline when file is missing
// ---------------------------------------------------------------------------

test("integration: health/baseline returns no_baseline when file is missing", async () => {
  const tmpDir = await mkdtemp(join(tmpdir(), "mcp-int-base-"));
  try {
    const { client, cleanup } = await setupClient(tmpDir);
    try {
      const { contents } = await client.readResource({
        uri: "unity-open-mcp://health/baseline",
      });
      const body = parseContent({ contents });

      assert.equal(body.status, "no_baseline");
      assert.equal(body.asOf, null);
      assert.ok(typeof body.baselinePath === "string");
      assert.ok(
        (body.nextStep as string).includes("baseline_create"),
        "nextStep should mention baseline_create",
      );
    } finally {
      await cleanup();
    }
  } finally {
    await rm(tmpDir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// health/baseline — ok when file exists
// ---------------------------------------------------------------------------

test("integration: health/baseline returns ok when baseline file exists", async () => {
  const tmpDir = await mkdtemp(join(tmpdir(), "mcp-int-base-ok-"));
  try {
    await mkdir(join(tmpDir, "CI"), { recursive: true });
    await writeFile(
      join(tmpDir, "CI", "unity-open-mcp-baseline.json"),
      JSON.stringify({
        schemaVersion: 1,
        platformProfile: "desktop",
        generatedAt: "2026-06-12T00:00:00Z",
        summary: { error: 0, warn: 2, info: 5 },
        rules: [],
      }),
    );

    const { client, cleanup } = await setupClient(tmpDir);
    try {
      const { contents } = await client.readResource({
        uri: "unity-open-mcp://health/baseline",
      });
      const body = parseContent({ contents });

      assert.equal(body.status, "ok");
      assert.ok(body.asOf, "asOf should be populated from file mtime");
      assert.equal(body.schemaVersion, 1);
      assert.equal(body.platformProfile, "desktop");
      assert.deepEqual(body.summary, { error: 0, warn: 2, info: 5 });
    } finally {
      await cleanup();
    }
  } finally {
    await rm(tmpDir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// bridge/status — no_data when no ping has occurred
// ---------------------------------------------------------------------------

test("integration: bridge/status returns no_data in fresh session", async () => {
  const tmpDir = await mkdtemp(join(tmpdir(), "mcp-int-bridge-"));
  try {
    const { client, cleanup } = await setupClient(tmpDir);
    try {
      const { contents } = await client.readResource({
        uri: "unity-open-mcp://bridge/status",
      });
      const body = parseContent({ contents });

      assert.equal(body.status, "no_data");
      assert.equal(body.asOf, null);
      assert.equal(body.connected, false);
    } finally {
      await cleanup();
    }
  } finally {
    await rm(tmpDir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// bridge/status — ok with cached ping snapshot
// ---------------------------------------------------------------------------

test("integration: bridge/status returns ok with cached ping", async () => {
  const tmpDir = await mkdtemp(join(tmpdir(), "mcp-int-bridge-ok-"));
  try {
    const pingCache = new PingCache();
    pingCache.record({
      connected: true,
      projectPath: "/path/to/MyGame",
      unityVersion: "6000.0.23f1",
      bridgeVersion: "0.1.0",
      mode: "live",
      compiling: false,
      isPlaying: false,
    });

    const { client, cleanup } = await setupClient(tmpDir, { pingCache });
    try {
      const { contents } = await client.readResource({
        uri: "unity-open-mcp://bridge/status",
      });
      const body = parseContent({ contents });

      assert.equal(body.status, "ok");
      assert.ok(body.asOf, "asOf should be populated");
      assert.equal(body.connected, true);
      assert.equal(body.projectPath, "/path/to/MyGame");
      assert.equal(body.bridgePort, TEST_PORT);
      assert.equal(body.compiling, false);
      assert.equal(body.isPlaying, false);
    } finally {
      await cleanup();
    }
  } finally {
    await rm(tmpDir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// Sequential reads of all three URIs in empty state
// ---------------------------------------------------------------------------

test("integration: all three URIs readable in sequence with correct empty-state status", async () => {
  const tmpDir = await mkdtemp(join(tmpdir(), "mcp-int-seq-"));
  try {
    const { client, cleanup } = await setupClient(tmpDir);
    try {
      const uris = [
        "unity-open-mcp://health/summary",
        "unity-open-mcp://health/baseline",
        "unity-open-mcp://bridge/status",
      ];

      const statuses: string[] = [];
      const asOfs: unknown[] = [];

      for (const uri of uris) {
        const { contents } = await client.readResource({ uri });
        const body = parseContent({ contents });
        statuses.push(body.status as string);
        asOfs.push(body.asOf);
      }

      assert.deepEqual(statuses, ["no_data", "no_baseline", "no_data"]);
      assert.deepEqual(asOfs, [null, null, null]);
    } finally {
      await cleanup();
    }
  } finally {
    await rm(tmpDir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// All reads return correct mimeType
// ---------------------------------------------------------------------------

test("integration: resource reads return application/json mimeType", async () => {
  const tmpDir = await mkdtemp(join(tmpdir(), "mcp-int-mime-"));
  try {
    const { client, cleanup } = await setupClient(tmpDir);
    try {
      for (const uri of [
        "unity-open-mcp://health/summary",
        "unity-open-mcp://health/baseline",
        "unity-open-mcp://bridge/status",
      ]) {
        const { contents } = await client.readResource({ uri });
        const first = contents[0];
        assert.ok(first, `${uri} must return a content part`);
        assert.equal(first!.mimeType, "application/json");
        assert.ok("text" in first! && typeof first!.text === "string", `${uri} must return text content`);
      }
    } finally {
      await cleanup();
    }
  } finally {
    await rm(tmpDir, { recursive: true, force: true });
  }
});
