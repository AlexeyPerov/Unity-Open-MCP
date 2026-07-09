import { test } from "node:test";
import assert from "node:assert/strict";

import {
  effectiveLaunchMode,
  resolveLaunchSourceMode,
  wireModeForSourceMode,
} from "./launch_mode.ts";

test("effectiveLaunchMode: npx is the default", () => {
  assert.equal(
    effectiveLaunchMode({
      mcpIndexOverride: "",
      useLocalCheckout: false,
      useGlobalInstall: false,
    }),
    "npx",
  );
});

test("effectiveLaunchMode: useGlobalInstall refines npx → global", () => {
  assert.equal(
    effectiveLaunchMode({
      mcpIndexOverride: "",
      useLocalCheckout: false,
      useGlobalInstall: true,
    }),
    "global",
  );
});

test("effectiveLaunchMode: useLocalCheckout → local (ignores global flag)", () => {
  assert.equal(
    effectiveLaunchMode({
      mcpIndexOverride: "",
      useLocalCheckout: true,
      useGlobalInstall: true,
    }),
    "local",
  );
});

test("effectiveLaunchMode: mcpIndexOverride always wins (localOverride)", () => {
  assert.equal(
    effectiveLaunchMode({
      mcpIndexOverride: "/opt/builds/unity-open-mcp/index.js",
      useLocalCheckout: true,
      useGlobalInstall: true,
    }),
    "localOverride",
  );
});

test("effectiveLaunchMode: whitespace-only override is ignored", () => {
  assert.equal(
    effectiveLaunchMode({
      mcpIndexOverride: "   ",
      useLocalCheckout: false,
      useGlobalInstall: false,
    }),
    "npx",
  );
});

test("effectiveLaunchMode precedence: override > local > global > npx", () => {
  // Each tier overrides the next-lower one.
  assert.equal(
    effectiveLaunchMode({
      mcpIndexOverride: "x",
      useLocalCheckout: true,
      useGlobalInstall: true,
    }),
    "localOverride",
  );
  assert.equal(
    effectiveLaunchMode({
      mcpIndexOverride: "",
      useLocalCheckout: true,
      useGlobalInstall: true,
    }),
    "local",
  );
  assert.equal(
    effectiveLaunchMode({
      mcpIndexOverride: "",
      useLocalCheckout: false,
      useGlobalInstall: true,
    }),
    "global",
  );
  assert.equal(
    effectiveLaunchMode({
      mcpIndexOverride: "",
      useLocalCheckout: false,
      useGlobalInstall: false,
    }),
    "npx",
  );
});

// ---- resolveLaunchSourceMode (Plan 2 exclusive-mode model) ----

test("resolveLaunchSourceMode: npx is the default", () => {
  assert.equal(
    resolveLaunchSourceMode({
      mcpIndexOverride: "",
      useLocalCheckout: false,
      useGlobalInstall: false,
    }),
    "npx",
  );
});

test("resolveLaunchSourceMode: global mode", () => {
  assert.equal(
    resolveLaunchSourceMode({
      mcpIndexOverride: "",
      useLocalCheckout: false,
      useGlobalInstall: true,
    }),
    "global",
  );
});

test("resolveLaunchSourceMode: local mode", () => {
  assert.equal(
    resolveLaunchSourceMode({
      mcpIndexOverride: "",
      useLocalCheckout: true,
      useGlobalInstall: false,
    }),
    "local",
  );
});

test("resolveLaunchSourceMode: override maps to custom mode", () => {
  assert.equal(
    resolveLaunchSourceMode({
      mcpIndexOverride: "/opt/builds/unity-open-mcp/index.js",
      useLocalCheckout: false,
      useGlobalInstall: false,
    }),
    "custom",
  );
});

test("resolveLaunchSourceMode: whitespace-only override is npx, not custom", () => {
  assert.equal(
    resolveLaunchSourceMode({
      mcpIndexOverride: "   ",
      useLocalCheckout: false,
      useGlobalInstall: false,
    }),
    "npx",
  );
});

test("resolveLaunchSourceMode: precedence override > local > global > npx", () => {
  assert.equal(
    resolveLaunchSourceMode({
      mcpIndexOverride: "x",
      useLocalCheckout: true,
      useGlobalInstall: true,
    }),
    "custom",
  );
  assert.equal(
    resolveLaunchSourceMode({
      mcpIndexOverride: "",
      useLocalCheckout: true,
      useGlobalInstall: true,
    }),
    "local",
  );
  assert.equal(
    resolveLaunchSourceMode({
      mcpIndexOverride: "",
      useLocalCheckout: false,
      useGlobalInstall: true,
    }),
    "global",
  );
});

// ---- wireModeForSourceMode ----

test("wireModeForSourceMode: maps each source mode to its wire mode", () => {
  assert.equal(wireModeForSourceMode("npx"), "npx");
  assert.equal(wireModeForSourceMode("global"), "global");
  assert.equal(wireModeForSourceMode("local"), "local");
  assert.equal(wireModeForSourceMode("custom"), "localOverride");
});

test("wireModeForSourceMode + resolveLaunchSourceMode round-trips through effectiveLaunchMode", () => {
  // The deprecated effectiveLaunchMode must agree with the new two-step
  // model for every legacy-field combination.
  const cases = [
    { mcpIndexOverride: "", useLocalCheckout: false, useGlobalInstall: false },
    { mcpIndexOverride: "", useLocalCheckout: false, useGlobalInstall: true },
    { mcpIndexOverride: "", useLocalCheckout: true, useGlobalInstall: false },
    { mcpIndexOverride: "", useLocalCheckout: true, useGlobalInstall: true },
    { mcpIndexOverride: "x", useLocalCheckout: false, useGlobalInstall: false },
    { mcpIndexOverride: "x", useLocalCheckout: true, useGlobalInstall: true },
  ];
  for (const input of cases) {
    assert.equal(
      effectiveLaunchMode(input),
      wireModeForSourceMode(resolveLaunchSourceMode(input)),
    );
  }
});
