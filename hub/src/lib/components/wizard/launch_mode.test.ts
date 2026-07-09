import { test } from "node:test";
import assert from "node:assert/strict";

import { effectiveLaunchMode } from "./launch_mode.ts";

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
