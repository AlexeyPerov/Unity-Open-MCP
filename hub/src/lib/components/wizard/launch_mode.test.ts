import { test } from "node:test";
import assert from "node:assert/strict";

import { resolveLaunchSourceMode, wireModeForSourceMode } from "./launch_mode.ts";

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

test("wireModeForSourceMode + resolveLaunchSourceMode preserves legacy precedence mapping", () => {
  // Legacy precedence was override > local > global > npx.
  const cases = [
    {
      input: { mcpIndexOverride: "", useLocalCheckout: false, useGlobalInstall: false },
      expected: "npx",
    },
    {
      input: { mcpIndexOverride: "", useLocalCheckout: false, useGlobalInstall: true },
      expected: "global",
    },
    {
      input: { mcpIndexOverride: "", useLocalCheckout: true, useGlobalInstall: false },
      expected: "local",
    },
    {
      input: { mcpIndexOverride: "", useLocalCheckout: true, useGlobalInstall: true },
      expected: "local",
    },
    {
      input: { mcpIndexOverride: "x", useLocalCheckout: false, useGlobalInstall: false },
      expected: "localOverride",
    },
    {
      input: { mcpIndexOverride: "x", useLocalCheckout: true, useGlobalInstall: true },
      expected: "localOverride",
    },
  ];
  for (const { input, expected } of cases) {
    assert.equal(
      wireModeForSourceMode(resolveLaunchSourceMode(input)),
      expected,
    );
  }
});
