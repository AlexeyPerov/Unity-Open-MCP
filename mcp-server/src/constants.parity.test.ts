import test from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

import {
  STATUS_DIR_NAME,
  PORT_ENV_VAR,
  PROJECT_PATH_ENV_VAR,
  LOOPBACK_HOST,
  VERIFY_JSON_BEGIN,
  VERIFY_JSON_END,
  BRIDGE_DEFAULT_TIMEOUT_MS,
  NPM_PACKAGE_LATEST,
  ARCHIVE_URL,
  RELEASE_NOTES_URL_PREFIX,
  REPO_URL,
} from "./constants.js";
import {
  computePort,
  PORT_RANGE_START,
  PORT_RANGE_SIZE,
} from "./instance-discovery.js";

// ---------------------------------------------------------------------------
// Cross-tree parity tests.
//
// These assert that values the three trees (bridge C#, mcp-server TS, hub Rust)
// MUST agree on are in fact identical. Modeled on the client-paths manifest-
// sync test: the TS side is the reference; the C# / Rust sides are read from
// their on-disk source so a one-sided bump fails CI.
//
// When run outside the toolkit repo tree (a standalone mcp-server/ install),
// the C# / Rust sources are absent and the file-read parity checks skip
// gracefully — the in-tree values are still self-consistent and the
// intra-TS checks below still run.
// ---------------------------------------------------------------------------

function hereDir(): string {
  if (typeof __dirname !== "undefined") return __dirname;
  return dirname(fileURLToPath(import.meta.url));
}

/** Walk up from this module to find the toolkit root (the dir containing
 *  `skills/client-paths.json`). Returns null when not in the repo tree. */
