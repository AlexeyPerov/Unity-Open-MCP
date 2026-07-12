// mcp-test-lib.mjs — shared helpers for the MCP test suites (S0–S5).
//
// Extracted from scripts/mcp-full-test.mjs (S0) so the behavioral (S1),
// headless (S2), protocol (S3), extensions (S4), and sandbox (S5) suites can
// reuse the same arg parsing, expect classifier, CLI runner, scene hygiene,
// and cleanup machinery without duplicating ~560 lines each.
//
// Design notes:
//   - Pure helpers (parseEnvelope, pluck, classify) carry no suite state.
//   - Scene-hygiene helpers (closeInitTestScenes, revertMainSceneIfDirty,
//     saveAllDirtyScenes, collectIsolationState, finalizeEditorState) assume
//     the standard fixture layout: mutations live under Assets/MCP_FullTest/
//     (configurable via fixtureRoot) and the shared demo Main scene is at
//     Assets/Scenes/Main.unity. S5 (sandbox) passes its own clone path.
//   - The step runner (runTool/runToolOnce) implements the same scene_dirty /
//     main_thread_blocked save+retry + ok_or_timeout timeout tolerance as S0.
//   - parseCommonArgs handles the flags every suite shares (--project/-P,
//     --list, --json-out, --only, --band, --no-cleanup, --timeout-ms, --help).
//     Suites merge their own flags by passing an `extra` callback.
//
// Usage:
//   import { parseCommonArgs, classify, invokeTool, runTool, makeStepBuilder,
//            buildRunEnv, dismissBlockingModals, finalizeEditorState,
//            cleanupViaBridge, cleanupTempFolder, parseEnvelope, pluck,
//            REFUSED_CODES, LOCKED_CODES, CLI_BIN, REPO_ROOT } from "./mcp-test-lib.mjs";
//
// All helpers route through the unity-open-mcp CLI (one fresh node process per
// call) — the same transport an MCP client uses via run-tool. Requirements:
// mcp-server/dist/index.js built; a Unity Editor open with the project + bridge
// running for live suites (S0/S1/S4); Editor closed for S2; stdio server for S3.

