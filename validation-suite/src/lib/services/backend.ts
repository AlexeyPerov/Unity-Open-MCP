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
