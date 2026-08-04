import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync, chmodSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";

import { BatchSpawn, BATCH_TOOL_NAMES, VERIFY_BATCH_TOOL_NAMES, ALWAYS_BATCH_TOOLS, buildMetaArgs, buildVerifyArgs, extractCompilerErrors, classifyBatchFailure, extractOffendingPackages, BatchClassificationError, encodeSpaces, buildUnityBatchArgs, BoundedTextAccumulator } from "./batch-spawn.js";
import { lockPath } from "./instance-discovery.js";
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

// Guard the always-batch routing invariants. The router's single pinned branch
// keys off ALWAYS_BATCH_TOOLS; these tests prevent silent drift between the
// always-batch policy and the underlying batch-capable set.
test("VERIFY_BATCH_TOOL_NAMES is a subset of BATCH_TOOL_NAMES", () => {
  // Every verify-family tool must also be batch-capable, otherwise the
  // always-batch branch routes to a tool the batch spawner cannot run.
  for (const name of VERIFY_BATCH_TOOL_NAMES) {
    assert.ok(
      BATCH_TOOL_NAMES.has(name),
      `verify tool ${name} is always-batch but missing from BATCH_TOOL_NAMES`,
    );
  }
});

test("always-batch set is disjoint: compile_check not in verify set", () => {
  // compile_check has its own reason; it must not also appear in the verify
  // set, or the verify-set entry would shadow the compile_check reason.
  assert.ok(!VERIFY_BATCH_TOOL_NAMES.has("unity_open_mcp_compile_check"));
  // And the union map contains compile_check exactly once with its own reason.
  assert.equal(
    ALWAYS_BATCH_TOOLS.get("unity_open_mcp_compile_check"),
    "compile_check_always_batch",
  );
});