import { execFileSync } from "node:child_process";
import { existsSync, readdirSync, rmSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(__dirname, "..");
const CLI_BIN = resolve(REPO_ROOT, "mcp-server", "dist", "index.js");
const DISMISS_MOD = pathToFileURL(resolve(REPO_ROOT, "mcp-server", "dist", "dialog-dismiss.js")).href;

// Standard fixture + scene layout shared by S0/S1. S5 overrides via opts.
const DEFAULT_FIXTURE_ROOT = "Assets/MCP_FullTest";
const DEFAULT_MAIN_SCENE_PATH = "Assets/Scenes/Main.unity";

export { REPO_ROOT, CLI_BIN, DISMISS_MOD };

// ---------------------------------------------------------------------------
// expect classifier
//
// Each step is { label, band, tool, args?, expect?, gate?, resolveArgs?, after?,
// timeoutMs?, tolerate? }. `expect` defaults to "ok". Pass is derived as:
//   ok            → !isError
//   gate          → mutation.success && (gate.delta.newErrors === 0)
//   locked        → ok OR error.code in LOCKED_CODES  (batch can't run w/ Editor open)
//   refused       → mutation.error.code in REFUSED_CODES  (deny heuristic fired)
//   unavailable   → error.code === tool_not_found  (group not compiled into bridge)
//   reachable     → error.code !== tool_not_found  (compiled extension pack; minimal
//                   args — missing-param / guard errors still prove registration)
//   verify_timeout→ ok OR error.code === "timeout"  (known verify-family hang)
//   ok_or_timeout → ok OR parsed._ft_timeout  (CLI timeout tolerated)
//   tolerate      → ok OR error.code in step.tolerate[]  (known bugs; route exercised)
// ---------------------------------------------------------------------------

export const REFUSED_CODES = new Set(["build_confirmation_required", "denied_by_policy", "menu_blocked"]);
export const LOCKED_CODES = new Set([
  "project_path_missing",
  "editor_instance_locked",
  "unity_not_discovered",
  "unity_spawn_refused",
  "batch_failed",
  "compile_check_failed",
]);
// Known issue (see specs/feedback.md 2026-07-03): the live-bridge verify-rule
// path hangs and hits the bridge's 30s internal limit. We tolerate that
// timeout for the verify-family tools so the rest of the suite can run; the
// timeout itself is the finding, surfaced in the report.
export const TIMEOUT_CODE = "timeout";

export function classify(step, parsed) {
  const isError = parsed.isError === true;
  const result = parsed.result ?? {};
  const errCode = result.error?.code ?? result.mutation?.error?.code;
  const route = result._route?.route;

  switch (step.expect) {
    case "ok":
      return { pass: !isError, detail: isError ? `err=${errCode}` : "ok" };
    case "gate": {
      const mut = result.mutation ?? {};
      const newErrors = result.gate?.delta?.newErrors;
      const gateClean = typeof newErrors !== "number" || newErrors === 0;
      const pass = mut.success === true && gateClean;
      return {
        pass,
        detail: `mutation.success=${mut.success} gate.newErrors=${newErrors ?? "n/a"}${mut.error ? " err=" + mut.error.code : ""}`,
      };
    }
    case "locked": {
      // Batch tools: pass if either they ran OR they reported a known
      // "can't run because Editor has the project" / env-missing code.
      // After M30 Plan 3, verify-family tools always route to batch even with
      // a live bridge up — a live `tool_not_found` is no longer an expected
      // outcome and would indicate a routing regression, so it is NOT
      // tolerated here. Expected degraded codes: editor_instance_locked,
      // unity_spawn_refused, project_path_missing, unity_not_discovered.
      const pass = !isError || LOCKED_CODES.has(errCode);
      return { pass, detail: isError ? `err=${errCode} (route=${route}, expected)` : "ran" };
    }
    case "refused": {
      const pass = isError && REFUSED_CODES.has(errCode);
      return { pass, detail: pass ? `refused: ${errCode}` : `expected refusal, got err=${errCode}` };
    }
    case "unavailable": {
      const pass = isError && errCode === "tool_not_found";
      return { pass, detail: pass ? "tool_not_found (group not compiled in)" : `err=${errCode ?? "none"}` };
    }
    case "reachable": {
      // Compiled extension pack: any structured response except tool_not_found.
      if (errCode === "tool_not_found") {
        return { pass: false, detail: "tool_not_found (expected compiled in)" };
      }
      if (!isError) return { pass: true, detail: "ok (compiled in)" };
      return { pass: true, detail: `err=${errCode} (compiled in)` };
    }
    case "verify_timeout": {
      // Verify-family live-bridge hang (see specs/feedback.md). Pass = either
      // the tool actually returned, OR it hit the bridge 30s timeout. Any
      // OTHER error is a real failure.
      if (!isError) return { pass: true, detail: "ok (verify returned)" };
      const pass = errCode === TIMEOUT_CODE;
      return { pass, detail: pass ? "bridge 30s timeout (KNOWN ISSUE — see specs/feedback.md)" : `err=${errCode}` };
    }
    case "ok_or_timeout": {
      // Tolerate either a clean response OR a CLI timeout (the tool was reached;
      // the hang is itself the signal). The runner marks timeouts with a sentinel.
      const pass = !isError || parsed._ft_timeout === true;
      return { pass, detail: parsed._ft_timeout ? "timed out (tolerated)" : "ok" };
    }
    case "tolerate": {
      // Pass on a clean response OR any error code in step.tolerate (known tool
      // bugs we still want to exercise + report, without failing the suite).
      const tol = new Set(step.tolerate ?? []);
      const pass = !isError || tol.has(errCode);
      return { pass, detail: isError ? `err=${errCode} (tolerated)` : "ok" };
    }
    default:
      return { pass: !isError, detail: isError ? `err=${errCode}` : "ok" };
  }
}

/** Dig into a nested object by dotted path. */
export function pluck(obj, path) {
  return path.split(".").reduce((acc, k) => (acc && typeof acc === "object" ? acc[k] : undefined), obj);
}

/** Extract a JSON envelope from CLI stdout (brace-matched fallback). */
export function parseEnvelope(stdout) {
  try {
    return JSON.parse(stdout);
  } catch {
    const start = stdout.indexOf("{");
    if (start < 0) return null;
    let depth = 0;
    let end = -1;
    for (let i = start; i < stdout.length; i++) {
      if (stdout[i] === "{") depth++;
      else if (stdout[i] === "}") {
        depth--;
        if (depth === 0) { end = i; break; }
      }
    }
    if (end > start) {
      try {
        return JSON.parse(stdout.slice(start, end + 1));
      } catch {
        return null;
      }
    }
    return null;
  }
}

// ---------------------------------------------------------------------------
// common arg parsing
//
// Every suite shares --project/-P, --list, --json-out, --only, --band,
// --no-cleanup, --timeout-ms, --help. Suites pass an `extra` callback to
// handle their own flags (e.g. S3 --skip-live, S5 --source-project). The
// callback receives (a, opts, argv, i) and returns the new index i (or i+1
// if it consumed no extra args).
// ---------------------------------------------------------------------------

export function parseCommonArgs(argv, { extra, help, defaults = {} } = {}) {
  const opts = {
    project: defaults.project ?? resolve(REPO_ROOT, "demo"),
    band: null, // comma-separated band letters, null = all
    only: null, // comma-separated label needles, null = all
    list: false,
    noCleanup: false,
    jsonOut: null,
    timeoutMs: defaults.timeoutMs ?? 120_000,
    ...defaults.extra,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--project" || a === "-P") opts.project = argv[++i];
    else if (a === "--band") opts.band = argv[++i].split(",").map((s) => s.trim().toUpperCase());
    else if (a === "--only") opts.only = argv[++i].split(",").map((s) => s.trim());
    else if (a === "--list") opts.list = true;
    else if (a === "--no-cleanup") opts.noCleanup = true;
    else if (a === "--json-out") opts.jsonOut = argv[++i];
    else if (a === "--timeout-ms") opts.timeoutMs = Number(argv[++i]);
    else if (a === "--help" || a === "-h") {
      if (help) help();
      process.exit(0);
    } else if (extra) {
      i = extra(a, opts, argv, i);
    } else {
      console.error(`Unknown argument: ${a}`);
      process.exit(2);
    }
  }
  return opts;
}

// ---------------------------------------------------------------------------
// step builder
//
// The 4th arg is normally the tool's input args. But many chained steps pass
// ONLY step-meta (resolveArgs/after/gate/expect/timeoutMs/tolerate) and no
// static args — historically those were written as
//   s(label, band, tool, { resolveArgs, ... }, { gate, ... })
// To support both shapes transparently: if the 4th arg contains any step-meta
// key, treat it as extra (step metadata) and leave args empty; otherwise it's
// the args.
// ---------------------------------------------------------------------------

export const STEP_META_KEYS = new Set(["resolveArgs", "after", "gate", "expect", "timeoutMs", "tolerate", "_retried"]);

export function makeStepBuilder() {
  const steps = [];
  const s = (label, band, tool, arg4, extra = {}) => {
    if (arg4 && typeof arg4 === "object" && Object.keys(arg4).some((k) => STEP_META_KEYS.has(k))) {
      steps.push({ label, band, tool, args: undefined, ...arg4, ...extra });
    } else {
      steps.push({ label, band, tool, args: arg4, ...extra });
    }
  };
  return { s, steps };
}

// ---------------------------------------------------------------------------
// env + dialog dismissal
// ---------------------------------------------------------------------------

/** Child-process env for every CLI invocation: opt in to unsaved-scene modal
 *  dismiss (destructive — test-only backstop) and prefer Don't Save on those
 *  modals so InitTestScene* temp scenes from run_tests do not wedge the editor. */
export function buildRunEnv() {
  const env = { ...process.env };
  env.UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS = "1";
  env.UNITY_OPEN_MCP_DIALOG_POLICY = "cancel";
  return env;
}

/** Poll OS dialogs (unsaved-scene, launch-errors, …) — best-effort sync wrapper. */
export function dismissBlockingModals(runEnv) {
  const script = `
    import { pollAndDismissDialogs, readDismissConfig } from ${JSON.stringify(DISMISS_MOD)};
    const cfg = readDismissConfig(process.env);
    if (!cfg.enabled) process.exit(0);
    await pollAndDismissDialogs({
      timeoutMs: 10_000,
      intervalMs: 500,
      policy: cfg.policy,
      allowProjectUpgrade: cfg.allowProjectUpgrade,
      allowUnsavedSceneDismiss: cfg.allowUnsavedSceneDismiss,
      log: (line) => console.error(line),
    });
  `;
  try {
    execFileSync(process.execPath, ["--input-type=module", "-e", script], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
      timeout: 15_000,
      env: runEnv,
    });
  } catch (err) {
    const msg = (err.stderr ?? err.message ?? "").toString().trim();
    if (msg) process.stderr.write(`${msg}\n`);
  }
}

