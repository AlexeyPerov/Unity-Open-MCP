/**
 * Node:test harness for the M4 AI-toolkit env-var builders.
 * Run with: `node --test --experimental-strip-types --no-warnings src/lib/services/ai_toolkit.test.ts`
 * (Node 22+ has built-in TypeScript stripping; Hub itself requires
 *  Node 18+, so the harness uses Node 22 when available.)
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  buildCursorMcpEntry,
  buildMcpEnv,
  buildOpenCodeMcpEntry,
  MCP_SERVER_KEY,
  mcpClientConfigTarget,
  type McpClientId,
} from "./ai_toolkit.ts";

// The wizard resolves the bridge port via the `resolve_bridge_port` Tauri
// command before building an entry; these tests pass an explicit port to
// exercise the env-builder directly.
const PORT = "27916";

test("buildMcpEnv always includes required vars", () => {
  const env = buildMcpEnv({ unityProjectPath: "/games/MyGame", bridgePort: PORT });
  assert.equal(env.UNITY_PROJECT_PATH, "/games/MyGame");
  assert.equal(env.UNITY_OPEN_MCP_BRIDGE_PORT, PORT);
  assert.equal(env.UNITY_PATH, undefined);
});

test("buildMcpEnv honors custom bridge port", () => {
  const env = buildMcpEnv({
    unityProjectPath: "/games/MyGame",
    bridgePort: "19199",
  });
  assert.equal(env.UNITY_OPEN_MCP_BRIDGE_PORT, "19199");
});

test("buildMcpEnv omits UNITY_PATH when blank", () => {
  const env = buildMcpEnv({
    unityProjectPath: "/games/MyGame",
    bridgePort: PORT,
    unityPath: "   ",
  });
  assert.equal(env.UNITY_PATH, undefined);
});

test("buildMcpEnv trims and includes UNITY_PATH when set", () => {
  const env = buildMcpEnv({
    unityProjectPath: "/games/MyGame",
    bridgePort: PORT,
    unityPath: "  /Applications/Unity  ",
  });
  assert.equal(env.UNITY_PATH, "/Applications/Unity");
});

test("buildCursorMcpEntry uses command+args+env envelope", () => {
  const entry = buildCursorMcpEntry("/repos/uai/mcp-server/dist/index.js", {
    unityProjectPath: "/games/MyGame",
    bridgePort: PORT,
  });
  assert.equal(entry.command, "node");
  assert.deepEqual(entry.args, ["/repos/uai/mcp-server/dist/index.js"]);
  assert.equal(entry.env.UNITY_PROJECT_PATH, "/games/MyGame");
  assert.equal(entry.env.UNITY_OPEN_MCP_BRIDGE_PORT, PORT);
});

test("buildOpenCodeMcpEntry uses command-array+environment envelope", () => {
  const entry = buildOpenCodeMcpEntry(
    "/repos/uai/mcp-server/dist/index.js",
    { unityProjectPath: "/games/MyGame", bridgePort: PORT }
  );
  assert.equal(entry.type, "local");
  assert.equal(entry.enabled, true);
  assert.deepEqual(entry.command, [
    "node",
    "/repos/uai/mcp-server/dist/index.js",
  ]);
  // OpenCode env field is `environment`, not `env`.
  assert.equal("env" in entry, false);
  assert.equal(entry.environment.UNITY_PROJECT_PATH, "/games/MyGame");
});

test("mcpClientConfigTarget resolves cursor to ~/.cursor/mcp.json", () => {
  const t = mcpClientConfigTarget("cursor", "/home/dev");
  assert.equal(t.path, "/home/dev/.cursor/mcp.json");
  assert.equal(t.scope, "global");
  assert.equal(t.mergeKey, "mcpServers.unity-open-mcp");
});

test("mcpClientConfigTarget resolves opencode-global to ~/.config/opencode/opencode.json", () => {
  const t = mcpClientConfigTarget("opencode-global", "/home/dev");
  assert.equal(t.path, "/home/dev/.config/opencode/opencode.json");
  assert.equal(t.mergeKey, "mcp.unity-open-mcp");
});

test("mcpClientConfigTarget returns null path for claude-code (CLI-only)", () => {
  const t = mcpClientConfigTarget("claude-code", "/home/dev");
  assert.equal(t.path, null);
  assert.equal(t.scope, "none");
});

test("mcpClientConfigTarget returns null path for manual (clipboard-only)", () => {
  const t = mcpClientConfigTarget("manual", "/home/dev");
  assert.equal(t.path, null);
  assert.equal(t.scope, "none");
});

test("MCP_SERVER_KEY is unity-open-mcp (matches Rust AiToolkitSettings convention)", () => {
  assert.equal(MCP_SERVER_KEY, "unity-open-mcp");
});

test("mcpClientConfigTarget covers all McpClientId values", () => {
  // Every variant in the McpClientId union must resolve without falling
  // through the switch (TypeScript exhaustiveness helps, but the runtime
  // guard catches a missing case when the union grows).
  const ids: McpClientId[] = [
    "cursor",
    "claude-desktop",
    "claude-code",
    "opencode-global",
    "opencode-project",
    "zcode-global",
    "zcode-project",
    "manual",
    "cline",
    "codex",
    "gemini",
    "github-copilot-cli",
    "kilo-code",
    "rider",
    "unity-ai",
    "vscode-copilot",
    "vs-copilot",
    "zoocode",
    "antigravity",
    "custom",
  ];
  for (const id of ids) {
    const t = mcpClientConfigTarget(id, "/home/dev");
    // All targets must have a string mergeKey, even when path is null.
    assert.equal(typeof t.mergeKey, "string");
  }
});

test("mcpClientConfigTarget resolves codex to .codex/config.toml (project, TOML)", () => {
  const t = mcpClientConfigTarget("codex", "/home/dev");
  assert.equal(t.path, ".codex/config.toml");
  assert.equal(t.scope, "project");
  assert.equal(t.mergeKey, "mcp_servers.unity-open-mcp");
});

test("mcpClientConfigTarget resolves vscode-copilot to .vscode/mcp.json with servers key", () => {
  const t = mcpClientConfigTarget("vscode-copilot", "/home/dev");
  assert.equal(t.path, ".vscode/mcp.json");
  assert.equal(t.scope, "project");
  // VS Code Copilot uses `servers`, NOT `mcpServers`.
  assert.equal(t.mergeKey, "servers.unity-open-mcp");
});

test("mcpClientConfigTarget resolves antigravity to global gemini/antigravity path", () => {
  const t = mcpClientConfigTarget("antigravity", "/home/dev");
  assert.equal(t.path, "/home/dev/.gemini/antigravity/mcp_config.json");
  assert.equal(t.scope, "global");
});

test("mcpClientConfigTarget resolves cline to global scope with mcpServers key", () => {
  const t = mcpClientConfigTarget("cline", "/home/dev");
  // OS-specific path resolved in Step 4; the merge key is the Cursor family.
  assert.equal(t.path, null);
  assert.equal(t.scope, "global");
  assert.equal(t.mergeKey, "mcpServers.unity-open-mcp");
});

test("custom returns null path like manual (clipboard-only)", () => {
  const t = mcpClientConfigTarget("custom", "/home/dev");
  assert.equal(t.path, null);
  assert.equal(t.scope, "none");
});
