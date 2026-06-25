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
  buildExportMarkdown,
  ensureTestState,
  loadScenarios,
  nowIso,
  parseProfile,
  resetStep as coreResetStep,
  resetTest as coreResetTest,
  resetTestState,
  runStep as coreRunStep,
  setStepStatus,
  type EngineProfile,
  type RequirementLevel,
  type Scenario,
  type ScenarioLoadResult,
  type Status,
  type SuiteState,
} from "@validation-suite/core";

import * as backend from "../services/backend.ts";
import { buildContext, tauriBackend } from "../services/action_backend.ts";
import { actionLog } from "./action_log.svelte.ts";
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

/**
 * Coarse bridge health token shown in the project bar (phase-3). Mirrors the
 * `status` field returned by the operator-only `unity_open_mcp_bridge_status`
 * MCP tool, plus `unknown` for the pre-probe state.
 */
export type BridgeStatusToken =
  | "unknown"
  | "running"
  | "compiling"
  | "stopped"
  | "dead_bridge";

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

  // ── bridge status (phase-3) ────────────────────────────────────────────────
  /** Coarse bridge health token shown in the TopBar chip. `unknown` until probed. */
  bridgeStatus = $state<BridgeStatusToken>("unknown");
  /** Operator-facing next-step hint from the last bridge_status call. */
  bridgeNextStep = $state<string | null>(null);
  /** True while a bridge_status probe is in flight (disables the chip button). */
  bridgeRefreshing = $state(false);

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
    // Action logs are project-scoped (paths/manifests belong to the old
    // project); clear them when switching projects so stale traces never
    // render against a different fixture tree.
    actionLog.clearAll();
    this.busy = true;
    try {
      await this.loadScenarios();
      await this.loadSuite();
      // Probe the bridge once on project load so the TopBar chip reflects the
      // current state. Fire-and-forget; failure leaves `unknown` + a log line.
      void this.refreshBridgeStatus();
    } finally {
      this.busy = false;
    }
  }

  /**
   * Probe bridge health via the operator-only
   * `unity_open_mcp_bridge_status` MCP tool and update the TopBar chip. The
   * tool returns a coarse `status` token (running/compiling/stopped/
   * dead_bridge) synthesized from the instance-lock classifier + one /ping;
   * it never errors on an offline bridge (stopped IS the answer), so a
   * transport failure is the only error path. Refreshes are best-effort and
   * never block the UI (busy is not set).
   */
  async refreshBridgeStatus(): Promise<void> {
    if (!this.activeProject) {
      this.bridgeStatus = "unknown";
      this.bridgeNextStep = null;
      return;
    }
    this.bridgeRefreshing = true;
    try {
      const result = await backend.mcpToolAction(
        "unity_open_mcp_bridge_status",
        {},
        15_000,
      );
      const body = (result.mcp?.result ?? {}) as {
        status?: string;
        nextStep?: string;
      };
      const token = bridgeStatusTokenFromString(body.status);
      this.bridgeStatus = token;
      this.bridgeNextStep = typeof body.nextStep === "string" ? body.nextStep : null;
      logs.log(`bridge_status: ${token}`);
    } catch (e) {
      // Transport/CLI failure (binary not on PATH, etc.) — keep `unknown` so
      // the chip stays neutral rather than falsely reporting `stopped`.
      this.bridgeStatus = "unknown";
      this.bridgeNextStep = null;
      logs.error(`bridge_status probe failed: ${String(e)}`);
    } finally {
      this.bridgeRefreshing = false;
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

  /**
   * Run a setup step's actions through the action executor (phase-2).
   * Records the manifest id in `manifestRefs`, writes the action log,
   * and marks the step done on success / blocked on failure. Execution
   * is ordered + deterministic; a partial fixture is never left behind
   * (the runner stops at the first failed action).
   */
  async runStep(scenario: Scenario, stepId: string): Promise<boolean> {
    if (!this.suite || !this.profile || !this.activeProject) return false;
    const step = scenario.steps.find((s) => s.id === stepId);
    if (!step || step.type !== "setup") return false;
    this.busy = true;
    try {
      const ctx = await buildContext(this.activeProject, this.profile as unknown as EngineProfile, scenario.id);
      const result = await coreRunStep(scenario, step, ctx, tauriBackend());
      actionLog.set(scenario.id, stepId, result.logs);
      for (const line of result.logs) {
        if (line.level === "error") logs.error(`${scenario.id} › ${stepId}: ${line.message}`);
        else logs.log(`${scenario.id} › ${stepId}: ${line.message}`);
      }
      if (result.ok) {
        // Record the manifest id so reset can revert this step.
        this.suite = setStepStatus(this.suite, scenario, stepId, "done");
        const test = this.suite.tests[scenario.id];
        if (test) test.manifestRefs[stepId] = result.manifestId;
        await this.persist();
        logs.log(`${scenario.id} › ${stepId} setup ok`);
        return true;
      }
      this.suite = setStepStatus(this.suite, scenario, stepId, "blocked");
      await this.persist();
      logs.error(`${scenario.id} › ${stepId} setup failed`);
      return false;
    } catch (e) {
      actionLog.append(scenario.id, stepId, [
        { level: "error", message: `Setup crashed: ${String(e)}` },
      ]);
      logs.error(`${scenario.id} › ${stepId} setup crashed: ${String(e)}`);
      this.suite = setStepStatus(this.suite, scenario, stepId, "blocked");
      await this.persist();
      return false;
    } finally {
      this.busy = false;
    }
  }

  /**
   * Reset a single setup step: revert its recorded manifest (reverse
   * order) + run declared reset actions, then clear the step status +
   * manifest ref. Missing manifests warn (best-effort) rather than crash
   * (phase-2 reset contract).
   */
  async resetStep(scenario: Scenario, stepId: string): Promise<boolean> {
    if (!this.suite || !this.profile || !this.activeProject) return false;
    const step = scenario.steps.find((s) => s.id === stepId);
    if (!step || step.type !== "setup") return false;
    this.busy = true;
    try {
      const manifestId = this.suite.tests[scenario.id]?.manifestRefs[stepId] ?? null;
      const ctx = await buildContext(this.activeProject, this.profile as unknown as EngineProfile, scenario.id);
      const result = await coreResetStep(scenario, step, ctx, tauriBackend(), manifestId);
      actionLog.append(scenario.id, stepId, result.logs);
      for (const line of result.logs) {
        if (line.level === "warn" || line.level === "error") {
          logs.error(`${scenario.id} › ${stepId} reset: ${line.message}`);
        } else {
          logs.log(`${scenario.id} › ${stepId} reset: ${line.message}`);
        }
      }
      for (const w of result.warnings) logs.error(`${scenario.id} › ${stepId} reset warn: ${w}`);
      // Clear the step status + manifest ref regardless of warnings.
      this.suite = setStepStatus(this.suite, scenario, stepId, "awaiting");
      const test = this.suite.tests[scenario.id];
      if (test) test.manifestRefs[stepId] = null;
      await this.persist();
      logs.log(`${scenario.id} › ${stepId} reset${result.ok ? "" : " (with warnings)"}`);
      return result.ok;
    } catch (e) {
      logs.error(`${scenario.id} › ${stepId} reset crashed: ${String(e)}`);
      return false;
    } finally {
      this.busy = false;
    }
  }

  /** Reset a whole test (revert setup steps, then clear state). */
  async resetTest(scenario: Scenario): Promise<void> {
    if (!this.suite || !this.profile || !this.activeProject) return;
    this.busy = true;
    try {
      const refs = this.suite.tests[scenario.id]?.manifestRefs ?? {};
      const ctx = await buildContext(this.activeProject, this.profile as unknown as EngineProfile, scenario.id);
      const result = await coreResetTest(scenario, ctx, tauriBackend(), refs);
      for (const line of result.logs) logs.log(`${scenario.id} reset: ${line.message}`);
      for (const w of result.warnings) logs.error(`${scenario.id} reset warn: ${w}`);
      this.suite = resetTestState(this.suite, scenario);
      await this.persist();
      logs.log(`reset test ${scenario.id}`);
    } finally {
      this.busy = false;
    }
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

  /**
   * Build the run-summary export markdown (phase-5 deliverable: export)
   * from the loaded scenarios + suite state. The body is built by the
   * engine-neutral core builder so it stays testable without a backend.
   * Returns the markdown; the caller decides copy-to-clipboard vs. save.
   */
  buildExportMarkdown(): string {
    const generatedAt = nowIso();
    return buildExportMarkdown({
      scenarios: this.scenarios,
      state: this.suite,
      projectPath: this.activeProject,
      engineProfileId: this.profile?.id ?? null,
      generatedAt,
    });
  }

  /**
   * Copy the run-summary export markdown to the clipboard. The operator
   * pastes it into a milestone checklist or changelog as the sign-off
   * record (convention-patch-outline → checklist as index + sign-off).
   * Returns true on success.
   */
  async copyExport(): Promise<boolean> {
    const md = this.buildExportMarkdown();
    const ok = await this.copy(md);
    if (ok) logs.log("export copied to clipboard");
    else logs.error("export clipboard copy failed");
    return ok;
  }

  /**
   * Save the run-summary export markdown to a file under the project's
   * `exportsDir` (phase-5 deliverable: optional file export). The backend
   * owns the atomic write + timestamped filename; returns the
   * project-relative path the file landed at. Throws on backend failure
   * (the caller surfaces the error).
   */
  async saveExportFile(): Promise<string> {
    if (!this.activeProject) throw new Error("No project selected.");
    const generatedAt = nowIso();
    const body = buildExportMarkdown({
      scenarios: this.scenarios,
      state: this.suite,
      projectPath: this.activeProject,
      engineProfileId: this.profile?.id ?? null,
      generatedAt,
    });
    // Stem: the active milestone if all scenarios share one, else "run".
    const milestones = new Set(this.scenarios.map((s) => s.milestone));
    const stem = milestones.size === 1 ? [...milestones][0] : "run";
    const path = await backend.saveExport(stem, generatedAt, body);
    logs.log(`export saved: ${path}`);
    return path;
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

/**
 * Coerce a raw `status` string from the bridge_status MCP tool into the UI's
 * `BridgeStatusToken`. Unknown / missing values fall back to `unknown` so the
 * TopBar chip stays neutral rather than crashing on a shape drift.
 */
function bridgeStatusTokenFromString(raw: string | undefined): BridgeStatusToken {
  switch (raw) {
    case "running":
    case "compiling":
    case "stopped":
    case "dead_bridge":
      return raw;
    default:
      return "unknown";
  }
}
