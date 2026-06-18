import test from "node:test";
import assert from "node:assert/strict";

import {
  loadClientPathsManifest,
  clientSkillRelativePath,
  knownClientKeys,
  BUNDLED_MANIFEST,
} from "./client-paths.js";

// ---------------------------------------------------------------------------
// Bundled fallback mirrors the checked-in manifest
// ---------------------------------------------------------------------------

test("bundled manifest exposes the four canonical client keys", () => {
  const keys = Object.keys(BUNDLED_MANIFEST.clients).sort();
  assert.deepEqual(keys, ["agents", "claude", "cursor", "opencode"]);
});

test("bundled manifest maps ZCode to agents and Cursor to cursor", () => {
  assert.deepEqual(BUNDLED_MANIFEST.mcpClientMapping["zcode-global"], ["agents"]);
  assert.deepEqual(BUNDLED_MANIFEST.mcpClientMapping["zcode-project"], ["agents"]);
  assert.deepEqual(BUNDLED_MANIFEST.mcpClientMapping["cursor"], ["cursor"]);
  assert.deepEqual(BUNDLED_MANIFEST.mcpClientMapping["manual"].sort(), [
    "agents",
    "claude",
    "cursor",
    "opencode",
  ]);
});

// ---------------------------------------------------------------------------
// Loader + helpers contract
// ---------------------------------------------------------------------------

test("loadClientPathsManifest always returns a usable manifest (bundled fallback)", () => {
  // The loader walks up from this module looking for the manifest; in
  // the test environment it resolves the checked-in file. It must
  // never throw — the bundled fallback covers a missing manifest so
  // unity_open_mcp_generate_skill keeps working in a standalone install.
  const manifest = loadClientPathsManifest();
  assert.equal(manifest.skillId, "unity-open-mcp");
  assert.ok(manifest.clients.cursor);
  assert.ok(manifest.clients.claude);
});

test("clientSkillRelativePath returns the path for known keys", () => {
  assert.equal(
    clientSkillRelativePath("cursor"),
    ".cursor/skills/unity-open-mcp/SKILL.md",
  );
  assert.equal(
    clientSkillRelativePath("claude"),
    ".claude/skills/unity-open-mcp/SKILL.md",
  );
  assert.equal(
    clientSkillRelativePath("opencode"),
    ".opencode/skills/unity-open-mcp/SKILL.md",
  );
  assert.equal(
    clientSkillRelativePath("agents"),
    ".agents/skills/unity-open-mcp/SKILL.md",
  );
});

test("clientSkillRelativePath throws for an unknown client key", () => {
  assert.throws(
    () => clientSkillRelativePath("nope"),
    /Unknown skill client key "nope"/,
  );
});

test("knownClientKeys lists the canonical clients", () => {
  const keys = knownClientKeys().sort();
  assert.deepEqual(keys, ["agents", "claude", "cursor", "opencode"]);
});
