/**
 * Tauri IPC wrappers for the Validation Suite backend.
 *
 * Thin typed wrappers over `invoke(...)`. The command names + argument
 * names mirror the `#[tauri::command]` functions in
 * `src-tauri/src/commands.rs` exactly. All structural validation of
 * scenarios lives in `@validation-suite/core`; this layer only moves
 * bytes between the UI and Rust.
 */

import { invoke } from "@tauri-apps/api/core";

// ── Backend result types (mirror src-tauri/src/schemas.rs) ────────────────────

/** Backend representation of an engine profile. */
export interface BackendProfile {
  id: string;
  displayName: string;
  mcpCliBinary: string;
  paths: {
    fixtureRoot: string;
    stateRoot: string;
    stateFile: string;
    actualsDir: string;
    exportsDir: string;
  };
  markers: { dirs: string[]; files: string[] };
  companions: { primary: string; companion: string }[];
  placeholders: string[];
  toolNamePrefix: string;
}

export interface ProjectCheck {
  valid: boolean;
  path: string;
  reason: string | null;
}

export interface AppConfig {
  lastProjectPath: string | null;
  engineProfileId: string | null;
}

/** A raw scenario document as read from disk (pre-validation). */
export interface ScenarioFile {
  source: string;
  content: unknown;
}

export interface ScenarioReadError {
  source: string;
  message: string;
}

export interface ScenarioReadResult {
  files: ScenarioFile[];
  errors: ScenarioReadError[];
}

/** Discriminated load outcome for a project's state file. */
export interface SuiteStateOutcome {
  kind: "ok" | "missing" | "malformed" | "incompatible";
  state?: import("@validation-suite/core").SuiteState;
  reason?: string;
  foundVersion?: number;
}

// ── Phase 2: action execution + manifest result types ────────────────────────

/** Mirrors `src-tauri/src/schemas.rs` `ActionLogLevel`. */
export type ActionLogLevel = "info" | "warn" | "error";

export interface ActionLogLine {
  level: ActionLogLevel;
  message: string;
  snippet?: string;
}

export interface McpResult {
  isError: boolean;
  result: unknown;
}

/** Mirrors `ActionResult` / `StepManifest` from the Rust schemas. */
export interface ActionResult {
  ok: boolean;
  summary: string;
  logs: ActionLogLine[];
  entries: import("@validation-suite/core").ManifestEntry[];
  mcp?: McpResult;
}

export interface StepManifest {
  scenarioId: string;
  stepId: string;
  entries: import("@validation-suite/core").ManifestEntry[];
}

// ── Command wrappers ─────────────────────────────────────────────────────────

/** The bundled active engine profile (v1: always `unity`). */
export function getEngineProfile(): Promise<BackendProfile> {
  return invoke<BackendProfile>("get_engine_profile");
}

/**
 * Validate + scope a project folder. On success the backend persists
 * it as the last-project pointer and remembers it as the active root.
 */
export function selectProject(path: string): Promise<ProjectCheck> {
  return invoke<ProjectCheck>("select_project", { path });
}

/** The persisted last-project pointer (may be null on first launch). */
export function getLastProject(): Promise<AppConfig> {
  return invoke<AppConfig>("get_last_project");
}

/** The currently scoped project root, or null if none selected. */
export function getActiveProject(): Promise<string | null> {
  return invoke<string | null>("get_active_project");
}

/** Discover + parse all scenario files for the active engine. */
export function readScenarios(): Promise<ScenarioReadResult> {
  return invoke<ScenarioReadResult>("read_scenarios");
}

/** Load the suite state for the active project (ok/missing/malformed/incompatible). */
export function loadSuiteState(): Promise<SuiteStateOutcome> {
  return invoke<SuiteStateOutcome>("load_suite_state");
}

/** Persist the suite state atomically for the active project. */
export function saveSuiteState(suite: import("@validation-suite/core").SuiteState): Promise<void> {
  return invoke<void>("save_suite_state", { suite });
}

/** Delete the state file for the active project (reset local data). */
export function resetSuiteState(): Promise<void> {
  return invoke<void>("reset_suite_state");
}

// ── Phase 2: action execution + manifest commands ────────────────────────────

/**
 * Resolve the fixture root for a scenario id. Returns a project-relative
 * path (the `<test-id>` token interpolated from the profile pattern). The
 * runner joins this with the project root to build its placeholder context.
 */
export function resolveFixtureRoot(scenarioId: string): Promise<string> {
  return invoke<string>("resolve_fixture_root", { scenarioId });
}

/** `fs_copy` action: copy file/dir, auto-tracking companions. */
export function fsCopyAction(from: string, to: string): Promise<ActionResult> {
  return invoke<ActionResult>("fs_copy_action", { from, to });
}

/** `fs_patch` action: apply patches (or restore a snapshot when override set). */
export function fsPatchAction(
  path: string,
  patches: unknown[],
  snapshotOverride: string | null,
): Promise<ActionResult> {
  return invoke<ActionResult>("fs_patch_action", {
    path,
    patches,
    snapshotOverride,
  });
}

/** `fs_delete` action: delete manifest-listed paths. */
export function fsDeleteAction(paths: string[]): Promise<ActionResult> {
  return invoke<ActionResult>("fs_delete_action", { paths });
}

/** `mcp_tool` action: run an MCP tool via the engine CLI. */
export function mcpToolAction(
  tool: string,
  args: unknown,
  timeoutMs: number | null,
): Promise<ActionResult> {
  return invoke<ActionResult>("mcp_tool_action", { tool, args, timeoutMs });
}

/** MCP health check (`status` or `ping`) via the engine CLI. */
export function mcpHealthAction(
  subcommand: "status" | "ping",
  timeoutMs: number | null,
): Promise<ActionResult> {
  return invoke<ActionResult>("mcp_health_action", { subcommand, timeoutMs });
}

/** Persist a step manifest blob; returns its id. */
export function saveStepManifest(
  scenarioId: string,
  stepId: string,
  entries: import("@validation-suite/core").ManifestEntry[],
): Promise<string> {
  return invoke<string>("save_step_manifest", { scenarioId, stepId, entries });
}

/** Load a step manifest blob by id (best-effort; may resolve null). */
export function loadStepManifest(id: string): Promise<StepManifest | null> {
  return invoke<StepManifest | null>("load_step_manifest", { id });
}

/** Delete a step manifest blob after reset consumes it. */
export function deleteStepManifest(id: string): Promise<void> {
  return invoke<void>("delete_step_manifest", { id });
}

// ── Phase 5: run-summary export ──────────────────────────────────────────────

/**
 * Write a run-summary export markdown body to the project's `exportsDir`
 * (`UserSettings/ValidationSuite/exports/`). Returns the project-relative
 * path the file landed at. `stem` is a short label (e.g. `m9`);
 * `generatedAt` is the ISO-8601 timestamp already baked into the body.
 */
export function saveExport(stem: string, generatedAt: string, body: string): Promise<string> {
  return invoke<string>("save_export", { stem, generatedAt, body });
}
