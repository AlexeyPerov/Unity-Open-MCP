import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync, chmodSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";

import { BatchSpawn, BATCH_TOOL_NAMES, buildMetaArgs, buildVerifyArgs, extractCompilerErrors } from "./batch-spawn.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

function parseBody(result: CallToolResult): Record<string, unknown> {
  const first = result.content[0];
  if (!first || first.type !== "text" || typeof first.text !== "string") {
    throw new Error("expected a text content part");
  }
  return JSON.parse(first.text);
}

test("BATCH_TOOL_NAMES includes find_members and limited meta-tools", () => {
  assert.ok(BATCH_TOOL_NAMES.has("unity_open_mcp_find_members"));
  assert.ok(BATCH_TOOL_NAMES.has("unity_open_mcp_execute_csharp"));
  assert.ok(BATCH_TOOL_NAMES.has("unity_open_mcp_invoke_method"));
  assert.ok(BATCH_TOOL_NAMES.has("unity_open_mcp_execute_menu"));
  assert.ok(BATCH_TOOL_NAMES.has("unity_open_mcp_scan_all"));
  assert.ok(BATCH_TOOL_NAMES.has("unity_open_mcp_baseline_create"));
  assert.ok(BATCH_TOOL_NAMES.has("unity_open_mcp_regression_check"));
});

test("isBatchTool returns true for all batch-capable tools", () => {
  const batch = new BatchSpawn();
  assert.ok(batch.isBatchTool("unity_open_mcp_find_members"));
  assert.ok(batch.isBatchTool("unity_open_mcp_execute_csharp"));
  assert.ok(batch.isBatchTool("unity_open_mcp_invoke_method"));
  assert.ok(batch.isBatchTool("unity_open_mcp_execute_menu"));
  assert.ok(batch.isBatchTool("unity_open_mcp_scan_all"));
});

test("isBatchTool returns false for non-batch tools", () => {
  const batch = new BatchSpawn();
  assert.ok(!batch.isBatchTool("unity_open_mcp_ping"));
  assert.ok(!batch.isBatchTool("unity_open_mcp_validate_edit"));
  assert.ok(!batch.isBatchTool("unknown_tool"));
});

test("limited meta-tools fast-fail without spawning Unity", async () => {
  const batch = new BatchSpawn();
  const tools = [
    "unity_open_mcp_execute_csharp",
    "unity_open_mcp_invoke_method",
    "unity_open_mcp_execute_menu",
  ] as const;

  for (const tool of tools) {
    const result = await batch.route(tool, {});
    assert.equal(result.isError, true);
    const body = parseBody(result);
    const error = body.error as Record<string, string>;
    assert.equal(error.code, "batch_not_supported");
    assert.ok(
      error.message.includes("not supported in batch mode"),
      `${tool} error should mention batch mode`,
    );
    assert.ok(
      error.message.includes("find_members"),
      `${tool} error should mention find_members as available`,
    );
  }
});

test("limited meta-tool errors mention the specific limitation", async () => {
  const batch = new BatchSpawn();

  const csharp = await batch.route("unity_open_mcp_execute_csharp", { code: "return 1;" });
  const csharpBody = parseBody(csharp);
  assert.ok(
    (csharpBody.error as Record<string, string>).message.includes("gate"),
    "execute_csharp error should mention gate",
  );

  const menu = await batch.route("unity_open_mcp_execute_menu", { menu_path: "Assets/Refresh" });
  const menuBody = parseBody(menu);
  assert.ok(
    (menuBody.error as Record<string, string>).message.includes("UI"),
    "execute_menu error should mention UI",
  );
});

test("buildMetaArgs produces correct find_members CLI flags", () => {
  const cli = buildMetaArgs("find_members", {
    query: "Transform",
    kind: "type",
    assembly_filter: "UnityEngine",
    include_unity_editor: false,
    include_project: true,
    max_results: 100,
  });

  assert.deepEqual(cli, [
    "find_members",
    "--query", "Transform",
    "--kind", "type",
    "--assembly-filter", "UnityEngine",
    "--include-unity-editor", "false",
    "--include-project", "true",
    "--max-results", "100",
  ]);
});

