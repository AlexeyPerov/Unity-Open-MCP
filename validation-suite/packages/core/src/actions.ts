/**
 * Setup-action runner (phase-2 deliverable: action executor).
 *
 * The runner is engine-neutral orchestration: it walks a step's ordered
 * actions, expands placeholders, and delegates each verb to a backend
 * capability (`ActionBackend`). The backend owns all side effects:
 *  - `fs_*` → Tauri/Rust filesystem ops with project-root sandboxing.
 *  - `mcp_tool` → MCP CLI subprocess (`unity-open-mcp run-tool`).
 *  - `manual` → resolved by the UI (a confirmation gate); the runner
 *    just records an info log so the action log shows the instruction.
 *
 * Reset (step / all) walks the manifests recorded by setup in reverse
 * order and delegates cleanup to the backend. Missing/incomplete
 * manifest metadata is a warning, not a crash (phase-2 task 6).
 *
 * The runner never touches disk itself — it is pure with respect to the
 * backend it's given, which keeps it unit-testable with a stub backend
 * and reusable across engines (idea.md → Multi-engine reuse strategy).
 */

import type {
  ActionLogLine,
  ActionResult,
  EngineProfile,
  ManifestEntry,
  ManifestRef,
  ResetResult,
  Scenario,
  ScenarioStep,
  SetupAction,
  StepManifest,
  StepRunResult,
} from "./types.ts";
import { expandValue } from "./placeholders.ts";

// ─────────────────────────────────────────────────────────────────────────────
// Backend capability interface
// ─────────────────────────────────────────────────────────────────────────────

/** Resolved absolute paths handed to each backend op. */
export interface ActionContext {
  /** Absolute selected project root. */
  projectRoot: string;
  /** Absolute fixture root for the active test (`<project>/Assets/_ValidationSuite/<id>/`). */
  fixtureRoot: string;
  /** The active engine profile (companions, CLI binary, tool prefix). */
  profile: EngineProfile;
}

/** Arguments for `fs_copy`. Source + dest are post-expansion, project-relative. */
export interface FsCopyArgs {
  /** Project-relative source path (file or directory). */
  from: string;
  /** Project-relative destination path. */
  to: string;
}

/** Arguments for `fs_patch`. `path` is project-relative; patches are expanded. */
export interface FsPatchArgs {
  path: string;
  patches: Array<{ op: string; match?: string; replace?: string; insert?: string }>;
  /**
   * Reset path: when set, the op writes this exact content back to
   * `path` (snapshot restore) instead of applying `patches`. This lets
   * reset reuse the same backend op without a separate restore verb.
   */
  snapshotOverride?: string;
}

/** Arguments for `fs_delete`. Paths are project-relative. */
export interface FsDeleteArgs {
  paths: string[];
}

/** Arguments for `mcp_tool`. `tool` is the full MCP tool name. */
export interface McpToolArgs {
  tool: string;
  /** Free-form args object passed to the tool (already expanded). */
  args?: Record<string, unknown>;
  /** Optional timeout override in ms. */
  timeoutMs?: number;
}

/**
 * The capability surface the runner needs from the host (the Tauri
 * backend in production; a stub in tests). Each op returns an
 * {@link ActionResult} so the runner can forward logs + manifest entries
 * to the UI uniformly. Ops raise on hard failure; the runner catches and
 * converts to a failed {@link ActionResult}.
 */