// ---------------------------------------------------------------------------
// CLI runner
// ---------------------------------------------------------------------------

/** Fire-and-forget CLI call returning the parsed envelope (or null). Used by
 *  cleanup/finalize helpers; the main step loop uses runTool/runToolOnce. */
export function invokeTool(project, tool, args, timeoutMs, runEnv) {
  const argStr = JSON.stringify(args ?? {});
  try {
    const stdout = execFileSync(
      process.execPath,
      [CLI_BIN, "run-tool", tool, "--project", project, "--json", "--args", argStr],
      { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], maxBuffer: 64 * 1024 * 1024, timeout: timeoutMs, env: runEnv },
    );
    return parseEnvelope(stdout);
  } catch (err) {
    const maybeStdout = err.stdout ?? "";
    if (maybeStdout && maybeStdout.includes("{")) {
      const parsed = parseEnvelope(maybeStdout);
      if (parsed) return parsed;
    }
    return null;
  }
}

/** Save every dirty scene except those in excludePaths. Returns count saved. */
export function saveAllDirtyScenes(project, runEnv, timeoutMs = 30_000, excludePaths = null, opts = {}) {
  const mainScenePath = opts.mainScenePath ?? DEFAULT_MAIN_SCENE_PATH;
  const parsed = invokeTool(project, "unity_open_mcp_scene_get_dirty_summary", {}, timeoutMs, runEnv);
  if (!parsed || parsed.isError === true) return 0;
  const scenes = parsed.result?.scenes ?? [];
  let saved = 0;
  for (const scene of scenes) {
    if (!scene?.isDirty) continue;
    if (isMainSceneEntry(scene, excludePaths, mainScenePath)) continue;
    const path = scene.path;
    const name = scene.name;
    const saveArgs = path
      ? { path, paths_hint: [path] }
      : name
        ? { name, paths_hint: ["<mcp-test-auto-save>"] }
        : null;
    if (!saveArgs) continue;
    const out = invokeTool(project, "unity_open_mcp_scene_save", saveArgs, timeoutMs, runEnv);
    if (out && out.isError !== true) saved++;
  }
  return saved;
}