test("buildMetaArgs omits undefined optional fields", () => {
  const cli = buildMetaArgs("find_members", {
    query: "Rigidbody",
  });

  assert.deepEqual(cli, ["find_members", "--query", "Rigidbody"]);
});

test("buildMetaArgs produces minimal args for empty input", () => {
  const cli = buildMetaArgs("find_members", {});
  assert.deepEqual(cli, ["find_members"]);
});

test("find_members without UNITY_PATH returns discovery error, not batch_not_supported", async () => {
  const savedPath = process.env.UNITY_PATH;
  delete process.env.UNITY_PATH;
  try {
    // Point discovery at an empty roots list so it deterministically finds
    // nothing (otherwise a dev/CI machine with Unity installed would spawn).
    const batch = new BatchSpawn({ discoveryRoots: [] });
    const result = await batch.route("unity_open_mcp_find_members", { query: "Transform" });
    assert.equal(result.isError, true);
    const body = parseBody(result);
    const error = body.error as Record<string, string>;
    // New code: discovery scanned and found nothing (distinct from the old
    // UNITY_PATH-mandatory `unity_path_missing`).
    assert.equal(error.code, "unity_not_discovered");
    assert.ok(
      error.message.includes("auto-discovers"),
      "error should explain the auto-discovery fallback",
    );
  } finally {
    if (savedPath) process.env.UNITY_PATH = savedPath;
  }
});

test("find_members auto-discovers Unity when UNITY_PATH unset and an install exists", async () => {
  // This test exercises the discovery→spawn path without actually running a
  // real Unity: we point discovery at a temp dir with a fake install, then
  // assert that validation passes (no unity_not_discovered) and the spawn is
  // attempted. The fake binary is not a real Unity so the spawn will fail
  // downstream — we only assert that we got PAST path validation.
  const savedPath = process.env.UNITY_PATH;
  delete process.env.UNITY_PATH;
  try {
    // We can't easily build a fake Unity executable that runs -batchmode; the
    // point of this test is just that discovery resolves a path. Build a tiny
    // fake binary that exits immediately so the spawn fails fast but cleanly.
    const tmp = mkdtempSync(join(tmpdir(), "batch-disc-"));
    try {
      const installDir = join(tmp, "6000.0.0f1");
      const exeRel = process.platform === "win32"
        ? ["Editor", "Unity.exe"]
        : process.platform === "darwin"
          ? ["Unity.app", "Contents", "MacOS", "Unity"]
          : ["Editor", "Unity"];
      const exe = join(installDir, ...exeRel);
      mkdirSync(dirname(exe), { recursive: true });
      // Non-Windows: write a shell script that exits 1 immediately so the
      // spawn doesn't hang. Windows: write a tiny placeholder; the spawn
      // will fail to execute but path validation passes first.
      if (process.platform === "win32") {
        writeFileSync(exe, "fake");
      } else {
        writeFileSync(exe, "#!/bin/sh\nexit 1\n");
        chmodSync(exe, 0o755);
      }

      const batch = new BatchSpawn({
        discoveryRoots: [tmp],
        projectPath: tmp,
      });
      const result = await batch.route("unity_open_mcp_find_members", { query: "Transform" });
      const body = parseBody(result);
      const error = body.error as Record<string, string> | undefined;
      // We should NOT have gotten the discovery error — that means discovery
      // worked and we proceeded to spawn. The spawn itself failing is fine
      // (it's a fake binary); we just need `code !== "unity_not_discovered"`.
      assert.ok(
        !error || error.code !== "unity_not_discovered",
        "discovery should have resolved the fake install (got past validation)",
      );
    } finally {
      rmSync(tmp, { recursive: true, force: true });
    }
  } finally {
    if (savedPath === undefined) delete process.env.UNITY_PATH;
    else process.env.UNITY_PATH = savedPath;
  }
});

// --- compile_check wiring --------------------------------------------------