export interface ActionBackend {
  /** Copy a file or directory tree, tracking companions (e.g. `.meta`). */
  fsCopy(args: FsCopyArgs, ctx: ActionContext): Promise<ActionResult>;
  /** Apply deterministic patches; snapshots the pre-patch file. */
  fsPatch(args: FsPatchArgs, ctx: ActionContext): Promise<ActionResult>;
  /** Delete manifest-listed paths (used by reset). */
  fsDelete(args: FsDeleteArgs, ctx: ActionContext): Promise<ActionResult>;
  /** Invoke an MCP tool via the engine CLI subprocess. */
  mcpTool(args: McpToolArgs, ctx: ActionContext): Promise<ActionResult>;
  /**
   * Persist a step manifest and return its id (stored in
   * `manifestRefs[stepId]`). Returns `null` when there are no entries.
   */
  saveManifest(manifest: StepManifest): Promise<ManifestRef>;
  /** Load a previously-saved manifest by id (best-effort; may return null). */
  loadManifest(id: ManifestRef): Promise<StepManifest | null>;
  /** Delete a saved manifest blob (after reset consumes it). */
  deleteManifest(id: ManifestRef): Promise<void>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Run
// ─────────────────────────────────────────────────────────────────────────────

/** Coerce an unknown to a string with a readable fallback label. */
function asStringField(action: unknown, field: string, label: string): string {
  const v = (action as Record<string, unknown> | undefined)?.[field];
  return typeof v === "string" ? v : "";
}

/** Build a failed result from a thrown error. */
function failed(verb: string, err: unknown, summary: string): ActionResult {
  const message = err instanceof Error ? err.message : String(err);
  return {
    ok: false,
    summary,
    logs: [{ level: "error", message: `${verb} failed: ${message}`, snippet: undefined }],
    entries: [],
  };
}

/**
 * Execute a single expanded action. Returns the result; never throws
 * (failures become `ok:false` results so the caller can log + stop).
 */
async function runOne(
  action: SetupAction,
  ctx: ActionContext,
  backend: ActionBackend,
  scenarioId: string,
  stepId: string,
): Promise<ActionResult> {
  switch (action.action) {
    case "fs_copy": {
      const from = asStringField(action, "from", "from");
      const to = asStringField(action, "to", "to");
      if (!from || !to) {
        return failed(
          "fs_copy",
          new Error('fs_copy requires "from" and "to" string params.'),
          "fs_copy skipped",
        );
      }
      try {
        return await backend.fsCopy({ from, to }, ctx);
      } catch (e) {
        return failed("fs_copy", e, `fs_copy ${from} → ${to} failed`);
      }
    }
    case "fs_patch": {
      const path = asStringField(action, "path", "path");
      const patches = (action as { patches?: unknown }).patches;
      if (!path) {
        return failed("fs_patch", new Error('fs_patch requires a "path" string.'), "fs_patch skipped");
      }
      if (!Array.isArray(patches)) {
        return failed("fs_patch", new Error('fs_patch requires a "patches" array.'), "fs_patch skipped");
      }
      try {
        return await backend.fsPatch({ path, patches }, ctx);
      } catch (e) {
        return failed("fs_patch", e, `fs_patch ${path} failed`);
      }
    }
    case "fs_delete": {
      const rawPaths = (action as { paths?: unknown }).paths;
      if (!Array.isArray(rawPaths)) {
        return failed("fs_delete", new Error('fs_delete requires a "paths" array.'), "fs_delete skipped");
      }
      const paths = rawPaths.filter((p): p is string => typeof p === "string");
      try {
        return await backend.fsDelete({ paths }, ctx);
      } catch (e) {
        return failed("fs_delete", e, `fs_delete failed`);
      }
    }
    case "mcp_tool": {
      const tool = asStringField(action, "tool", "tool");
      if (!tool) {
        return failed("mcp_tool", new Error('mcp_tool requires a "tool" name.'), "mcp_tool skipped");
      }
      const args = (action as { args?: Record<string, unknown> }).args;
      const timeoutMs = (action as { timeoutMs?: number }).timeoutMs;
      try {
        return await backend.mcpTool({ tool, args, timeoutMs }, ctx);
      } catch (e) {
        return failed("mcp_tool", e, `mcp_tool ${tool} failed`);
      }
    }
    case "manual": {
      // Manual actions are a UI confirmation gate; the runner just logs
      // the instruction so the action log explains what the operator did.
      const note = asStringField(action, "note", "note");
      const message = note
        ? `Manual step: ${note}`
        : "Manual step (operator action).";
      return {
        ok: true,
        summary: "manual gate recorded",
        logs: [{ level: "info", message }],
        entries: [],
      };
    }
    default: {
      return failed(String((action as SetupAction).action), new Error("unknown action verb"), "unknown action");
    }
  }
}

/**
 * Run every action in a `setup` step, in order. Execution stops at the
 * first failure (setup is deterministic + ordered; a partial fixture is
 * worse than none). On success the combined manifest entries are saved
 * and the manifest id is returned for storage in `manifestRefs`.
 *
 * `reset.afterStep[stepId]` actions are NOT run here — those run during
 * reset (see {@link resetStep}). Only forward setup actions execute.
 */
export async function runStep(
  scenario: Scenario,
  step: ScenarioStep,
  ctx: ActionContext,
  backend: ActionBackend,
): Promise<StepRunResult> {
  const actions = step.actions ?? [];
  const results: ActionResult[] = [];
  const logs: ActionLogLine[] = [];
  const entries: ManifestEntry[] = [];
  let ok = true;

  for (const action of actions) {
    // Expand placeholders in the whole action object (paths, args, etc.).
    const expanded = expandValue(action, ctx);
    const result = await runOne(expanded, ctx, backend, scenario.id, step.id);
    results.push(result);
    logs.push(...result.logs);
    if (result.entries.length > 0) entries.push(...result.entries);
    if (!result.ok) {
      ok = false;
      break; // ordered setup: stop on first failure.
    }
  }

  let manifestId: ManifestRef = null;
  if (entries.length > 0) {
    const manifest: StepManifest = { scenarioId: scenario.id, stepId: step.id, entries };
    try {
      manifestId = await backend.saveManifest(manifest);
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e);
      logs.push({ level: "error", message: `Failed to record manifest: ${message}` });
      ok = false;
    }
  }