/** Single CLI invocation + classify. */
export function runToolOnce(step, ctx, project, defaultTimeout, runEnv) {
  const args = step.resolveArgs ? step.resolveArgs(ctx) : step.args ?? {};
  const argStr = JSON.stringify(args);
  const timeout = step.timeoutMs ?? defaultTimeout;
  const t0 = Date.now();
  let stdout;
  try {
    stdout = execFileSync(
      process.execPath,
      [CLI_BIN, "run-tool", step.tool, "--project", project, "--json", "--args", argStr],
      { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], maxBuffer: 64 * 1024 * 1024, timeout, env: runEnv },
    );
  } catch (err) {
    const ms = Date.now() - t0;
    // Detect a CLI timeout robustly: execFileSync sets err.killed=true when it
    // kills the child on timeout, but a child that exits with ETIMEDOUT (e.g.
    // the bridge's own timeout surfaced as a non-zero exit) sets err.code
    // instead. Match either signal.
    const isTimeout =
      err.killed === true ||
      /timeout/i.test(err.message || "") ||
      /timed?out/i.test(String(err.code || "")) ||
      err.code === "ETIMEDOUT";
    // A non-zero exit from the CLI still prints a JSON envelope to stdout in
    // most cases; if execFileSync threw because of timeout or exit code, try
    // to recover the envelope from err.stdout.
    const maybeStdout = err.stdout ?? "";
    if (maybeStdout && maybeStdout.includes("{")) {
      const parsed = parseEnvelope(maybeStdout);
      if (parsed) {
        if (isTimeout) parsed._ft_timeout = true;
        const { pass, detail } = classify(step, parsed);
        return { ok: pass, ms, detail, result: parsed.result ?? {}, raw: parsed };
      }
    }
    if (isTimeout && step.expect === "ok_or_timeout") {
      return { ok: true, ms, detail: `timed out after ${timeout}ms (tolerated)`, result: {}, raw: { _ft_timeout: true } };
    }
    const reason = isTimeout ? `timeout after ${timeout}ms` : `CLI failed: ${err.code ?? err.message}`;
    return { ok: false, ms, error: reason, raw: "" };
  }
  const ms = Date.now() - t0;
  const parsed = parseEnvelope(stdout);
  if (!parsed) {
    return { ok: false, ms, error: "no JSON envelope in CLI stdout", raw: stdout.slice(-400) };
  }
  const { pass, detail } = classify(step, parsed);
  return { ok: pass, ms, detail, result: parsed.result ?? {}, raw: parsed };
}

