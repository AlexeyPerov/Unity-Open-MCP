// Tests for project-settings.ts — the TS-side reader of
// `.unity-open-mcp/settings.json` for server-side (bridge-independent) keys.
// The motivating consumer is `resource_pressure`'s configurable fd ceiling (D).

import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
  readFdCeiling,
  settingsFilePath,
  FD_CEILING_MIN,
  FD_CEILING_MAX,
} from "./project-settings.js";
import { FD_CEILING_DEFAULT } from "./process-diagnostics.js";

async function withTmp(name: string, fn: (tmp: string) => Promise<void>): Promise<void> {
  const tmp = await mkdtemp(join(tmpdir(), name));
  try {
    await fn(tmp);
  } finally {
    await rm(tmp, { recursive: true, force: true });
  }
}

async function writeSettings(tmp: string, json: string): Promise<void> {
  await mkdir(join(tmp, ".unity-open-mcp"), { recursive: true });
  await writeFile(settingsFilePath(tmp), json, "utf8");
}

test("readFdCeiling: missing settings file → default, source default", async () => {
  await withTmp("ps-missing-", async (tmp) => {
    const r = readFdCeiling(tmp);
    assert.equal(r.ceiling, FD_CEILING_DEFAULT);
    assert.equal(r.source, "default");
  });
});

test("readFdCeiling: missing resourcePressure slice → default", async () => {
  await withTmp("ps-noslice-", async (tmp) => {
    await writeSettings(tmp, JSON.stringify({ verify: { severityThreshold: "warn" } }));
    const r = readFdCeiling(tmp);
    assert.equal(r.ceiling, FD_CEILING_DEFAULT);
    assert.equal(r.source, "default");
  });
});

test("readFdCeiling: valid fdCeiling → applied, source config", async () => {
  await withTmp("ps-valid-", async (tmp) => {
    await writeSettings(tmp, JSON.stringify({ resourcePressure: { fdCeiling: 2048 } }));
    const r = readFdCeiling(tmp);
    assert.equal(r.ceiling, 2048);
    assert.equal(r.source, "config");
  });
});

test("readFdCeiling: coexists with C#-side keys (ignored slice)", async () => {
  // The same file holds C#-read keys (verify/bridge slices). The TS reader
  // must ignore them and still resolve its own slice.
  await withTmp("ps-mixed-", async (tmp) => {
    await writeSettings(
      tmp,
      JSON.stringify({
        verify: { severityThreshold: "warn" },
        bridge: { batchExecuteMaxCommands: 50 },
        resourcePressure: { fdCeiling: 4096 },
      }),
    );
    const r = readFdCeiling(tmp);
    assert.equal(r.ceiling, 4096);
    assert.equal(r.source, "config");
  });
});

test("readFdCeiling: non-numeric fdCeiling → default (no throw)", async () => {
  await withTmp("ps-nonnumeric-", async (tmp) => {
    await writeSettings(tmp, JSON.stringify({ resourcePressure: { fdCeiling: "lots" } }));
    const r = readFdCeiling(tmp);
    assert.equal(r.ceiling, FD_CEILING_DEFAULT);
    assert.equal(r.source, "default");
  });
});

test("readFdCeiling: below-min fdCeiling → default (treated as not configured)", async () => {
  await withTmp("ps-belowmin-", async (tmp) => {
    await writeSettings(tmp, JSON.stringify({ resourcePressure: { fdCeiling: FD_CEILING_MIN - 1 } }));
    const r = readFdCeiling(tmp);
    assert.equal(r.ceiling, FD_CEILING_DEFAULT);
    assert.equal(r.source, "default");
  });
});

test("readFdCeiling: above-max fdCeiling → clamped to max, source config", async () => {
  await withTmp("ps-abovemax-", async (tmp) => {
    await writeSettings(tmp, JSON.stringify({ resourcePressure: { fdCeiling: FD_CEILING_MAX + 1 } }));
    const r = readFdCeiling(tmp);
    assert.equal(r.ceiling, FD_CEILING_MAX);
    assert.equal(r.source, "config");
  });
});

test("readFdCeiling: unparseable JSON → default (no throw)", async () => {
  await withTmp("ps-broken-", async (tmp) => {
    await writeSettings(tmp, "{ not json");
    const r = readFdCeiling(tmp);
    assert.equal(r.ceiling, FD_CEILING_DEFAULT);
    assert.equal(r.source, "default");
  });
});

test("readFdCeiling: fractional fdCeiling is floored", async () => {
  await withTmp("ps-floor-", async (tmp) => {
    await writeSettings(tmp, JSON.stringify({ resourcePressure: { fdCeiling: 2048.9 } }));
    const r = readFdCeiling(tmp);
    assert.equal(r.ceiling, 2048);
    assert.equal(r.source, "config");
  });
});

test("settingsFilePath lives under the .unity-open-mcp dir", () => {
  const p = settingsFilePath("/proj");
  assert.ok(p.endsWith(join(".unity-open-mcp", "settings.json")));
});