test("BATCH_TOOL_NAMES includes compile_check", () => {
  assert.ok(BATCH_TOOL_NAMES.has("unity_open_mcp_compile_check"));
});

test("isBatchTool returns true for compile_check", () => {
  const batch = new BatchSpawn();
  assert.ok(batch.isBatchTool("unity_open_mcp_compile_check"));
});

test("buildMetaArgs passes through timeout_ms for compile_check", () => {
  const cli = buildMetaArgs("compile_check", { timeout_ms: 120000 });
  assert.deepEqual(cli, ["compile_check", "--timeout-ms", "120000"]);
});

test("buildMetaArgs omits timeout_ms when not supplied for compile_check", () => {
  const cli = buildMetaArgs("compile_check", {});
  assert.deepEqual(cli, ["compile_check"]);
});

test("extractCompilerErrors pulls CSxxxx lines from raw output", () => {
  const out = [
    "Some preamble line",
    "Assets/Broken.cs(10,14): error CS0246: The type or namespace name 'Foo' could not be found",
    "Assets/Broken.cs(20,2): error CS0103: The name 'Bar' does not exist in the current context",
    "a non-error line",
    "  error CS1002: ; expected (indented variant)",
  ].join("\n");
  const errors = extractCompilerErrors(out);
  assert.equal(errors.length, 3);
  assert.ok(errors[0].includes("CS0246"));
  assert.ok(errors[1].includes("CS0103"));
  assert.ok(errors[2].includes("CS1002"), "indented error lines are captured");
});

test("extractCompilerErrors returns [] when no CS errors present", () => {
  assert.deepEqual(extractCompilerErrors("all good, no errors here"), []);
  assert.deepEqual(extractCompilerErrors(""), []);
});

test("extractCompilerErrors dedupes repeated lines", () => {
  const line = "Assets/Broken.cs(10,14): error CS0246: The type 'Foo' could not be found";
  const out = `${line}\n${line}\n${line}`;
  const errors = extractCompilerErrors(out);
  assert.equal(errors.length, 1);
});

// ---------------------------------------------------------------------------
// buildVerifyArgs — per-category thresholds forwarded to the CLI
// ---------------------------------------------------------------------------

test("buildVerifyArgs forwards regression_check args unchanged without per-category map", () => {
  const cli = buildVerifyArgs("regression_check", {
    baseline_path: "CI/baseline.json",
    regression_threshold: 2,
    platform_profile: "mobile",
  });
  assert.deepEqual(cli, [
    "regression_check",
    "--baseline-path", "CI/baseline.json",
    "--regression-threshold", "2",
    "--platform-profile", "mobile",
  ]);
});

test("buildVerifyArgs emits per-category threshold flags in stable key order", () => {
  // Insertion order is intentionally non-alphabetical; the builder must sort
  // by key so the spawn line is deterministic for CI snapshots.
  const cli = buildVerifyArgs("regression_check", {
    baseline_path: "baseline.json",
    per_category_thresholds: {
      dependencies: 3,
      missing_references: 1,
    },
  });
  assert.deepEqual(cli, [
    "regression_check",
    "--baseline-path", "baseline.json",
    "--per-category-threshold", "dependencies=3",
    "--per-category-threshold", "missing_references=1",
  ]);
});

test("buildVerifyArgs truncates non-integer per-category values", () => {
  const cli = buildVerifyArgs("regression_check", {
    baseline_path: "b.json",
    per_category_thresholds: { missing_references: 2.9 },
  });
  assert.deepEqual(cli, [
    "regression_check",
    "--baseline-path", "b.json",
    "--per-category-threshold", "missing_references=2",
  ]);
});

test("buildVerifyArgs drops negative or non-finite per-category values", () => {
  const cli = buildVerifyArgs("regression_check", {
    baseline_path: "b.json",
    per_category_thresholds: {
      missing_references: -1,
      dependencies: Number.POSITIVE_INFINITY,
      scene_prefab_health: 0,
    },
  });
  assert.deepEqual(cli, [
    "regression_check",
    "--baseline-path", "b.json",
    "--per-category-threshold", "scene_prefab_health=0",
  ]);
});
