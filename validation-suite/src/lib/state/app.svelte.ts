/**
 * Central app state (Svelte 5 runes).
 *
 * Single reactive store for the runner view: the active project +
 * profile, the loaded scenarios, the suite state, the active filters,
 * and any load warnings/errors. UI components read this through
 * `appState`; mutations go through the action methods so state +
 * persistence stay in sync.
 *
 * Status transitions + test-level rollups are computed by the
 * engine-neutral core package (`setStepStatus`, `resetTestState`);
 * this file wires those pure helpers to the backend persistence
 * (`saveSuiteState`) so a step toggle is one call.
 */

import {
  ensureTestState,
  loadScenarios,
  parseProfile,
  resetTestState as coreResetTest,
  setStepStatus,
  type RequirementLevel,
  type Scenario,
  type ScenarioLoadResult,
  type Status,
  type SuiteState,
} from "@validation-suite/core";

import * as backend from "../services/backend.ts";
import { logs } from "./logs.svelte.ts";

/** Top-level requirement-tier filters (idea.md → UI shape). */
export interface Filters {
  core: boolean;
  extended: boolean;
  optional: boolean;
  onlyIncomplete: boolean;
}

export const DEFAULT_FILTERS: Filters = {
  core: true,
  extended: true,
  optional: true,
  onlyIncomplete: false,
};

/** A stateful warning shown to the operator (e.g. incompatible shape). */
export interface AppWarning {
  title: string;
  body: string;
}

class AppState {
  // ── project + profile ──────────────────────────────────────────────────────
  activeProject = $state<string | null>(null);
  profile = $state<backend.BackendProfile | null>(null);
  /** True while a backend call is in flight (drives a top-level spinner). */
  busy = $state(false);

  // ── scenarios ──────────────────────────────────────────────────────────────
  scenarios = $state<Scenario[]>([]);
  /** Per-file structural load errors (readable, non-fatal). */
  loadErrors = $state<import("@validation-suite/core").ScenarioLoadError[]>([]);
  /** Parse/read errors from the backend (files that weren't even JSON). */
  readErrors = $state<backend.ScenarioReadError[]>([]);

  // ── state ──────────────────────────────────────────────────────────────────
  suite = $state<SuiteState | null>(null);
  /** Non-blocking warning (incompatible/malformed state) with reset guidance. */
  warning = $state<AppWarning | null>(null);
  /** Fatal init error (e.g. profile missing). */
  fatal = $state<string | null>(null);

  // ── ui ─────────────────────────────────────────────────────────────────────
  selectedScenarioId = $state<string | null>(null);
  filters = $state<Filters>({ ...DEFAULT_FILTERS });

  // ── derived ────────────────────────────────────────────────────────────────
  /** Scenarios grouped by milestone, then ordered (core ordering applied). */
  get milestones(): { milestone: string; scenarios: Scenario[] }[] {
    const groups = new Map<string, Scenario[]>();
    for (const s of this.scenarios) {
      const list = groups.get(s.milestone) ?? [];
      list.push(s);
      groups.set(s.milestone, list);
    }
    return [...groups.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([milestone, scenarios]) => ({
        milestone,
        scenarios: [...scenarios].sort(
          (a, b) => a.order - b.order || a.id.localeCompare(b.id),
        ),
      }));
  }

  /** Scenarios passing the active filters. */
  get filteredScenarios(): Scenario[] {
    return this.scenarios.filter((s) => {
      if (s.requirementLevel === "required-core" && !this.filters.core) return false;
      if (s.requirementLevel === "required-extended" && !this.filters.extended) return false;
      if (s.requirementLevel === "optional" && !this.filters.optional) return false;
      if (this.filters.onlyIncomplete) {
        const status = this.suite?.tests[s.id]?.status ?? "awaiting";
        if (status === "done") return false;
      }
      return true;
    });
  }

  /** The currently selected scenario, or null. */
  get selected(): Scenario | null {
    if (!this.selectedScenarioId) return null;
    return this.scenarios.find((s) => s.id === this.selectedScenarioId) ?? null;
  }

  /** Read-only status for a scenario from the suite state. */
  statusOf(scenarioId: string): Status {
    return this.suite?.tests[scenarioId]?.status ?? "awaiting";
  }

  // ── lifecycle ──────────────────────────────────────────────────────────────

  /** Bootstrap on app open: load profile + last project pointer. */
  async init(): Promise<void> {
    this.busy = true;
    try {
      this.profile = await backend.getEngineProfile();
      logs.log(`engine profile: ${this.profile.displayName}`);
    } catch (e) {
      this.fatal = `Could not load the engine profile: ${String(e)}`;
      logs.error(`profile load failed: ${String(e)}`);
      this.busy = false;
      return;
    }
    try {
      const cfg = await backend.getLastProject();
      if (cfg.lastProjectPath) {
        // Re-validate the remembered project before trusting it.
        const check = await backend.selectProject(cfg.lastProjectPath);
        if (check.valid) {
          this.activeProject = check.path;
          logs.log(`restored last project: ${check.path}`);
          await this.loadAll();
        } else if (check.reason) {
          logs.log(`last project no longer valid: ${check.reason}`);
        }
      }
    } catch (e) {
      // A stale pointer is non-fatal; the operator just re-picks.
      logs.log(`last-project restore skipped: ${String(e)}`);
    } finally {
      this.busy = false;
    }
  }

