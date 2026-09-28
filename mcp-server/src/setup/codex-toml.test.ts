import { test } from "node:test";
import assert from "node:assert/strict";

import {
  CodexTomlError,
  mergeCodexServer,
  renderCodexServer,
  type CodexServerEntry,
} from "./codex-toml.js";

const KEY = "unity-open-mcp";

const ABSOLUTE: CodexServerEntry = {
  command: "npx",
  args: ["-y", "unity-open-mcp@1.4.0"],
  env: { UNITY_PROJECT_PATH: "/Users/dev/game/Client" },
  removeEnv: [],
};

const WRAPPER: CodexServerEntry = {
  command: "bash",
  args: ["scripts/mcp/unity-open-mcp.sh"],
  env: {},
  removeEnv: ["UNITY_PROJECT_PATH"],
};

/** A realistic file: comments, a remote server holding a token, tool tables. */
const OTHERS = [
  "# Codex settings",
  'model = "gpt-5"',
  "",
  "[mcp_servers.fal_ai]",
  'url = "https://mcp.fal.ai/mcp"',
  'http_headers = { Authorization = "Bearer secret-token" }  # keep me',
  "enabled = true",
  "",
  "[mcp_servers.fal_ai.tools.run_model]",
  'approval_mode = "prompt"',
  "",
].join("\n");

test("an empty file gets the fresh table block", () => {
  const out = mergeCodexServer("", KEY, ABSOLUTE);
  assert.equal(
    out,
    [
      "[mcp_servers.unity-open-mcp]",
      "enabled = true",
      'command = "npx"',
      'args = ["-y", "unity-open-mcp@1.4.0"]',
      "",
      "[mcp_servers.unity-open-mcp.env]",
      'UNITY_PROJECT_PATH = "/Users/dev/game/Client"',
      "",
    ].join("\n"),
  );
});

test("appending leaves every existing byte in place", () => {
  const out = mergeCodexServer(OTHERS, KEY, ABSOLUTE);
  assert.ok(out.startsWith(OTHERS), "the original file is an untouched prefix");
  assert.ok(out.endsWith(renderCodexServer(KEY, ABSOLUTE)));
});

test("a file without a trailing newline is separated from the new table", () => {
  const out = mergeCodexServer('model = "gpt-5"', KEY, WRAPPER);
  assert.ok(out.startsWith('model = "gpt-5"\n\n[mcp_servers.unity-open-mcp]\n'));
});

test("an absolute entry becomes the wrapper form; other tables are byte-identical", () => {
  const existing =
    OTHERS +
    [
      "[mcp_servers.unity-open-mcp]",
      "enabled = true",
      'command = "npx"',
      "args = [",
      '    "-y",',
      '    "unity-open-mcp@1.3.0",',
      "]",
      "startup_timeout_sec = 30",
      "",
      "[mcp_servers.unity-open-mcp.env]",
      'UNITY_PROJECT_PATH = "/Users/dev/game/Client"',
      'UNITY_PATH = "/Applications/Unity/Unity.app"',
      "",
    ].join("\n");
  const out = mergeCodexServer(existing, KEY, WRAPPER);
  assert.ok(out.startsWith(OTHERS));
  assert.equal(
    out.slice(OTHERS.length),
    [
      "[mcp_servers.unity-open-mcp]",
      "enabled = true",
      'command = "bash"',
      'args = ["scripts/mcp/unity-open-mcp.sh"]',
      "startup_timeout_sec = 30",
      "",
      "[mcp_servers.unity-open-mcp.env]",
      'UNITY_PATH = "/Applications/Unity/Unity.app"',
      "",
    ].join("\n"),
  );
});

test("merging twice is a no-op", () => {
  for (const entry of [ABSOLUTE, WRAPPER]) {
    const once = mergeCodexServer(OTHERS, KEY, entry);
    assert.equal(mergeCodexServer(once, KEY, entry), once);
  }
});

test("a replaced value keeps the key spelling and its trailing comment", () => {
  const existing = [
    "[mcp_servers.unity-open-mcp]",
    'command   =   "node"   # launcher',
    'args = ["-y", "unity-open-mcp@1.3.0"]',
    "",
  ].join("\n");
  const out = mergeCodexServer(existing, KEY, { ...ABSOLUTE, env: {} });
  assert.equal(
    out,
    [
      "[mcp_servers.unity-open-mcp]",
      'command   =   "npx"   # launcher',
      'args = ["-y", "unity-open-mcp@1.4.0"]',
      "",
    ].join("\n"),
  );
});

