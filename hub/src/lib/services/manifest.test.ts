/**
 * Node:test harness for the M4 Plan 3 manifest UI helpers.
 * Run with: `node --test --experimental-strip-types --no-warnings src/lib/services/manifest.test.ts`
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  changeKindLabel,
  changeKindTone,
  describeManifestError,
  formatChangeLine,
  formatDiffPreview,
  shortPackageName,
  summarizeChanges,
} from "./manifest.ts";
import type { ManifestError, PackageChange } from "./config.ts";

test("changeKindLabel covers every kind", () => {
  assert.equal(changeKindLabel("add"), "will add");
  assert.equal(changeKindLabel("upgrade"), "will upgrade");
  assert.equal(changeKindLabel("unchanged"), "already installed");
});

test("changeKindTone maps upgrades to warn", () => {
  assert.equal(changeKindTone("add"), "ok");
  assert.equal(changeKindTone("upgrade"), "warn");
  assert.equal(changeKindTone("unchanged"), "muted");
});

test("shortPackageName strips the com.alexeyperov. prefix", () => {
  assert.equal(
    shortPackageName("com.alexeyperov.unity-open-mcp-bridge"),
    "unity-open-mcp-bridge"
  );
  assert.equal(shortPackageName("com.unity.ide.rider"), "rider");
  assert.equal(shortPackageName("plain"), "plain");
});

test("formatChangeLine for an add", () => {
  const line = formatChangeLine({
    id: "com.alexeyperov.unity-open-mcp-bridge",
    before: null,
    after: "file:../../packages/bridge",
    kind: "add",
  });
  assert.equal(line, "unity-open-mcp-bridge: add file:../../packages/bridge");
});

test("formatChangeLine for an upgrade", () => {
  const line = formatChangeLine({
    id: "com.alexeyperov.unity-open-mcp-bridge",
    before: "https://github.com/.../bridge#bridge-v0.9.0",
    after: "https://github.com/.../bridge#bridge-v1.0.0",
    kind: "upgrade",
  });
  assert.equal(
    line,
    "unity-open-mcp-bridge: upgrade https://github.com/.../bridge#bridge-v0.9.0 → https://github.com/.../bridge#bridge-v1.0.0"
  );
});

test("formatChangeLine for an unchanged", () => {
  const line = formatChangeLine({
    id: "com.alexeyperov.unity-open-mcp-bridge",
    before: "file:../../packages/bridge",
    after: "file:../../packages/bridge",
    kind: "unchanged",
  });
  assert.equal(
    line,
    "unity-open-mcp-bridge: unchanged (file:../../packages/bridge)"
  );
});

test("formatDiffPreview joins change lines with newlines", () => {
  const changes: PackageChange[] = [
    {
      id: "com.alexeyperov.unity-open-mcp-bridge",
      before: null,
      after: "file:../../packages/bridge",
      kind: "add",
    },
    {
      id: "com.alexeyperov.unity-open-mcp-verify",
      before: null,
      after: "file:../../packages/verify",
      kind: "add",
    },
  ];
  const out = formatDiffPreview(changes);
  assert.match(out, /^unity-open-mcp-bridge: add /);
  assert.match(out, /\nunity-open-mcp-verify: add /);
});

test("summarizeChanges counts adds, upgrades, and unchanged", () => {
  const summary = summarizeChanges([
    { id: "a", before: null, after: "x", kind: "add" },
    { id: "b", before: null, after: "x", kind: "add" },
    { id: "c", before: "1", after: "2", kind: "upgrade" },
    { id: "d", before: "1", after: "1", kind: "unchanged" },
  ]);
  assert.equal(summary, "2 added, 1 upgraded, 1 unchanged");
});

test("summarizeChanges empty list returns no changes", () => {
  assert.equal(summarizeChanges([]), "no changes");
});

test("describeManifestError handles each known kind", () => {
  assert.match(
    describeManifestError({
      kind: "invalidJson",
      message: "expected `,` at line 3",
    }),
    /Cannot parse Packages\/manifest\.json/
  );
  assert.match(
    describeManifestError({ kind: "notAUnityProject", message: "missing" }),
    /missing/
  );
  assert.match(
    describeManifestError({
      kind: "upgradeNotConfirmed",
      message: "differ",
    }),
    /revert the version pin/
  );
  assert.match(
    describeManifestError({ kind: "writeFailed", message: "denied" }),
    /Check folder permissions/
  );
  assert.match(
    describeManifestError({ kind: "weirdKind", message: "x" }),
    /weirdKind: x/
  );
});

test("describeManifestError preserves upgrade guidance wording", () => {
  const msg = describeManifestError({
    kind: "upgradeNotConfirmed",
    message: "Existing entry differs from proposed value",
  });
  assert.match(msg, /Existing entry differs/);
  assert.match(msg, /revert the version pin/);
});

test("ManifestError type accepts the wizard wire shape", () => {
  const err: ManifestError = { kind: "invalidJson", message: "boom" };
  assert.equal(err.kind, "invalidJson");
});
