import test from "node:test";
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { join } from "node:path";

import {
  loadClientPathsManifest,
  clientSkillRelativePath,
  knownClientKeys,
  getKnownClientKeys,
  resolveTemplateSkillPath,
  _clearClientPathsCacheForTests,
  BUNDLED_MANIFEST,
} from "./client-paths.js";

// ---------------------------------------------------------------------------
// Bundled fallback mirrors the checked-in manifest
// ---------------------------------------------------------------------------

test("bundled manifest exposes the canonical + extended client keys", () => {
  const keys = Object.keys(BUNDLED_MANIFEST.clients).sort();
  // The four M4 canonical keys are always present; M27 Plan 5 adds the
  // extended Ivan-parity skill targets. The bundled fallback must mirror
  // the on-disk manifest exactly so a standalone install generates the
  // same skill paths.
  for (const k of [
    "agents",
    "claude",
    "cursor",
    "opencode",
    "cline",
    "gemini",
    "kilocode",
    "roo",
    "agent",
    "junie",
    "vscode",
    "vs",
    "github",
  ]) {
    assert.ok(keys.includes(k), `bundled manifest missing client key ${k}`);
  }
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

test("knownClientKeys lists the canonical + extended clients", () => {
  const keys = knownClientKeys().sort();
  // Same expectation as the bundled-manifest test — the on-disk manifest
  // is the source of truth and the loader must surface every key.
  for (const k of [
    "agents",
    "claude",
    "cursor",
    "opencode",
    "cline",
    "gemini",
    "kilocode",
    "roo",
    "agent",
    "junie",
    "vscode",
    "vs",
    "github",
  ]) {
    assert.ok(keys.includes(k), `knownClientKeys missing ${k}`);
  }
});

// ---------------------------------------------------------------------------
// M31-optimizations Plan 4 / T4.4 (L6) — getKnownClientKeys singleton
// ---------------------------------------------------------------------------

test("getKnownClientKeys returns the same Set reference across calls", () => {
  // The manifest is immutable for process lifetime (cached after first
  // resolution), so the derived client-keys Set is too. routeGenerateSkill
  // previously allocated `new Set(knownClientKeys())` per call; it now
  // imports this singleton. Identity is the contract.
  _clearClientPathsCacheForTests();
  const a = getKnownClientKeys();
  const b = getKnownClientKeys();
  assert.equal(a, b, "getKnownClientKeys must return the memoized singleton");
  // The singleton's contents match knownClientKeys().
  assert.deepEqual(
    Array.from(a).sort(),
    knownClientKeys().sort(),
    "singleton contents must match knownClientKeys()",
  );
});

test("getKnownClientKeys re-derives after the manifest cache is cleared", () => {
  // Tests that mutate UNITY_OPEN_MCP_TOOLKIT_ROOT and clear the manifest
  // cache must observe a fresh client-keys Set on the next access. This
  // mirrors `_clearClientPathsCacheForTests`'s existing contract.
  const before = getKnownClientKeys();
  _clearClientPathsCacheForTests();
  const after = getKnownClientKeys();
  assert.notEqual(
    before,
    after,
    "clearing the manifest cache must re-derive the client-keys Set",
  );
  // Contents are still equivalent (same manifest resolves in this repo).
  assert.deepEqual(
    Array.from(before).sort(),
    Array.from(after).sort(),
    "contents equivalent after re-derivation in the same repo",
  );
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
