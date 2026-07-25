import { test } from "node:test";
import assert from "node:assert/strict";

import { MCP_CLIENT_OPTIONS, clientToWire } from "./constants.ts";

/**
 * H6 / H7 (round-2 review): the wire id `clientToWire` emits must
 * match the camelCase name the Rust `McpClientId` enum deserialises
 * from. Two of ~20 clients were silently misrouted — VS Code Copilot
 * was writing `.vs/mcp.json` (Visual Studio's path) and ZooCode was
 * rejected outright as "unknown variant".
 *
 * We cannot import the Rust enum here, so the expected wire ids are
 * spelled out from the `#[serde(rename_all = "camelCase")]` rule. Any
 * drift between this list and the enum surfaces as a failing test
 * before the wizard ships a broken client.
 */
const EXPECTED_WIRE: Record<string, string> = {
  cursor: "cursor",
  "claude-desktop": "claudeDesktop",
  "claude-code": "claudeCode",
  "opencode-global": "opencodeGlobal",
  "opencode-project": "opencodeProject",
  "zcode-global": "zcodeGlobal",
  "zcode-project": "zcodeProject",
  manual: "manual",
  cline: "cline",
  codex: "codex",
  gemini: "gemini",
  "github-copilot-cli": "githubCopilotCli",
  "kilo-code": "kiloCode",
  rider: "rider",
  "unity-ai": "unityAi",
  // H6: VS Code Copilot targets `.vscode/mcp.json` (NOT Visual Studio).
  "vscode-copilot": "vscodeCopilot",
  "vs-copilot": "vsCopilot",
  // H7: ZooCode deserialises from "zooCode", not "zoocode".
  zoocode: "zooCode",
  antigravity: "antigravity",
  custom: "custom",
};

test("clientToWire: every catalog entry has an expected wire id", () => {
  for (const opt of MCP_CLIENT_OPTIONS) {
    assert.ok(
      opt.id in EXPECTED_WIRE,
      `no expected wire id recorded for "${opt.id}" — add it to EXPECTED_WIRE`,
    );
    assert.equal(
      clientToWire(opt.id),
      EXPECTED_WIRE[opt.id],
      `clientToWire("${opt.id}") returned the wrong wire id`,
    );
  }
});

test("clientToWire: VS Code Copilot does not collide with Visual Studio", () => {
  // H6 regression guard: the two clients share a target path pattern
  // (`.vscode/` vs `.vs/`) but must produce distinct wire ids.
  assert.notEqual(
    clientToWire("vscode-copilot"),
    clientToWire("vs-copilot"),
  );
  assert.equal(clientToWire("vscode-copilot"), "vscodeCopilot");
  assert.equal(clientToWire("vs-copilot"), "vsCopilot");
});

test("clientToWire: ZooCode uses the serde camelCase name", () => {
  // H7 regression guard: the wire value must be "zooCode" (the name
  // Rust's `#[serde(rename_all = "camelCase")]` produces for the
  // `ZooCode` variant), not "zoocode".
  assert.equal(clientToWire("zoocode"), "zooCode");
});
