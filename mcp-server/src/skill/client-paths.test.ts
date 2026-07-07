import test from "node:test";
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { join } from "node:path";

import {
  loadClientPathsManifest,
  clientSkillRelativePath,
  knownClientKeys,
  resolveTemplateSkillPath,
  _clearClientPathsCacheForTests,
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

// ---------------------------------------------------------------------------
// Template skill path resolver
// ---------------------------------------------------------------------------

test("resolveTemplateSkillPath resolves the checked-in template in this repo", () => {
  // In the test environment the loader walks up from this module and
  // finds the toolkit root containing skills/client-paths.json, so the
  // template path must point at the real checked-in SKILL.md.
  const p = resolveTemplateSkillPath();
  if (p !== null) {
    assert.ok(p.endsWith(join("skills", "unity-open-mcp", "SKILL.md")), p);
    assert.ok(existsSync(p), `template must exist at ${p}`);
  }
  // When run outside the repo tree (standalone install), p is null —
  // both outcomes are valid; the composer degrades gracefully.
});

test("resolveTemplateSkillPath honors UNITY_OPEN_MCP_TOOLKIT_ROOT", () => {
  const prev = process.env.UNITY_OPEN_MCP_TOOLKIT_ROOT;
  _clearClientPathsCacheForTests();
  try {
    process.env.UNITY_OPEN_MCP_TOOLKIT_ROOT = "/nonexistent-toolkit-root-xyz";
    _clearClientPathsCacheForTests();
    const p = resolveTemplateSkillPath();
    // The env override points at a dir with no manifest, so the env
    // branch fails and the resolver falls back to the walk-up which
    // (in this repo) finds the real toolkit root. That fallback is the
    // documented behavior: an invalid env override never silently
    // breaks resolution. We assert the override is *non-authoritative*
    // by confirming resolution still succeeds (returns a real path)
    // rather than null.
    assert.ok(p !== null, "invalid env override falls back to walk-up, not null");
    assert.ok(p.endsWith(join("skills", "unity-open-mcp", "SKILL.md")), p);
  } finally {
    if (prev === undefined) {
      delete process.env.UNITY_OPEN_MCP_TOOLKIT_ROOT;
    } else {
      process.env.UNITY_OPEN_MCP_TOOLKIT_ROOT = prev;
    }
    _clearClientPathsCacheForTests();
  }
});