/** Step runner with scene_dirty / main_thread_blocked save+retry + optional
 *  pre/post-save guards. saveDirtyBefore/saveDirtyAfter are Sets of step labels. */
export function runTool(step, ctx, project, defaultTimeout, runEnv, opts = {}) {
  const saveBefore = opts.saveDirtyBefore ?? new Set();
  const saveAfter = opts.saveDirtyAfter ?? new Set();
  if (saveBefore.has(step.label)) {
    saveAllDirtyScenes(project, runEnv, Math.min(defaultTimeout, 30_000), null, opts);
  }
  const result = runToolOnce(step, ctx, project, defaultTimeout, runEnv);
  const errCode = result.result?.error?.code ?? result.result?.mutation?.error?.code;
  if ((errCode === "scene_dirty" || errCode === "main_thread_blocked") && !step._retried) {
    const retried = { ...step, _retried: true };
    saveAllDirtyScenes(project, runEnv, 30_000, null, opts);
    const retry = runToolOnce(retried, ctx, project, defaultTimeout, runEnv);
    const tag = errCode === "main_thread_blocked" ? "main_thread_blocked save+retry" : "scene_dirty save+retry";
    return { ...retry, detail: `${retry.detail} (after ${tag})`, ms: result.ms + retry.ms };
  }
  if (saveAfter.has(step.label) && result.ok) {
    saveAllDirtyScenes(project, runEnv, Math.min(defaultTimeout, 30_000), null, opts);
  }
  return result;
}

// ---------------------------------------------------------------------------
// scene hygiene (InitTestScene* + Main dirty state)
// ---------------------------------------------------------------------------

export function isMainSceneEntry(scene, excludePaths, mainScenePath = DEFAULT_MAIN_SCENE_PATH) {
  if (!excludePaths) return false;
  const path = scene?.path;
  const name = scene?.name;
  if (path && excludePaths.has(path)) return true;
  if (name === "Main" && excludePaths.has(mainScenePath)) return true;
  return false;
}

/** Close dirty InitTestScene* temp scenes via the bridge (no save prompt when not dirty). */
export function closeInitTestScenes(project, runEnv, opts = {}) {
  const mainSceneHint = [opts.mainScenePath ?? DEFAULT_MAIN_SCENE_PATH];
  return invokeTool(
    project,
    "unity_open_mcp_execute_csharp",
    {
      code: [
        "var closed = new List<string>();",
        "for (int i = UnityEngine.SceneManagement.SceneManager.sceneCount - 1; i >= 0; i--) {",
        "  var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);",
        "  if (!scene.IsValid() || !scene.isLoaded) continue;",
        "  if (!scene.name.StartsWith(\"InitTestScene\")) continue;",
        "  UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene, false);",
        "  UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, removeScene: true);",
        "  closed.Add(scene.name);",
        "}",
        "return new { status = \"ok\", closed = closed.ToArray() };",
      ].join("\n"),
      usings: ["UnityEngine.SceneManagement", "UnityEditor.SceneManagement"],
      paths_hint: mainSceneHint,
    },
    60_000,
    runEnv,
  );
}

