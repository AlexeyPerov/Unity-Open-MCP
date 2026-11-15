// S0 (`scripts/mcp-full-test.mjs`) and S4 (`scripts/mcp-extensions.mjs`) must
// send arguments the published tool schemas accept. A step that fails MCP
// preflight measures the script, not the tool: after schema changes ~70 S0
// steps drifted into "invalid_arguments
// (tolerated)" without anyone noticing. `--check-args` builds every step
// (placeholder values for ids an earlier step would capture) and validates it
// with the same withSchemaDefaults + validateSchema the server runs — no Unity
// needed. Schema changes land in mcp-server/, so this test is where they meet
// the suite.

import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

function findSuite(script = "mcp-full-test.mjs"): string | null {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (let i = 0; i < 10; i++) {
    const candidate = join(dir, "scripts", script);
    if (existsSync(candidate)) return candidate;
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}

test("every S0 step sends schema-valid arguments", (t) => {
  const suite = findSuite();
  if (!suite) {
    t.skip("scripts/mcp-full-test.mjs not found (not a repo checkout)");
    return;
  }
  const run = spawnSync(process.execPath, [suite, "--check-args"], { encoding: "utf-8", timeout: 60_000 });
  assert.equal(run.status, 0, `S0 steps drifted from the tool schemas:\n${run.stdout}${run.stderr}`);
  assert.match(run.stdout, /All \d+ step\(s\) send schema-valid arguments/);
});

test("every S4 step sends schema-valid arguments", (t) => {
  const suite = findSuite("mcp-extensions.mjs");
  if (!suite) {
    t.skip("scripts/mcp-extensions.mjs not found (not a repo checkout)");
    return;
  }
  const run = spawnSync(process.execPath, [suite, "--check-args"], { encoding: "utf-8", timeout: 60_000 });
  assert.equal(run.status, 0, `S4 steps drifted from the tool schemas:\n${run.stdout}${run.stderr}`);
  assert.match(run.stdout, /All \d+ step\(s\) send schema-valid arguments/);
});
