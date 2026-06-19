/**
 * Tests for the extension pack catalog mirror.
 *
 * Run with:
 *   node --test --experimental-strip-types --no-warnings src/lib/services/extensions.test.ts
 */
import { test } from "node:test";
import assert from "node:assert/strict";

import {
  EXTENSION_PACKS,
  findPack,
  localPackageEntry,
  shippedPacks,
} from "./extensions.ts";

test("EXTENSION_PACKS is non-empty and unique by id", () => {
  assert.ok(EXTENSION_PACKS.length > 0);
  const ids = EXTENSION_PACKS.map((p) => p.id);
  assert.equal(new Set(ids).size, ids.length);
});

test("navigation pack is shipped and lists all 11 tools", () => {
  const nav = findPack("com.alexeyperov.unity-open-mcp-ext-navigation");
  assert.ok(nav, "navigation pack should be in the catalog");
  assert.equal(nav?.shipped, true);
  assert.equal(nav?.toolIds.length, 11);
  assert.equal(nav?.upmDependency, "com.unity.ai.navigation");
});

test("planned packs are not in shippedPacks()", () => {
  const shipped = shippedPacks();
  // navigation + inputsystem + probuilder + particlesystem + animation ship.
  assert.equal(
    shipped.length,
    5,
    "navigation + inputsystem + probuilder + particlesystem + animation ship",
  );
  const shippedDomains = shipped.map((p) => p.domain);
  assert.ok(shippedDomains.includes("navigation"));
  assert.ok(shippedDomains.includes("inputsystem"));
  assert.ok(shippedDomains.includes("probuilder"));
  assert.ok(shippedDomains.includes("particle_system"));
  assert.ok(shippedDomains.includes("animation"));
  for (const p of shipped) {
    assert.equal(p.shipped, true);
  }
});

test("findPack returns undefined for an unknown id", () => {
  assert.equal(findPack("com.alexeyperov.no-such-pack"), undefined);
});

test("localPackageEntry produces a file: URL relative to Packages", () => {
  const nav = findPack("com.alexeyperov.unity-open-mcp-ext-navigation")!;
  assert.equal(
    localPackageEntry(nav),
    "file:../../packages/extensions/navigation",
  );
});

test("every pack carries the minimum metadata", () => {
  for (const p of EXTENSION_PACKS) {
    assert.ok(p.id.startsWith("com.alexeyperov.unity-open-mcp-ext-"));
    assert.ok(p.domain.length > 0);
    assert.ok(p.displayName.length > 0);
    assert.ok(p.description.length > 0);
    assert.ok(p.localPath.startsWith("packages/extensions/"));
    assert.ok(p.skillPath.startsWith("skills/extensions/"));
  }
});