  return { ok, results, manifestId, logs };
}

// ─────────────────────────────────────────────────────────────────────────────
// Reset
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Revert a single step's recorded artifacts + run its declared reset
 * actions. Processing is best-effort: missing/incomplete manifest
 * metadata produces a warning and continues (phase-2 task 6). Reverse
 * order applies *within* the manifest entries; reset actions declared
 * in `reset.afterStep` run after the manifest cleanup so declared
 * deletes (e.g. drop the fixture dir) run on the reverted tree.
 */
export async function resetStep(
  scenario: Scenario,
  step: ScenarioStep,
  ctx: ActionContext,
  backend: ActionBackend,
  manifestId: ManifestRef,
): Promise<ResetResult> {
  const logs: ActionLogLine[] = [];
  const warnings: string[] = [];

  // 1. Manifest-driven revert (snapshot restore / created delete).
  if (manifestId) {
    let manifest: StepManifest | null = null;
    try {
      manifest = await backend.loadManifest(manifestId);
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e);
      warnings.push(`Could not load manifest for ${scenario.id} › ${step.id}: ${message}`);
      logs.push({ level: "warn", message: `manifest load failed: ${message}` });
    }
    if (manifest) {
      // Reverse order: later mutations unwind first.
      const reversed = [...manifest.entries].reverse();
      for (const entry of reversed) {
        try {
          if (entry.kind === "modified") {
            // Restore the pre-patch snapshot (and its companion if any).
            await backend.fsPatch(
              { path: entry.path, patches: [], snapshotOverride: entry.snapshot ?? "" },
              ctx,
            );
            logs.push({ level: "info", message: `restored snapshot: ${entry.path}` });
          } else if (entry.kind === "created") {
            const paths = entry.companionPath ? [entry.path, entry.companionPath] : [entry.path];
            await backend.fsDelete({ paths }, ctx);
            logs.push({ level: "info", message: `deleted created artifact: ${entry.path}` });
          }
          // `deleted` entries need no revert.
        } catch (e) {
          const message = e instanceof Error ? e.message : String(e);
          warnings.push(`Revert of ${entry.path} failed: ${message}`);
          logs.push({ level: "warn", message: `revert failed (${entry.path}): ${message}` });
        }
      }
      try {
        await backend.deleteManifest(manifestId);
      } catch {
        // best-effort; the manifest is already consumed.
      }
    } else {
      warnings.push(
        `No manifest recorded for ${scenario.id} › ${step.id}; skipping artifact revert.`,
      );
      logs.push({ level: "warn", message: "no manifest for this step — nothing to revert" });
    }
  }

  // 2. Declared reset actions (e.g. fs_delete {fixtureRoot}, manual bridge start).
  const declared = scenario.reset?.afterStep?.[step.id]?.actions ?? [];
  for (const action of declared) {
    const expanded = expandValue(action, ctx);
    const result = await runOne(expanded, ctx, backend, scenario.id, step.id);
    logs.push(...result.logs);
    if (!result.ok) {
      warnings.push(`Reset action ${expanded.action} for ${step.id} did not complete cleanly.`);
    }
  }

  return { ok: warnings.length === 0, warnings, logs };
}

/**
 * Reset an entire test: revert every step with a manifest in reverse
 * order (steps → manifests), then run each step's declared reset actions.
 * Used by "Reset test" in the UI.
 */
export async function resetTest(
  scenario: Scenario,
  ctx: ActionContext,
  backend: ActionBackend,
  manifestRefs: Record<string, ManifestRef>,
): Promise<ResetResult> {
  const allLogs: ActionLogLine[] = [];
  const allWarnings: string[] = [];
  // Reverse step order so later setup unwinds first.
  const steps = [...scenario.steps].reverse();
  for (const step of steps) {
    if (step.type !== "setup") continue;
    const res = await resetStep(scenario, step, ctx, backend, manifestRefs[step.id] ?? null);
    allLogs.push(...res.logs);
    allWarnings.push(...res.warnings);
  }
  return { ok: allWarnings.length === 0, warnings: allWarnings, logs: allLogs };
}
