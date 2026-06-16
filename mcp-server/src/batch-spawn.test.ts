import test from "node:test";
import assert from "node:assert/strict";

import { BatchSpawn, BATCH_TOOL_NAMES, buildMetaArgs } from "./batch-spawn.js";
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

test("find_members without UNITY_PATH returns path error, not batch_not_supported", async () => {
  const savedPath = process.env.UNITY_PATH;
  delete process.env.UNITY_PATH;
  try {
    const batch = new BatchSpawn();
    const result = await batch.route("unity_open_mcp_find_members", { query: "Transform" });
    assert.equal(result.isError, true);
    const body = parseBody(result);
    const error = body.error as Record<string, string>;
    assert.equal(error.code, "unity_path_missing");
  } finally {
    if (savedPath) process.env.UNITY_PATH = savedPath;
  }
});
