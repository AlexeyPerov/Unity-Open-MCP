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

test("EMBEDDED_DOMAINS lists all shipped domains with stable tool-group ids", () => {
  const domains = EMBEDDED_DOMAINS.map((d) => d.domain);
  assert.deepEqual(
    [...domains].sort(),
    [
      "animation",
      "inputsystem",
      "navigation",
      "particle_system",
      "probuilder",
      "splines",
    ],
  );
  for (const d of EMBEDDED_DOMAINS) {
    assert.ok(d.displayName.length > 0);
    assert.ok(d.description.length > 0);
    assert.ok(d.toolIds.length > 0, `${d.domain} must list its tool ids`);
  }
});

test("installableEmbeddedDomains excludes built-in module domains", () => {
  const installable = installableEmbeddedDomains();
  // Nav + Input + ProBuilder + Splines are real UPM packages.
  assert.equal(installable.length, 4);
  const upmIds = installable.map((d) => d.upmDependency).sort();
  assert.deepEqual(upmIds, [
    "com.unity.ai.navigation",
    "com.unity.inputsystem",
    "com.unity.probuilder",
    "com.unity.splines",
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
// Legacy extension-pack catalog (M18 Plan 5 — community / planned only).
// ---------------------------------------------------------------------------

test("EXTENSION_PACKS is non-empty and unique by id", () => {
  assert.ok(EXTENSION_PACKS.length > 0);
  const ids = EXTENSION_PACKS.map((p) => p.id);
  assert.equal(new Set(ids).size, ids.length);
});

test("EXTENSION_PACKS does not double-list shipped embedded domains", () => {
  // The five shipped first-party domains are embedded in the bridge
  // (EMBEDDED_DOMAINS) and MUST NOT also appear in the legacy catalog —
  // that would double-describe the surface and risk duplicate tool
  // registration if a legacy pack were still installed (M18 Plan 5/6).
  const shippedDomains = new Set(
    EMBEDDED_DOMAINS.map((d) => d.domain),
  );
  for (const p of EXTENSION_PACKS) {
    assert.ok(
      !shippedDomains.has(p.domain),
      `${p.domain} is embedded — must not be in EXTENSION_PACKS`,
    );
  }
});

test("EXTENSION_PACKS advertises the planned placeholders", () => {
  const domains = EXTENSION_PACKS.map((p) => p.domain).sort();
  // Splines graduated into EMBEDDED_DOMAINS in M18 Plan 7; Terrain shipped as
  // an ungated embedded domain in M20 Plan 4. Tilemap remains the sole
  // planned placeholder.
  assert.deepEqual(domains, ["tilemap"]);
  // All current entries are planned (no shipped community pack yet).
  for (const p of EXTENSION_PACKS) {
    assert.equal(p.shipped, false, `${p.domain} should be a planned placeholder`);
  }
});

test("shippedPacks() is empty until a real community pack lands", () => {
  // No shipped first-party or community pack in this catalog after M18
  // Plan 5; shipped domains live in EMBEDDED_DOMAINS.
  assert.equal(shippedPacks().length, 0);
});

test("findPack returns the planned pack and undefined for unknown ids", () => {
  const tilemap = findPack("com.alexeyperov.unity-open-mcp-ext-tilemap");
  assert.ok(tilemap, "tilemap planned pack should be in the catalog");
  assert.equal(tilemap?.shipped, false);
  // Splines graduated into EMBEDDED_DOMAINS in M18 Plan 7; Terrain shipped as
  // an ungated embedded domain in M20 Plan 4 — both must no longer appear as
  // planned packs.
  assert.equal(findPack("com.alexeyperov.unity-open-mcp-ext-splines"), undefined);
  assert.equal(findPack("com.alexeyperov.unity-open-mcp-ext-terrain"), undefined);
  assert.equal(findPack("com.alexeyperov.unity-open-mcp-ext-navigation"), undefined);
  assert.equal(findPack("com.alexeyperov.no-such-pack"), undefined);
});

test("localPackageEntry produces a file: URL relative to Packages", () => {
  const tilemap = findPack("com.alexeyperov.unity-open-mcp-ext-tilemap")!;
  assert.equal(
    localPackageEntry(tilemap),
    "file:../../packages/extensions/tilemap",
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