test("missing keys are inserted under the header and an env table is added before sub-tables", () => {
  const existing = [
    "[mcp_servers.unity-open-mcp]",
    "enabled = false",
    "",
    "[mcp_servers.unity-open-mcp.tools.scan_all]",
    'approval_mode = "prompt"',
    "",
  ].join("\n");
  const out = mergeCodexServer(existing, KEY, ABSOLUTE);
  assert.equal(
    out,
    [
      "[mcp_servers.unity-open-mcp]",
      'command = "npx"',
      'args = ["-y", "unity-open-mcp@1.4.0"]',
      "enabled = false",
      "",
      "[mcp_servers.unity-open-mcp.env]",
      'UNITY_PROJECT_PATH = "/Users/dev/game/Client"',
      "",
      "[mcp_servers.unity-open-mcp.tools.scan_all]",
      'approval_mode = "prompt"',
      "",
    ].join("\n"),
  );
});

test("an inline env table is edited in place", () => {
  const existing = [
    '[mcp_servers."unity-open-mcp"]',
    'command = "npx"',
    'args = ["-y", "unity-open-mcp@1.3.0"]',
    'env = { UNITY_PROJECT_PATH = "/old", KEEP = "yes" }',
    "",
  ].join("\n");
  const absolute = mergeCodexServer(existing, KEY, ABSOLUTE);
  assert.match(absolute, /env = \{ UNITY_PROJECT_PATH = "\/Users\/dev\/game\/Client", KEEP = "yes" \}/);
  const wrapper = mergeCodexServer(existing, KEY, WRAPPER);
  assert.match(wrapper, /env = \{ KEEP = "yes" \}/);
  assert.doesNotMatch(wrapper, /\[mcp_servers\.unity-open-mcp\.env\]/);
});

test("CRLF files stay CRLF", () => {
  const existing = OTHERS.split("\n").join("\r\n");
  const out = mergeCodexServer(existing, KEY, ABSOLUTE);
  assert.ok(out.startsWith(existing));
  assert.ok(!/[^\r]\n/.test(out), "every newline is CRLF");
  assert.equal(mergeCodexServer(out, KEY, ABSOLUTE), out);
});

test("Windows paths are escaped as TOML basic strings", () => {
  const out = mergeCodexServer("", KEY, {
    ...ABSOLUTE,
    env: { UNITY_PROJECT_PATH: "C:\\work\\My Game" },
  });
  assert.match(out, /UNITY_PROJECT_PATH = "C:\\\\work\\\\My Game"/);
});

test("a multi-line string elsewhere in the table is not mistaken for keys", () => {
  const existing = [
    "[mcp_servers.unity-open-mcp]",
    'note = """',
    'command = "not a key"',
    '"""',
    'command = "npx"',
    'args = ["-y", "unity-open-mcp@1.3.0"]',
    "",
  ].join("\n");
  const out = mergeCodexServer(existing, KEY, { ...ABSOLUTE, env: {} });
  assert.match(out, /command = "not a key"/);
  assert.match(out, /args = \["-y", "unity-open-mcp@1\.4\.0"\]/);
});

test("other spellings of the same entry are refused, not guessed at", () => {
  const refused = [
    'mcp_servers.unity-open-mcp.command = "npx"\n',
    '[mcp_servers]\nunity-open-mcp = { command = "npx" }\n',
    '[mcp_servers]\n"unity-open-mcp".command = "npx"\n',
    '[[mcp_servers.unity-open-mcp]]\ncommand = "npx"\n',
    '[mcp_servers.unity-open-mcp.env]\nUNITY_PROJECT_PATH = "/x"\n',
    '[mcp_servers.unity-open-mcp]\nenv.UNITY_PROJECT_PATH = "/x"\n',
    '[mcp_servers.unity-open-mcp]\nenv = { A = "1" }\n\n[mcp_servers.unity-open-mcp.env]\nB = "2"\n',
    '[mcp_servers.unity-open-mcp]\ncommand = "npx"\n[mcp_servers.unity-open-mcp]\ncommand = "npx"\n',
  ];
  for (const body of refused) {
    assert.throws(
      () => mergeCodexServer(body, KEY, ABSOLUTE),
      (error: unknown) => error instanceof CodexTomlError && error.code === "unsupported_codex_config",
      body,
    );
  }
});

test("a similarly named server is not ours", () => {
  const existing = '[mcp_servers.unity-open-mcp-old]\ncommand = "npx"\n';
  const out = mergeCodexServer(existing, KEY, ABSOLUTE);
  assert.ok(out.startsWith(existing));
  assert.match(out, /\n\[mcp_servers\.unity-open-mcp\]\n/);
});

test("a value that already matches keeps its formatting", () => {
  const existing = [
    "[mcp_servers.unity-open-mcp]",
    "command = 'npx'",
    "args = [",
    '    "-y",  # auto-confirm',
    '    "unity-open-mcp@1.4.0",',
    "]",
    "",
    "[mcp_servers.unity-open-mcp.env]",
    'UNITY_PROJECT_PATH = "/Users/dev/game/Client"',
    "",
  ].join("\n");
  assert.equal(mergeCodexServer(existing, KEY, ABSOLUTE), existing);
});
