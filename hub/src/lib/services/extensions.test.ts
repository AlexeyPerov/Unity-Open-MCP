/**
 * Tests for the extension pack + embedded domain catalogs.
 *
 * Run with:
 *   node --test --experimental-strip-types --no-warnings src/lib/services/extensions.test.ts
 */
import { test } from "node:test";
import assert from "node:assert/strict";

import {
  EMBEDDED_DOMAINS,
  EXTENSION_PACKS,
  buildEmbeddedDomainInstallRows,
  builtinEmbeddedDomains,
  findEmbeddedDomainByUpmId,
  findPack,
  installableEmbeddedDomains,
  localPackageEntry,
  shippedPacks,
} from "./extensions.ts";

// ---------------------------------------------------------------------------
// Embedded domains (M18 Plan 4 — wizard Unity-dep toggles).
// ---------------------------------------------------------------------------

test("EMBEDDED_DOMAINS lists all 5 shipped domains with stable tool-group ids", () => {
  const domains = EMBEDDED_DOMAINS.map((d) => d.domain);
  assert.deepEqual(
    [...domains].sort(),
    ["animation", "inputsystem", "navigation", "particle_system", "probuilder"],
  );
  for (const d of EMBEDDED_DOMAINS) {
    assert.ok(d.displayName.length > 0);
    assert.ok(d.description.length > 0);
    assert.ok(d.toolIds.length > 0, `${d.domain} must list its tool ids`);
  }
});

test("installableEmbeddedDomains excludes built-in module domains", () => {
  const installable = installableEmbeddedDomains();
  // Nav + Input + ProBuilder are real UPM packages.
  assert.equal(installable.length, 3);
  const upmIds = installable.map((d) => d.upmDependency).sort();
  assert.deepEqual(upmIds, [
    "com.unity.ai.navigation",
    "com.unity.inputsystem",
    "com.unity.probuilder",
  ]);
  for (const d of installable) {
    assert.ok(!d.builtin);
    assert.ok(d.upmDependency.length > 0);
    assert.ok(d.defaultVersion.length > 0);
  }
});

test("builtinEmbeddedDomains surfaces Particle System + Animation as always-on", () => {
  const builtin = builtinEmbeddedDomains();
  assert.equal(builtin.length, 2);
  const domains = builtin.map((d) => d.domain).sort();
  assert.deepEqual(domains, ["animation", "particle_system"]);
  for (const d of builtin) {
    assert.equal(d.upmDependency, "");
    assert.equal(d.defaultVersion, "");
  }
});

test("findEmbeddedDomainByUpmId returns the matching domain", () => {
  const nav = findEmbeddedDomainByUpmId("com.unity.ai.navigation");
  assert.ok(nav);
  assert.equal(nav?.domain, "navigation");
  assert.equal(nav?.defaultVersion, "2.0.0");
  assert.equal(findEmbeddedDomainByUpmId("not.a.real.package"), undefined);
});

test("navigation embedded domain lists all 11 tools", () => {
  const nav = findEmbeddedDomainByUpmId("com.unity.ai.navigation");
  assert.ok(nav);
  assert.equal(nav?.toolIds.length, 11);
});

// ---------------------------------------------------------------------------
// buildEmbeddedDomainInstallRows (M18 Plan 4 T18.4.2 — Hub read-only panel).
// ---------------------------------------------------------------------------

test("buildEmbeddedDomainInstallRows renders one row per catalog domain", () => {
  const rows = buildEmbeddedDomainInstallRows([]);
  assert.equal(rows.length, EMBEDDED_DOMAINS.length);
  // Order matches the static catalog.
  assert.deepEqual(
    rows.map((r) => r.domain),
    EMBEDDED_DOMAINS.map((d) => d.domain),
  );
});

test("buildEmbeddedDomainInstallRows marks every installable dep missing on empty snapshot", () => {
  const rows = buildEmbeddedDomainInstallRows([]);
  for (const r of rows) {
    if (r.builtin) {
      assert.equal(r.installed, true, `${r.domain} should be always-on`);
      assert.equal(r.reference, null);
    } else {
      assert.equal(r.installed, false, `${r.domain} should be missing`);
      assert.equal(r.reference, null);
      assert.ok(r.upmDependency.length > 0);
    }
  }
});

test("buildEmbeddedDomainInstallRows joins the snapshot by UPM id", () => {
  const rows = buildEmbeddedDomainInstallRows([
    { id: "com.unity.ai.navigation", installed: true, reference: "2.0.0" },
    { id: "com.unity.probuilder", installed: true, reference: "file:../../pb" },
    { id: "com.unity.inputsystem", installed: false, reference: null },
  ]);
  const byDomain = new Map(rows.map((r) => [r.domain, r]));
  assert.equal(byDomain.get("navigation")?.installed, true);
  assert.equal(byDomain.get("navigation")?.reference, "2.0.0");
  assert.equal(byDomain.get("probuilder")?.installed, true);
  assert.equal(byDomain.get("probuilder")?.reference, "file:../../pb");
  assert.equal(byDomain.get("inputsystem")?.installed, false);
  assert.equal(byDomain.get("inputsystem")?.reference, null);
  // Built-in module domains stay always-on regardless of snapshot.
  assert.equal(byDomain.get("particle_system")?.builtin, true);
  assert.equal(byDomain.get("particle_system")?.installed, true);
  assert.equal(byDomain.get("animation")?.builtin, true);
});

test("buildEmbeddedDomainInstallRows tolerates a missing snapshot", () => {
  const rows = buildEmbeddedDomainInstallRows(undefined);
  assert.equal(rows.length, EMBEDDED_DOMAINS.length);
  assert.ok(rows.filter((r) => !r.builtin).every((r) => r.installed === false));
});

// ---------------------------------------------------------------------------
// Legacy extension-pack catalog (still mirrored for the bridge UI).
// ---------------------------------------------------------------------------

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