test("ALWAYS_BATCH_TOOLS covers compile_check + all verify tools with distinct reasons", () => {
  assert.equal(
    ALWAYS_BATCH_TOOLS.get("unity_open_mcp_compile_check"),
    "compile_check_always_batch",
  );
  for (const name of VERIFY_BATCH_TOOL_NAMES) {
    assert.equal(ALWAYS_BATCH_TOOLS.get(name), "verify_always_batch");
  }
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

// ---------------------------------------------------------------------------
// M26 Plan 3 — batch parity for execute_csharp / invoke_method / execute_menu
// ---------------------------------------------------------------------------

test("buildMetaArgs produces correct execute_csharp CLI flags", () => {
  const cli = buildMetaArgs("execute_csharp", {
    code: "return 1 + 2;",
    usings: ["System.IO"],
    object_ids: ["123"],
    max_depth: 6,
    max_items: 50,
    confirm_bypass: true,
  });

  // Spaces in the code payload are encoded as ASCII unit separator (0x1f) so
  // the snippet survives argv splitting; the C# entry point decodes them back.
  assert.deepEqual(cli, [
    "execute_csharp",
    "--code", encodeSpaces("return 1 + 2;"),
    "--using", "System.IO",
    "--object-id", "123",
    "--max-depth", "6",
    "--max-items", "50",
    "--confirm-bypass", "true",
  ]);
});

test("buildMetaArgs omits execute_csharp optional fields when not supplied", () => {
  const cli = buildMetaArgs("execute_csharp", {});
  assert.deepEqual(cli, ["execute_csharp"]);
});

test("buildMetaArgs forwards only code for a minimal execute_csharp call", () => {
  // A code value with no spaces passes through unencoded; spaces would be
  // turned into ASCII unit separators (covered by the encodeSpaces test).
  const cli = buildMetaArgs("execute_csharp", { code: "return;" });
  assert.deepEqual(cli, ["execute_csharp", "--code", "return;"]);
});

test("buildMetaArgs produces correct invoke_method CLI flags", () => {
  const cli = buildMetaArgs("invoke_method", {
    type_name: "UnityEngine.Transform",
    method_name: "GetPosition",
    is_static: true,
    assembly_name: "UnityEngine",
    args: [42, "hello"],
    arg_type_names: ["Int32"],
    generic_arg_types: ["UnityEngine.Vector3"],
    max_depth: 2,
  });

  assert.deepEqual(cli, [
    "invoke_method",
    "--type-name", "UnityEngine.Transform",
    "--method-name", "GetPosition",
    "--is-static", "true",
    "--assembly-name", "UnityEngine",
    "--arg", "42",
    "--arg", encodeSpaces("hello"),
    "--arg-type-name", "Int32",
    "--generic-arg-type", "UnityEngine.Vector3",
    "--max-depth", "2",
  ]);
});

test("buildMetaArgs omits invoke_method optional fields when not supplied", () => {
  const cli = buildMetaArgs("invoke_method", { type_name: "Foo", method_name: "Bar" });
  assert.deepEqual(cli, ["invoke_method", "--type-name", "Foo", "--method-name", "Bar"]);
});

test("buildMetaArgs produces correct execute_menu CLI flags", () => {
  const cli = buildMetaArgs("execute_menu", { menu_path: "Assets/Refresh" });
  assert.deepEqual(cli, ["execute_menu", "--menu-path", "Assets/Refresh"]);
});

test("buildMetaArgs omits execute_menu optional fields when not supplied", () => {
  const cli = buildMetaArgs("execute_menu", {});
  assert.deepEqual(cli, ["execute_menu"]);
});

test("execute_csharp route gets past the old fast-fail (now spawns via discovery)", async () => {
  // M26 Plan 3: the three meta-tools no longer fast-fail with
  // batch_not_supported. With no Unity discovered they surface the
  // unity_not_discovered error (the same path find_members takes), proving the
  // router now proceeds to spawn instead of short-circuiting.
  const savedPath = process.env.UNITY_PATH;
  delete process.env.UNITY_PATH;
  try {
    const batch = new BatchSpawn({ discoveryRoots: [] });
    const result = await batch.route("unity_open_mcp_execute_csharp", { code: "return 1;" });
    const body = parseBody(result);
    const error = body.error as Record<string, string>;
    assert.equal(error.code, "unity_not_discovered");
    assert.ok(
      !error.message.includes("not supported in batch mode"),
      "execute_csharp should no longer fast-fail with batch_not_supported",
    );
  } finally {
    if (savedPath) process.env.UNITY_PATH = savedPath;
  }
});

test("encodeSpaces replaces spaces with ASCII unit separator", () => {
  assert.equal(encodeSpaces("return 1 + 2;"), `return\x1f1\x1f+\x1f2;`);
  assert.equal(encodeSpaces("nospace"), "nospace");
  assert.equal(encodeSpaces(""), "");
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

// T6.1 — a caller passing timeout_ms:null must NOT produce `--timeout-ms null`
// on the argv. The C# parser would try to parse "null" as an int. The guard is
// typeof === "number" (not !== undefined), matching live-client.ts.
test("buildMetaArgs omits --timeout-ms when timeout_ms is null (not 'null' on argv)", () => {
  const cli = buildMetaArgs("compile_check", { timeout_ms: null });
  assert.deepEqual(cli, ["compile_check"], "null timeout_ms must not emit --timeout-ms");
});

test("buildMetaArgs omits numeric flags when null for other numeric fields (null guard sweep)", () => {
  // The same typeof guard applies to the other numeric argv fields so a null
  // value never stringifies to "null" on the spawn line.
  const findMembers = buildMetaArgs("find_members", { max_results: null, query: "Foo" });
  assert.deepEqual(findMembers, ["find_members", "--query", "Foo"]);

  const csharp = buildMetaArgs("execute_csharp", { code: "return;", max_depth: null, max_items: null });
  assert.deepEqual(csharp, ["execute_csharp", "--code", "return;"]);

  const invoke = buildMetaArgs("invoke_method", { type_name: "T", method_name: "M", max_depth: null, max_items: null });
  assert.deepEqual(invoke, ["invoke_method", "--type-name", "T", "--method-name", "M"]);
});

test("buildVerifyArgs omits --regression-threshold when null", () => {
  const cli = buildVerifyArgs("regression_check", { baseline_path: "b.json", regression_threshold: null });
  assert.deepEqual(cli, ["regression_check", "--baseline-path", "b.json"]);
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

// ---------------------------------------------------------------------------
// M22 Plan 3 / T-fix-2 — editor_instance_locked classification
// ---------------------------------------------------------------------------

test("classifyBatchFailure detects Unity's project-lock signature", () => {
  // The verbatim tail from the 2026-06-28 feedback entry.
  const tail = "Batch output did not contain JSON markers... another Unity instance is running with this project open";
  assert.equal(classifyBatchFailure(tail), "editor_instance_locked");
});

test("classifyBatchFailure detects 'already open' variant case-insensitively", () => {
  assert.equal(
    classifyBatchFailure("Project is ALREADY OPEN in another editor"),
    "editor_instance_locked",
  );
  assert.equal(
    classifyBatchFailure("Another Unity instance detected"),
    "editor_instance_locked",
  );
});

test("classifyBatchFailure returns null for genuine failures (no lock)", () => {
  // CS compiler errors, timeout, missing markers without the lock phrase.
  assert.equal(
    classifyBatchFailure("Assets/Foo.cs(10,14): error CS0246: type not found"),
    null,
  );
  assert.equal(
    classifyBatchFailure("Batch Unity process timed out after 600s."),
    null,
  );
  assert.equal(classifyBatchFailure(""), null);
  assert.equal(classifyBatchFailure("all good"), null);
});

test("classifyBatchFailure detects package-resolution / project-load signature", () => {
  // Verbatim signatures from the feedback entry (compile_check
  // batch_spawn_failed): Unity bails at package resolution before the batch
  // entry point runs, so no markers are emitted.
  assert.equal(
    classifyBatchFailure("[Package Manager] Project has invalid dependencies:"),
    "project_load_failed",
  );
  assert.equal(
    classifyBatchFailure("Package Manager Server process was shutdown"),
    "project_load_failed",
  );
  assert.equal(
    classifyBatchFailure("Package Manager Server process was terminated"),
    "project_load_failed",
  );
});

test("classifyBatchFailure prefers editor_instance_locked when both signatures appear", () => {
  // A live-editor lock is the more actionable failure (close the Editor);
  // it should take precedence over a project-load signal if both appear.
  assert.equal(
    classifyBatchFailure("another Unity instance ... Project has invalid dependencies"),
    "editor_instance_locked",
  );
});

test("classifyBatchFailure keeps returning null for a clean compile tail", () => {
  // The case-2 reproduction: exit 0, healthy compile, no markers. The tail
  // has no lock/project-load phrase, so the classifier returns null and the
  // close handler's exit-0 message improvement handles it. This guards
  // against a future regex widening that would misclassify a clean tail.
  assert.equal(
    classifyBatchFailure("Compilation succeeded. Elapsed time: 1.23s"),
    null,
  );
});

test("extractOffendingPackages captures reverse-DNS ids and dedupes", () => {
  // A Package Manager resolution failure names the offending package(s) by
  // their reverse-DNS identifier. extractOffendingPackages pulls them out so
  // the project_load_failed error can name them.
  const tail =
    "[Package Manager] com.unity.modules.physicscore2d is not a valid package.\n" +
    "Missing or invalid dependencies: com.unity.modules.physicscore2d\n" +
    "while org.nuget.somepackage was also referenced";
  assert.deepEqual(extractOffendingPackages(tail), [
    "com.unity.modules.physicscore2d",
    "org.nuget.somepackage",
  ]);
  // Dedupes repeats and ignores bare two-label prefixes.
  assert.deepEqual(
    extractOffendingPackages("com.unity com.unity com.foo.bar com.foo.bar"),
    ["com.foo.bar"],
  );
  assert.deepEqual(extractOffendingPackages(""), []);
  assert.deepEqual(extractOffendingPackages("no packages here"), []);
});

test("BatchClassificationError carries a targeted code for route()", () => {
  const err = new BatchClassificationError(
    "editor_instance_locked",
    "live Editor holds the lock",
  );
  assert.equal(err instanceof Error, true);
  assert.equal(err.code, "editor_instance_locked");
  assert.equal(err.name, "BatchClassificationError");
  assert.ok(err.message.includes("live Editor"));
});

test("compile_check with a live Editor open surfaces editor_instance_locked, not batch_spawn_failed", async () => {
  // Regression for the 2026-06-28 feedback entry: route() must emit
  // editor_instance_locked when the batch tail matches the project-lock
  // signature. We drive it through a fake Unity that echoes the lock phrase
  // to stderr and exits non-zero without JSON markers, so the spawn hits the
  // classifyBatchFailure branch.
  const savedPath = process.env.UNITY_PATH;
  delete process.env.UNITY_PATH;
  try {
    const tmp = mkdtempSync(join(tmpdir(), "batch-lock-"));
    try {
      const installDir = join(tmp, "6000.0.0f1");
      const exeRel = process.platform === "win32"
        ? ["Editor", "Unity.exe"]
        : process.platform === "darwin"
          ? ["Unity.app", "Contents", "MacOS", "Unity"]
          : ["Editor", "Unity"];
      const exe = join(installDir, ...exeRel);
      mkdirSync(dirname(exe), { recursive: true });
      // Fake binary prints the lock signature to stderr, then exits 1 without
      // emitting JSON markers — exactly the shape Unity's project-lock refusal
      // produces.
      if (process.platform === "win32") {
        writeFileSync(exe, "fake");
      } else {
        writeFileSync(
          exe,
          "#!/bin/sh\n" +
            'echo "another Unity instance is running with this project open" 1>&2\n' +
            "exit 1\n",
        );
        chmodSync(exe, 0o755);
      }

      const batch = new BatchSpawn({ discoveryRoots: [tmp], projectPath: tmp });
      const result = await batch.route("unity_open_mcp_compile_check", {});
      const body = parseBody(result);
      const error = body.error as Record<string, string>;
      assert.equal(error.code, "editor_instance_locked");
      assert.ok(
        error.message.includes("live Unity Editor"),
        "message should explain the live-Editor lock",
      );
      // specs/feedback.md (editor_instance_locked) — the response now carries a
      // structured recovery hint array pointing at the live-bridge branch.
      assert.ok(
        Array.isArray(body.agentNextSteps) && body.agentNextSteps.length > 0,
        "editor_instance_locked should carry a non-empty agentNextSteps array",
      );
      assert.ok(
        (body.agentNextSteps as string[]).some((s) => s.includes("read_compile_errors")),
        "agentNextSteps should mention read_compile_errors",
      );
    } finally {
      rmSync(tmp, { recursive: true, force: true });
    }
  } finally {
    if (savedPath === undefined) delete process.env.UNITY_PATH;
    else process.env.UNITY_PATH = savedPath;
  }
});

// ---------------------------------------------------------------------------
// specs/feedback.md (compile_check batch_spawn_failed) — when Unity bails at
// package resolution before the batch entry point runs, no JSON markers are
// emitted. route() must emit project_load_failed (not the generic
// batch_spawn_failed) so an agent does not mistake a manifest problem for a
// compile failure or a parsing failure.
// ---------------------------------------------------------------------------
test("compile_check surfaces project_load_failed when the batch tail matches the package-resolution signature", async () => {
  const savedPath = process.env.UNITY_PATH;
  delete process.env.UNITY_PATH;
  try {
    const tmp = mkdtempSync(join(tmpdir(), "batch-pkgfail-"));
    try {
      const installDir = join(tmp, "6000.0.0f1");
      const exeRel = process.platform === "win32"
        ? ["Editor", "Unity.exe"]
        : process.platform === "darwin"
          ? ["Unity.app", "Contents", "MacOS", "Unity"]
          : ["Editor", "Unity"];
      const exe = join(installDir, ...exeRel);
      mkdirSync(dirname(exe), { recursive: true });
      // Fake binary prints the package-resolution failure to stderr, names the
      // offending package, then exits 1 without JSON markers — exactly the
      // shape Unity's manifest-resolution bail-out produces.
      if (process.platform === "win32") {
        writeFileSync(exe, "fake");
      } else {
        writeFileSync(
          exe,
          "#!/bin/sh\n" +
            'echo "[Package Manager] Project has invalid dependencies:" 1>&2\n' +
            'echo "[Package Manager] com.unity.modules.physicscore2d is not a valid package." 1>&2\n' +
            "exit 1\n",
        );
        chmodSync(exe, 0o755);
      }

      const batch = new BatchSpawn({ discoveryRoots: [tmp], projectPath: tmp });
      const result = await batch.route("unity_open_mcp_compile_check", {});
      const body = parseBody(result);
      const error = body.error as Record<string, string>;
      assert.equal(error.code, "project_load_failed");
      assert.ok(
        error.message.includes("package/project resolution"),
        "message should explain this is a package-resolution failure, not a compile failure",
      );
      assert.ok(
        error.message.includes("com.unity.modules.physicscore2d"),
        "message should name the offending package extracted from the tail",
      );
      assert.ok(
        Array.isArray(body.agentNextSteps) && body.agentNextSteps.length > 0,
        "project_load_failed should carry a non-empty agentNextSteps array",
      );
      assert.ok(
        (body.agentNextSteps as string[]).some((s) => s.includes("read_compile_errors")),
        "agentNextSteps should mention read_compile_errors",
      );
      assert.ok(
        (body.agentNextSteps as string[]).some((s) => s.includes("manifest")),
        "agentNextSteps should point at the manifest",
      );
    } finally {
      rmSync(tmp, { recursive: true, force: true });
    }
  } finally {
    if (savedPath === undefined) delete process.env.UNITY_PATH;
    else process.env.UNITY_PATH = savedPath;
  }
});

// ---------------------------------------------------------------------------
// feedback-fable-31-07 §5 — pre-spawn instance-lock check. When a live editor
// holds the project, the batch spawn must short-circuit BEFORE spawning (the
// spawn is guaranteed to fail on the project lock AND its startup rotates
// Editor.log, poisoning read_compile_errors).
// ---------------------------------------------------------------------------
test("compile_check short-circuits with editor_instance_locked when a live editor holds the project", async () => {
  // Write a fake instance lock for a temp project whose PID is THIS test
  // process (guaranteed alive). The pre-check must refuse to spawn and emit
  // editor_instance_locked WITHOUT running Unity.
  const tmp = mkdtempSync(join(tmpdir(), "batch-precheck-"));
  const lockFile = lockPath(tmp);
  // Ensure the parent dir exists (~/.unity-open-mcp/instances).
  mkdirSync(dirname(lockFile), { recursive: true });
  const alivePid = process.pid;
  const lockJson = JSON.stringify({
    pid: alivePid,
    port: 20000,
    projectPath: tmp,
    projectHash: "deadbeef",
    startedAt: "2026-07-31T00:00:00Z",
    updatedAt: "2026-07-31T00:00:00Z",
    heartbeatAt: new Date().toISOString(),
    state: "healthy",
    isPlaying: false,
    isCompiling: false,
    bridgeVersion: "0.0.0-test",
    unityVersion: "6000.0.0f1",
  });
  writeFileSync(lockFile, lockJson);
  try {
    // A Unity path is not even needed — the pre-check runs before path
    // validation is required to spawn, but validateUnityPath runs first. Point
    // at a harmless fake to satisfy it (the spawn must never happen).
    const batch = new BatchSpawn({ discoveryRoots: [tmp], projectPath: tmp });
    const result = await batch.route("unity_open_mcp_compile_check", {});
    const body = parseBody(result);
    const error = body.error as Record<string, unknown>;
    assert.equal(error.code, "editor_instance_locked");
    assert.ok(
      (error.message as string).includes("live Unity Editor"),
      "message should explain the live-Editor lock",
    );
    assert.ok(
      Array.isArray(body.agentNextSteps) && body.agentNextSteps.length > 0,
      "pre-check editor_instance_locked should carry agentNextSteps",
    );
  } finally {
    try { rmSync(lockFile, { force: true }); } catch { /* best effort */ }
    rmSync(tmp, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// compile_check spawn argv — omit -quit (async finalize path)
// ---------------------------------------------------------------------------

test("buildUnityBatchArgs omits -quit for compile_check", () => {
  const args = buildUnityBatchArgs(
    "compile_check",
    "/proj",
    "UnityOpenMcpBridge.Batch.BridgeBatchEntry.Run",
    ["compile_check"],
  );
  assert.ok(!args.includes("-quit"), "compile_check must not pass -quit");
  assert.deepEqual(args.slice(0, 2), ["-batchmode", "-projectPath"]);
});

test("buildUnityBatchArgs includes -quit for synchronous batch ops", () => {
  const args = buildUnityBatchArgs(
    "find_members",
    "/proj",
    "UnityOpenMcpBridge.Batch.BridgeBatchEntry.Run",
    ["find_members", "--query", "Transform"],
  );
  assert.deepEqual(args.slice(0, 3), ["-batchmode", "-quit", "-projectPath"]);
});

test("compile_check with exit 127 surfaces unity_spawn_refused, not batch_spawn_failed", async () => {
  const savedPath = process.env.UNITY_PATH;
  delete process.env.UNITY_PATH;
  try {
    const tmp = mkdtempSync(join(tmpdir(), "batch-spawn-127-"));
    try {
      const installDir = join(tmp, "6000.0.0f1");
      const exeRel = process.platform === "win32"
        ? ["Editor", "Unity.exe"]
        : process.platform === "darwin"
          ? ["Unity.app", "Contents", "MacOS", "Unity"]
          : ["Editor", "Unity"];
      const exe = join(installDir, ...exeRel);
      mkdirSync(dirname(exe), { recursive: true });
      if (process.platform === "win32") {
        // On win32 we cannot write a real shell `exit 127` stub; a non-
        // executable "fake" blob makes the spawn fail with ENOENT/EACCES,
        // which classifies to the same `unity_spawn_refused` code via the
        // child.on("error") path (not a genuine exit-127 process exit). The
        // assertion below holds on both branches, but the win32 path is
        // exercised through a different failure mode than the test name
        // implies.
        writeFileSync(exe, "fake");
      } else {
        writeFileSync(exe, "#!/bin/sh\nexit 127\n");
        chmodSync(exe, 0o755);
      }

      const batch = new BatchSpawn({ discoveryRoots: [tmp], projectPath: tmp });
      const result = await batch.route("unity_open_mcp_compile_check", {});
      const body = parseBody(result);
      const error = body.error as Record<string, string>;
      assert.equal(error.code, "unity_spawn_refused");
      assert.ok(
        Array.isArray(body.agentNextSteps) && body.agentNextSteps.length > 0,
        "unity_spawn_refused should carry agentNextSteps",
      );
      assert.ok(
        (body.agentNextSteps as string[]).some((s) => s.includes("read_compile_errors")),
        "agentNextSteps should mention read_compile_errors",
      );
    } finally {
      rmSync(tmp, { recursive: true, force: true });
    }
  } finally {
    if (savedPath === undefined) delete process.env.UNITY_PATH;
    else process.env.UNITY_PATH = savedPath;
  }
});

// ---------------------------------------------------------------------------
// M13 — BoundedTextAccumulator: UTF-8 correctness + bounded retention
// ---------------------------------------------------------------------------

test("M13: BoundedTextAccumulator reassembles a multi-byte UTF-8 char split across chunks", () => {
  // "café" — 'é' is U+00E9, two bytes in UTF-8 (0xC3 0xA9). Split the bytes so
  // 0xC3 lands at the end of one chunk and 0xA9 at the start of the next. The
  // naive `chunk.toString()` approach would emit U+FFFD for each half.
  const full = Buffer.from("café", "utf8");
  const splitAt = full.indexOf(0xc3);
  const acc = new BoundedTextAccumulator();
  acc.push(full.subarray(0, splitAt + 1)); // "caf" + lead byte 0xC3
  acc.push(full.subarray(splitAt + 1));    // trail byte 0xA9
  acc.flush();
  assert.equal(acc.toString(), "café", "split multi-byte char must reassemble");
});

test("M13: BoundedTextAccumulator preserves non-ASCII inside a JSON payload", () => {
  // A verify JSON payload whose asset path contains non-ASCII. Decode the
  // whole payload, then feed it byte-by-byte (worst case for naive decoders).
  const payload = `VERIFY_JSON_BEGIN{"asset":"Assets/Tëst.mat"}VERIFY_JSON_END`;
  const bytes = Buffer.from(payload, "utf8");
  const acc = new BoundedTextAccumulator();
  for (let i = 0; i < bytes.length; i++) acc.push(bytes.subarray(i, i + 1));
  acc.flush();
  assert.equal(acc.toString(), payload, "byte-by-byte feed must reconstruct the exact payload");
});

test("M13: BoundedTextAccumulator caps retained bytes (drops the head, keeps the tail)", () => {
  // Feed well over the 16 MiB cap of head bytes then a known tail marker, and
  // assert the retained buffer is bounded near the cap while the tail marker
  // survives. The cap keeps the verify JSON markers (emitted at the tail)
  // while bounding memory on a 10-minute run that spams compile/import logs.
  const acc = new BoundedTextAccumulator();
  const tailMarker = "__TAIL_MARKER__";
  const headSize = 40 * 1024 * 1024; // 40 MiB of head — well past the 16 MiB cap
  const chunkSize = 1024 * 1024;
  const headChunk = Buffer.alloc(chunkSize, 0x61); // 'a'
  for (let i = 0; i < headSize; i += chunkSize) acc.push(headChunk);
  acc.push(Buffer.from(tailMarker, "utf8"));
  acc.flush();
  const out = acc.toString();
  assert.ok(out.endsWith(tailMarker), "tail marker must survive the cap");
  // Retained buffer is bounded near the 16 MiB cap (one chunk of slack from
  // the whole-buffer drop policy), nowhere near the 40 MiB fed in.
  assert.ok(out.length <= 17 * 1024 * 1024, `retained buffer must be bounded near the cap (got ${out.length})`);
  assert.ok(out.length < headSize, "retained buffer must be smaller than the head fed in (cap applied)");
});