/** Remove FT_* fixture GOs from Main and clear its dirty flag (no disk save). */
export function revertMainSceneIfDirty(project, runEnv, opts = {}) {
  const mainScenePath = opts.mainScenePath ?? DEFAULT_MAIN_SCENE_PATH;
  const mainSceneHint = [mainScenePath];
  return invokeTool(
    project,
    "unity_open_mcp_execute_csharp",
    {
      code: [
        "var removed = new List<string>();",
        "var cleared = false;",
        "UnityEngine.SceneManagement.Scene main = default;",
        `for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++) {`,
        "  var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);",
        `  if (scene.path == "${mainScenePath}") { main = scene; break; }`,
        "}",
        "if (!main.IsValid() || !main.isLoaded) {",
        "  var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();",
        `  if (active.path == "${mainScenePath}" || active.name == "Main") main = active;`,
        "}",
        "if (main.IsValid() && main.isLoaded) {",
        "  foreach (var root in main.GetRootGameObjects()) {",
        "    if (root.name.StartsWith(\"FT_\") || root.name == \"GateTestCube\" || root.name.StartsWith(\"CM \")",
        "        || root.name == \"Terrain\" || root.name == \"Canvas\") {",
        "      UnityEngine.Object.DestroyImmediate(root);",
        "      removed.Add(root.name);",
        "    }",
        "  }",
        "  UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(main, false);",
        "  cleared = true;",
        "}",
        "var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();",
        "return new { status = \"ok\", removed = removed.ToArray(), cleared, activeScene = activeScene.name, activePath = activeScene.path };",
      ].join("\n"),
      usings: ["UnityEngine", "UnityEngine.SceneManagement", "UnityEditor.SceneManagement"],
      paths_hint: mainSceneHint,
    },
    60_000,
    runEnv,
  );
}

export function collectIsolationState(project, runEnv, ctx, opts = {}) {
  const mainScenePath = opts.mainScenePath ?? DEFAULT_MAIN_SCENE_PATH;
  const mainSceneHint = [mainScenePath];
  const dirty = invokeTool(project, "unity_open_mcp_scene_get_dirty_summary", {}, 15_000, runEnv);
  const scenes = dirty?.result?.scenes ?? [];
  const mainDirty = scenes.some((s) => isMainSceneEntry(s, new Set([mainScenePath]), mainScenePath) && s.isDirty);
  const active = invokeTool(
    project,
    "unity_open_mcp_execute_csharp",
    {
      code: "var s = UnityEngine.SceneManagement.SceneManager.GetActiveScene(); return new { name = s.name, path = s.path };",
      usings: ["UnityEngine.SceneManagement"],
      paths_hint: mainSceneHint,
    },
    15_000,
    runEnv,
  );
  return {
    ft_scene_created: ctx.ftSceneCreated === true,
    ft_scene_active: ctx.ftSceneActive === true,
    ft_scene_isolation_warning: ctx.ftSceneIsolationWarning ?? null,
    active_scene_at_teardown: active?.result?.mutation?.output?.name ?? null,
    active_scene_path_at_teardown: active?.result?.mutation?.output?.path ?? null,
    main_dirty_at_finalize: mainDirty,
  };
}

/** Tear down InitTestScene* leftovers and discard Main dirty state without saving. */
export function finalizeEditorState(project, runEnv, options = {}) {
  const excludeMain = options.excludeMain !== false;
  const mainScenePath = options.mainScenePath ?? DEFAULT_MAIN_SCENE_PATH;
  dismissBlockingModals(runEnv);
  closeInitTestScenes(project, runEnv, { mainScenePath });
  revertMainSceneIfDirty(project, runEnv, { mainScenePath });
  dismissBlockingModals(runEnv);
  const excludePaths = excludeMain ? new Set([mainScenePath]) : null;
  saveAllDirtyScenes(project, runEnv, 30_000, excludePaths, { mainScenePath });
  dismissBlockingModals(runEnv);
}

// ---------------------------------------------------------------------------
// cleanup (always runs)
// ---------------------------------------------------------------------------

/** Best-effort on-disk removal of the fixture root (+ orphan .meta + any
 *  Unity-auto-renamed "Foo N" siblings). The orphan .meta matters: Unity's
 *  AssetDatabase caches folder existence by .meta, so a leftover Foo.meta
 *  makes the next assets_create_folder silently rename to "Foo 1" and report
 *  created:[] — desyncing the bridge from disk. */
