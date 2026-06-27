// Unit tests for the server/bridge version-compatibility check.
//
// The rule under test is the pre-1.0 convention: while the major version is 0,
// the MINOR digit is the breaking axis. So 0.4.x is incompatible with 0.5.x,
// while 0.5.0 and 0.5.1 are compatible (patch-only, advisory). We pass an
// explicit serverVersion into checkBridgeCompat so the tests are deterministic
// and do not depend on the real package.json.

import { test } from "node:test";
import assert from "node:assert/strict";
import { checkBridgeCompat, versionsDiffer } from "./compat.js";

test("equal versions → ok, no message", () => {
  const r = checkBridgeCompat("0.5.2", "0.5.2");
  assert.equal(r.ok, true);
  assert.equal(r.message, "");
  assert.equal(versionsDiffer("0.5.2", "0.5.2"), false);
});

test("patch-only difference (pre-1.0) → ok, advisory message", () => {
  const r = checkBridgeCompat("0.5.1", "0.5.2");
  assert.equal(r.ok, true);
  assert.match(r.message, /Compatible/);
  assert.match(r.message, /patch difference only/i);
  assert.equal(versionsDiffer("0.5.1", "0.5.2"), true);
});

test("minor difference (pre-1.0) → NOT ok, server newer names bridge as older", () => {
  const r = checkBridgeCompat("0.4.0", "0.5.0");
  assert.equal(r.ok, false);
  assert.match(r.message, /INCOMPATIBLE/i);
  // bridge is older (0.4) than server (0.5) → message should name the bridge.
  assert.match(r.message, /bridge is older/i);
  assert.match(r.message, /0\.4\.0.*0\.5\.0|0\.5\.0.*0\.4\.0/);
});

test("minor difference (pre-1.0) → NOT ok, bridge newer names server as older", () => {
  const r = checkBridgeCompat("0.6.0", "0.5.0");
  assert.equal(r.ok, false);
  assert.match(r.message, /server is older/i);
  // server older → fix is to upgrade the server via npm.
  assert.match(r.message, /npm i -g unity-open-mcp@0\.6\.0/);
});

test("post-1.0: same major different minor → ok (major is the breaking axis)", () => {
  const r = checkBridgeCompat("1.2.0", "1.5.0");
  assert.equal(r.ok, true);
  assert.match(r.message, /Compatible/);
});

test("post-1.0: different major → NOT ok", () => {
  const r = checkBridgeCompat("2.0.0", "1.5.0");
  assert.equal(r.ok, false);
  assert.match(r.message, /INCOMPATIBLE/i);
});

test("0.x server vs 1.0 bridge → NOT ok (major boundary crossed)", () => {
  const r = checkBridgeCompat("1.0.0", "0.9.0");
  assert.equal(r.ok, false);
});

test("unparseable bridge version → ok with advisory (forward-compat with old bridges)", () => {
  const r = checkBridgeCompat("unknown", "0.5.0");
  assert.equal(r.ok, true);
  assert.match(r.message, /could not compare/i);
});

test("unparseable server version → ok with advisory", () => {
  const r = checkBridgeCompat("0.5.0", "dev");
  assert.equal(r.ok, true);
  assert.match(r.message, /could not compare/i);
});