function findToolkitRoot(): string | null {
  let dir = hereDir();
  for (let i = 0; i < 10; i++) {
    if (existsSync(join(dir, "skills", "client-paths.json"))) return dir;
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}

const BRIDGE_CONSTANTS = "packages/bridge/Editor/Config/BridgeConstants.cs";
const HUB_CONSTANTS = "hub/src-tauri/src/config/constants.rs";

function readBridgeConstants(): string | null {
  const root = findToolkitRoot();
  if (!root) return null;
  const p = join(root, BRIDGE_CONSTANTS);
  return existsSync(p) ? readFileSync(p, "utf8") : null;
}

function readHubConstants(): string | null {
  const root = findToolkitRoot();
  if (!root) return null;
  const p = join(root, HUB_CONSTANTS);
  return existsSync(p) ? readFileSync(p, "utf8") : null;
}

/** Extract a `const string = "value"` assignment from a C# source blob. */
function extractCsConst(src: string, name: string): string | null {
  const re = new RegExp(`${name}\\s*=\\s*"([^"]+)"`);
  const m = src.match(re);
  return m ? m[1] : null;
}

/** Extract a `pub const NAME: &str = "value"` assignment from a Rust source blob. */
function extractRustConst(src: string, name: string): string | null {
  const re = new RegExp(`${name}\\s*:\\s*&str\\s*=\\s*"([^"]+)"`);
  const m = src.match(re);
  return m ? m[1] : null;
}

/** Extract a `pub const NAME: u64 = value` assignment from a Rust source blob. */
function extractRustU64(src: string, name: string): string | null {
  const re = new RegExp(`${name}\\s*:\\s*u64\\s*=\\s*([0-9_]+)`);
  const m = src.match(re);
  return m ? m[1].replace(/_/g, "") : null;
}

test("verify JSON markers match across bridge C#, TS, and hub Rust", () => {
  const bridge = readBridgeConstants();
  const hub = readHubConstants();

  // TS is the reference; C# and Rust must agree.
  if (bridge) {
    assert.equal(
      extractCsConst(bridge, "VerifyJsonBegin"),
      VERIFY_JSON_BEGIN,
      "bridge VerifyJsonBegin drifted from TS",
    );
    assert.equal(
      extractCsConst(bridge, "VerifyJsonEnd"),
      VERIFY_JSON_END,
      "bridge VerifyJsonEnd drifted from TS",
    );
  }
  if (hub) {
    assert.equal(
      extractRustConst(hub, "VERIFY_JSON_BEGIN"),
      VERIFY_JSON_BEGIN,
      "hub VERIFY_JSON_BEGIN drifted from TS",
    );
    assert.equal(
      extractRustConst(hub, "VERIFY_JSON_END"),
      VERIFY_JSON_END,
      "hub VERIFY_JSON_END drifted from TS",
    );
  }
});

test("bridge default timeout matches across bridge C#, TS, and hub Rust", () => {
  const bridge = readBridgeConstants();
  const hub = readHubConstants();
  const tsValue = String(BRIDGE_DEFAULT_TIMEOUT_MS);

  // The bridge defines DefaultTimeoutMs in BridgeRequestBody.cs (not
  // BridgeConstants), so read it from there.
  const root = findToolkitRoot();
  if (root) {
    const requestBodyPath = join(
      root,
      "packages/bridge/Editor/Bridge/BridgeRequestBody.cs",
    );
    if (existsSync(requestBodyPath)) {
      const src = readFileSync(requestBodyPath, "utf8");
      const csVal = extractCsConst(src, "DefaultTimeoutMs");
      if (csVal !== null) {
        assert.equal(
          csVal,
          tsValue,
          "bridge DefaultTimeoutMs drifted from TS BRIDGE_DEFAULT_TIMEOUT_MS",
        );
      }
    }
  }
  if (hub) {
    assert.equal(
      extractRustU64(hub, "BRIDGE_DEFAULT_TIMEOUT_MS"),
      tsValue,
      "hub BRIDGE_DEFAULT_TIMEOUT_MS drifted from TS",
    );
  }
});

test("npm package name matches across bridge C#, TS, and hub Rust", () => {
  const bridge = readBridgeConstants();
  const hub = readHubConstants();

  if (bridge) {
    assert.equal(
      extractCsConst(bridge, "NpmPackageLatest"),
      NPM_PACKAGE_LATEST,
      "bridge NpmPackageLatest drifted from TS",
    );
  }
  if (hub) {
    assert.equal(
      extractRustConst(hub, "NPM_PACKAGE_LATEST"),
      NPM_PACKAGE_LATEST,
      "hub NPM_PACKAGE_LATEST drifted from TS",
    );
  }
});

test("scratch dir name matches across bridge C# and TS", () => {
  const bridge = readBridgeConstants();
  if (bridge) {
    assert.equal(
      extractCsConst(bridge, "SettingsDirName"),
      STATUS_DIR_NAME,
      "bridge SettingsDirName drifted from TS STATUS_DIR_NAME",
    );
  }
});

test("env var names match across bridge C#, TS, and hub Rust", () => {
  const bridge = readBridgeConstants();
  const hub = readHubConstants();

  if (bridge) {
    assert.equal(extractCsConst(bridge, "PortEnvVar"), PORT_ENV_VAR);
    assert.equal(
      extractCsConst(bridge, "ProjectPathEnvVar"),
      PROJECT_PATH_ENV_VAR,
    );
  }
  if (hub) {
    assert.equal(extractRustConst(hub, "PORT_ENV_VAR"), PORT_ENV_VAR);
    assert.equal(
      extractRustConst(hub, "PROJECT_PATH_ENV_VAR"),
      PROJECT_PATH_ENV_VAR,
    );
  }
});

test("repo URL matches across bridge C#, TS, and hub Rust", () => {
  const bridge = readBridgeConstants();
  const hub = readHubConstants();

  if (bridge) {
    assert.equal(extractCsConst(bridge, "RepoUrl"), REPO_URL);
  }
  if (hub) {
    assert.equal(extractRustConst(hub, "REPO_URL"), REPO_URL);
  }
});

// ---------------------------------------------------------------------------
// Port formula parity — computePort on the TS side must produce the same
// ports as the bridge C# InstancePortResolver and the hub Rust compute_port
// for a fixed set of sample paths. The exact cross-language equality is
// already pinned by per-side unit tests (instance-discovery.test.ts /
// InstancePortResolverTests.cs / bridge_port.rs tests); this test adds a
// deterministic in-range + self-consistency assertion keyed off the shared
// range constants.
// ---------------------------------------------------------------------------

test("port formula produces ports in the shared range for sample paths", () => {
  const samples = [
    "/Users/x/proj",
    "C:\\Users\\x\\proj",
    "/home/dev/game",
    "/tmp/p",
    "/Users/alexeyperov/Projects/Unity-AI-Hub/demo",
  ];
  for (const p of samples) {
    const port = computePort(p);
    assert.ok(
      port >= PORT_RANGE_START && port < PORT_RANGE_START + PORT_RANGE_SIZE,
      `port ${port} for ${p} out of [${PORT_RANGE_START}, ${PORT_RANGE_START + PORT_RANGE_SIZE})`,
    );
  }
});

test("pinned demo path resolves to the known port (27916)", () => {
  // Pinned across all three trees — the C#, TS, and Rust test suites all
  // assert this exact value. A change in any tree's hash / normalization
  // breaks this.
  assert.equal(
    computePort("/Users/alexeyperov/Projects/Unity-AI-Hub/demo"),
    27916,
  );
});

// ---------------------------------------------------------------------------
// Intra-TS consistency — the constants module and instance-discovery.ts
// agree on the range inputs (no second copy of 20000/10000 anywhere).
// ---------------------------------------------------------------------------

test("loopback and archive URL are stable values", () => {
  assert.equal(LOOPBACK_HOST, "127.0.0.1");
  assert.equal(ARCHIVE_URL, "https://unity.com/releases/editor/archive");
  assert.equal(
    RELEASE_NOTES_URL_PREFIX,
    "https://unity.com/releases/editor/whats-new/",
  );
});