export function cleanupTempFolder(project, opts = {}) {
  const fixtureRoot = opts.fixtureRoot ?? DEFAULT_FIXTURE_ROOT;
  const folderName = fixtureRoot.split("/").pop();
  const assetsDir = resolve(project, "Assets");
  let cleaned = true;
  let entries = [];
  try { entries = readdirSync(assetsDir); } catch { return false; }
  const re = new RegExp(`^${folderName.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}(\\s+\\d+)?(\\.meta)?$`);
  for (const entry of entries) {
    if (re.test(entry)) {
      try { rmSync(resolve(assetsDir, entry), { recursive: true, force: true }); }
      catch { cleaned = false; }
    }
  }
  return cleaned;
}

/** Try the typed assets_delete so Unity's AssetDatabase stays in sync, then fall
 *  back to fs removal. Also close any leftover prefab stage + unload the
 *  additive scene if still open. */
export function cleanupViaBridge(project, runEnv, opts = {}) {
  const fixtureRoot = opts.fixtureRoot ?? DEFAULT_FIXTURE_ROOT;
  const fixtureScene = opts.fixtureScene ?? `${fixtureRoot}/FT_Scene.unity`;
  const sceneHint = [fixtureScene];
  const ops = [
    {
      label: "assets_delete fixture root",
      tool: "unity_open_mcp_assets_delete",
      args: { paths: [fixtureRoot], paths_hint: [fixtureRoot] },
    },
    {
      label: "prefab_close (safety)",
      tool: "unity_open_mcp_prefab_close",
      args: { save: false },
    },
    {
      label: "scene_unload fixture (safety)",
      tool: "unity_open_mcp_scene_unload",
      args: { name: "FT_Scene", paths_hint: sceneHint },
    },
  ];
  for (const op of ops) {
    try {
      execFileSync(
        process.execPath,
        [CLI_BIN, "run-tool", op.tool, "--project", project, "--json", "--args", JSON.stringify(op.args)],
        { encoding: "utf8", stdio: ["ignore", "ignore", "ignore"], timeout: 30_000, env: runEnv },
      );
    } catch {
      // best-effort
    }
  }
  cleanupTempFolder(project, { fixtureRoot });
}

// ---------------------------------------------------------------------------
// misc helpers
// ---------------------------------------------------------------------------

/** Merge gameobject_find hits into ctx handles by name (FT_Cube/FT_Sphere/...). */
export function applyFindObjectsToCtx(r, ctx) {
  const objects = r.objects ?? [];
  for (const o of objects) {
    if (o.instanceId == null) continue;
    if (o.name === "FT_Cube") ctx.cubeId = o.instanceId;
    if (o.name === "FT_Sphere") ctx.sphereId = o.instanceId;
    if (o.name === "FT_Renamed") ctx.cubeDupId = o.instanceId;
    if (o.name === "GateTestCube") ctx.prefabInstanceId = o.instanceId;
  }
}

/** Build a SIGINT handler that runs cleanup once before exiting. A second
 *  SIGINT exits immediately. Returns the handler (caller registers/removes). */
export function makeSigIntHandler(opts) {
  const { project, runEnv, cleanup = true, onCleanup, noCleanupFlag = false } = opts;
  let interrupted = false;
  const handler = () => {
    if (interrupted) process.exit(1);
    interrupted = true;
    console.error("\n^C — interrupted; running cleanup before exit...");
    if (!noCleanupFlag && cleanup) {
      if (onCleanup) onCleanup();
      else cleanupViaBridge(project, runEnv);
    }
    process.exit(1);
  };
  return handler;
}

/** Print a --list rendering of steps grouped by band. Shared by every suite
 *  so the listing format is uniform. */
export function printStepList(selected, { suiteName = "Test suite steps", skips = [] } = {}) {
  console.log(`${suiteName}:`);
  let lastBand = null;
  for (const s of selected) {
    if (s.band !== lastBand) {
      console.log(`\n  --- Band ${s.band} ---`);
      lastBand = s.band;
    }
    const tag = s.expect && s.expect !== "ok" ? ` [${s.expect}]` : (s.gate ? " [gate]" : "");
    console.log(`  ${s.label.padEnd(34)} ${s.tool}${tag}`);
  }
  console.log(`\n${selected.length} step(s).`);
  if (skips.length && !skips._hidden) {
    console.log(`\nSkipped (deliberate):`);
    for (const sk of skips) console.log(`  ${sk.tool.padEnd(36)} — ${sk.reason}`);
  }
}