  /** Pick a project via the native folder dialog and scope the app to it. */
  async pickProject(): Promise<void> {
    this.busy = true;
    try {
      const { open } = await import("@tauri-apps/plugin-dialog");
      const picked = await open({
        directory: true,
        multiple: false,
        title: "Select Unity project folder",
      });
      if (typeof picked !== "string") {
        this.busy = false;
        return;
      }
      const check = await backend.selectProject(picked);
      if (!check.valid) {
        this.warning = {
          title: "Not a valid project folder",
          body: check.reason ?? "The selected folder is not a recognized project.",
        };
        logs.error(`project rejected: ${check.reason ?? "unknown reason"}`);
        this.busy = false;
        return;
      }
      this.activeProject = check.path;
      this.warning = null;
      logs.log(`opened project: ${check.path}`);
      await this.loadAll();
    } catch (e) {
      this.warning = { title: "Could not open project", body: String(e) };
      logs.error(`project open failed: ${String(e)}`);
    } finally {
      this.busy = false;
    }
  }

  /** Load scenarios + suite state for the active project. */
  async loadAll(): Promise<void> {
    if (!this.activeProject) return;
    this.busy = true;
    try {
      await this.loadScenarios();
      await this.loadSuite();
    } finally {
      this.busy = false;
    }
  }

  /** Read + validate scenarios for the active engine. */
  private async loadScenarios(): Promise<void> {
    const read = await backend.readScenarios();
    this.readErrors = read.errors;
    if (read.errors.length > 0) {
      for (const err of read.errors) logs.error(`scenario read ${err.source}: ${err.message}`);
    }
    // Validate structure with the engine-neutral core loader.
    const result: ScenarioLoadResult = loadScenarios(read.files);
    this.scenarios = result.scenarios;
    this.loadErrors = result.errors;
    if (result.errors.length > 0) {
      for (const err of result.errors) logs.error(`scenario invalid ${err.source}: ${err.message}`);
    }
    logs.log(`loaded ${result.scenarios.length} scenario(s) (${result.errors.length} invalid)`);
    // Auto-select the first scenario if nothing is selected.
    if (!this.selectedScenarioId && this.scenarios.length > 0) {
      this.selectedScenarioId = this.scenarios[0].id;
    }
  }

  /** Load the suite state, applying the warn+reset policy on bad shapes. */
  private async loadSuite(): Promise<void> {
    if (!this.profile) return;
    const outcome = await backend.loadSuiteState();
    if (outcome.kind === "ok") {
      this.suite = outcome.state ?? null;
      this.warning = null;
      logs.log("suite state loaded");
      this.reconcileState();
    } else if (outcome.kind === "missing") {
      this.suite = outcome.state ?? null;
      this.warning = null;
      logs.log("no prior suite state — starting fresh");
      this.reconcileState();
    } else if (outcome.kind === "malformed") {
      this.suite = null;
      this.warning = {
        title: "Local suite state is unreadable",
        body: `${outcome.reason ?? "The state file is corrupt."} A backup was saved. Reset local Validation Suite data to continue.`,
      };
      logs.error(`state malformed: ${outcome.reason ?? "unknown"}`);
    } else if (outcome.kind === "incompatible") {
      this.suite = null;
      this.warning = {
        title: "Local suite state is incompatible",
        body:
          outcome.reason ??
          `State version ${outcome.foundVersion} is not supported. Reset local Validation Suite data to continue.`,
      };
      logs.error(`state incompatible (found v${outcome.foundVersion})`);
    }
  }

  /** Ensure every loaded scenario has a state entry (adds new steps). */
  private reconcileState(): void {
    if (!this.suite) return;
    let next = this.suite;
    for (const scenario of this.scenarios) {
      next = ensureTestState(next, scenario);
    }
    this.suite = next;
    void this.persist();
  }

  /** Persist the current suite state (fire-and-forget; logged on error). */
  private async persist(): Promise<void> {
    if (!this.suite) return;
    try {
      await backend.saveSuiteState(this.suite);
    } catch (e) {
      logs.error(`state save failed: ${String(e)}`);
    }
  }

  // ── mutations ──────────────────────────────────────────────────────────────

  /** Toggle a step's status; recomputes the test rollup + persists. */
  async setStep(scenario: Scenario, stepId: string, status: Status): Promise<void> {
    if (!this.suite) return;
    this.suite = setStepStatus(this.suite, scenario, stepId, status);
    logs.log(`${scenario.id} › ${stepId} → ${status}`);
    await this.persist();
  }

  /** Reset a whole test (all steps awaiting; payloads/manifests cleared). */
  async resetTest(scenario: Scenario): Promise<void> {
    if (!this.suite) return;
    this.suite = coreResetTest(this.suite, scenario);
    logs.log(`reset test ${scenario.id}`);
    await this.persist();
  }

  /** Hard reset: delete the state file entirely and re-seed fresh. */
  async resetAll(): Promise<void> {
    this.busy = true;
    try {
      await backend.resetSuiteState();
      this.warning = null;
      logs.log("reset local suite state");
      await this.loadSuite();
    } finally {
      this.busy = false;
    }
  }

  select(scenarioId: string): void {
    this.selectedScenarioId = scenarioId;
  }

  toggleFilter(key: keyof Filters): void {
    this.filters = { ...this.filters, [key]: !this.filters[key] };
  }

  /** Copy arbitrary text to the clipboard (agent_prompt payload, etc.). */
  async copy(text: string): Promise<boolean> {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      return false;
    }
  }
}

/** Singleton app state. Imported by every view + component. */
export const app = new AppState();

/** Requirement-tier badge label for the UI. */
export function tierLabel(level: RequirementLevel): string {
  switch (level) {
    case "required-core":
      return "Required · core";
    case "required-extended":
      return "Required · extended";
    case "optional":
      return "Optional";
  }
}
