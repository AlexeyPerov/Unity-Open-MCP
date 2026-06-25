/**
 * Data contracts for the Validation Suite.
 *
 * These DTOs are the single source of truth shared between the UI
 * (Svelte 5) and the Rust backend (via Tauri IPC). They mirror the
 * frozen shapes in:
 *   - specs/testsuite-tauri/idea.md (scenario model + step types)
 *   - specs/testsuite-tauri/engine-profiles/unity.md (state file schema)
 *   - specs/testsuite-tauri/scenarios/sample-scenario.schema-reference.json
 *
 * Pure types only — no I/O, no Tauri imports. The core package is the
 * engine-neutral orchestration layer; engine specifics live in engine
 * profiles and scenario JSON (see idea.md → Multi-engine reuse).
 */

// ─────────────────────────────────────────────────────────────────────────────
// Patch operations (pinned in phase-2 spec)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Patch operations understood by `fs_patch` (pinned in the Phase 2 spec).
 * The loader rejects any other op at scenario-load time (config-action
 * drift guard — see phase-2 task 3). Reversion uses a file snapshot, not
 * an inverse recipe (see phase-2 spec → revert strategy: snapshot). The
 * matching vocabulary array {@link PATCH_OPS} lives in `errors.ts`.
 */
export type PatchOp =
  | "replace_line_contains"
  | "insert_after_line_contains"
  | "insert_before_line_contains"
  | "trim_trailing_whitespace";

/**
 * A single deterministic text patch. `match` selects the line to act on
 * (substring match; first matching line per op). Field usage by `op`:
 *  - `replace_line_contains`         → needs `match` + `replace`.
 *  - `insert_after_line_contains`    → needs `match` + `insert`.
 *  - `insert_before_line_contains`   → needs `match` + `insert`.
 *  - `trim_trailing_whitespace`      → ignores `match`/`replace`/`insert`.
 */
export interface FsPatchEntry {
  op: PatchOp;
  /** Substring used to select the target line. */
  match?: string;
  /** Replacement line (full line) for `replace_line_contains`. */
  replace?: string;
  /** Line(s) to insert for the insert ops. */
  insert?: string;
}

// ─────────────────────────────────────────────────────────────────────────────
// Requirement tiers & status
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Requirement tier for a scenario. Tiers drive the top-level filters
 * (idea.md → Coverage policy):
 *   - `required-core`    — milestone closeout gate
 *   - `required-extended` — recommended confidence pass
 *   - `optional`          — runnable; often shows automated coverage
 */
export type RequirementLevel = "required-core" | "required-extended" | "optional";

/**
 * Lifecycle status for a test and for individual steps (idea.md → UI
 * shape). `awaiting` is the default; `blocked` is operator-set when a
 * step cannot proceed.
 */
export type Status = "awaiting" | "done" | "blocked";

// ─────────────────────────────────────────────────────────────────────────────
// Step types
// ─────────────────────────────────────────────────────────────────────────────

/** All step `type` values recognized by the renderer (idea.md → Step types). */
export type StepType =
  | "info"
  | "setup"
  | "agent_prompt"
  | "expected"
  | "actual"
  | "external_doc"
  | "mark_done";

/**
 * Setup action verbs. v1 ships fs_* + mcp_tool + manual. The action
 * *executor* lands in Phase 2; Phase 1 only validates action types at
 * load time (idea.md → action schema; phase-1 task 4). Built-in action
 * verbs are engine-agnostic (idea.md → Built-in actions).
 */
export type ActionType = "fs_copy" | "fs_patch" | "fs_delete" | "mcp_tool" | "manual";

/**
 * A single declarative setup action. Params are free-form JSON because
 * each action verb has its own param shape (validated loosely here;
 * strictly by the executor in Phase 2). The `action` discriminator is
 * always present and is the only field checked at scenario-load time.
 */
export interface SetupAction {
  /** Action verb (discriminator). Must be one of {@link ActionType}. */
  action: ActionType;
  /** Free-form params. Shape depends on `action`; opaque at load time. */
  [key: string]: unknown;
}

/**
 * A scenario step. `id` is stable and keys into the state file's
 * `stepStatus` map. The `type` discriminator selects which optional
 * fields the renderer reads.
 */
export interface ScenarioStep {
  /** Stable step id. Unique within a scenario. Keys `stepStatus`. */
  id: string;
  /** Renderer discriminator (idea.md → Step types). */
  type: StepType;
  /** Title shown in the step header (optional for most types). */
  title?: string;
  /** Body copy for `info` steps. */
  body?: string;
  /** Ordered setup actions for `setup` steps. */
  actions?: SetupAction[];
  /** Tool name for `agent_prompt` (e.g. `unity_open_mcp_reserialize`). */
  tool?: string;
  /** Copy-ready prompt or tool payload for `agent_prompt`. */
  payload?: unknown;
  /** Expected-outcome checklist items for `expected` steps. */
  items?: string[];
  /** Local doc path to open for `external_doc` steps. */
  docPath?: string;
}

/** Per-step reset overrides. Keyed by step id (see sample scenario). */
export interface ScenarioReset {
  /** Reset actions to run when a specific step is reset. */
  afterStep?: Record<string, { actions: SetupAction[] }>;
  /** Free-text operator note (e.g. offline-bridge guidance). */
  note?: string;
}

// ─────────────────────────────────────────────────────────────────────────────
// Scenario
// ─────────────────────────────────────────────────────────────────────────────

/**
 * A validation scenario (one test). Source of truth is JSON shipped
 * under `scenarios/<engineId>/`. Ids follow `m{n}-{slug}`
 * (m9-id-map.md). `order` is within-milestone; the UI groups by
 * `milestone` then sorts by `order`.
 */
export interface Scenario {
  /** Stable id, e.g. `m9-reserialize-happy-path`. */
  id: string;
  /** Human title. */
  title: string;
  /** Milestone key for grouping, e.g. `m9`. */
  milestone: string;
  /** Profile key this scenario targets, e.g. `unity`. */
  engineId: string;
  /** Sort order within the milestone. */
  order: number;
  /** Requirement tier (idea.md → Coverage policy). */
  requirementLevel: RequirementLevel;
  /** Free-form tags. */
  tags?: string[];
  /** Ordered list of steps. */
  steps: ScenarioStep[];
  /** Automated-coverage references (for optional scenarios). */
  automatedCoverage?: string[];
  /** Optional reset contract for this scenario. */
  reset?: ScenarioReset;
}

// ─────────────────────────────────────────────────────────────────────────────
// Manifest (Phase 2 — artifact tracking for reset)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The kind of artifact a manifest entry describes. Determines how reset
 * reverts it (phase-2 spec → Reset contract):
 *  - `created`  → reset deletes the path (and its companion if any).
 *  - `modified` → reset restores the snapshot taken before the patch.
 *  - `deleted`  → reset does nothing (the delete was the cleanup).
 */
export type ManifestEntryKind = "created" | "modified" | "deleted";

/**
 * One artifact recorded by an `fs_*` action. Paths are project-relative
 * (forward-slash) so the manifest is portable across OSes and survives a
 * project root move. `snapshot` holds the pre-patch file bytes (encoded
 * utf-8) for `fs_patch` entries; reset writes it back verbatim
 * (phase-2 spec → revert strategy: snapshot).
 */
export interface ManifestEntry {
  kind: ManifestEntryKind;
  /** Project-relative path (forward-slash) of the primary artifact. */
  path: string;
  /** True when a companion (e.g. `.meta`) was also touched. */
  companionPath?: string;
  /** Pre-patch file contents (utf-8) for `modified` entries; else absent. */
  snapshot?: string;
}

/**
 * A per-step manifest: the ordered list of artifacts a step's setup
 * actions produced. Reverse-order reset walks this list from the back so
 * later operations unwind before earlier ones (phase-2 task 6). The
 * backend persists manifests as blobs in `UserSettings/ValidationSuite/`;
 * the state file only keeps the blob id reference per step.
 */
export interface StepManifest {
  scenarioId: string;
  stepId: string;
  entries: ManifestEntry[];
}

/**
 * Manifest reference stored in `.state.json`. `null` while Phase 1 (no
 * manifest); from Phase 2 onward it is the manifest blob id the backend
 * uses to load the full {@link StepManifest} (see
 * engine-profiles/unity.md → State file schema → manifestRefs).
 */
export type ManifestRef = string | null;

// ─────────────────────────────────────────────────────────────────────────────
// Action execution results + action log (Phase 2)
// ─────────────────────────────────────────────────────────────────────────────

/** Severity of an action-log line shown in the step's action log panel. */
export type ActionLogLevel = "info" | "warn" | "error";

/** A single line in a step's action log (phase-2 deliverable: log panel). */
export interface ActionLogLine {
  level: ActionLogLevel;
  message: string;
  /** Optional CLI output snippet (stderr/stdout) for `mcp_tool` actions. */
  snippet?: string;
}

/** Outcome of running a single setup action within a step. */
export interface ActionResult {
  /** True when the action completed without error. */
  ok: boolean;
  /** Human-readable summary (e.g. `copied 2 file(s)`). */
  summary: string;
  /** Log lines produced by this action (forwarded to the log panel). */
  logs: ActionLogLine[];
  /** Manifest entries recorded by this action (empty for read-only ops). */
  entries: ManifestEntry[];
  /**
   * For `mcp_tool`: the parsed CLI result body + isError flag. Absent for
   * `fs_*`/`manual`.
   */
  mcp?: { isError: boolean; result: unknown };
}

/**
 * Outcome of running all actions in a step (phase-2 deliverable: action
 * executor). `ok` is false if any action failed; execution stops at the
 * first failure (setup is ordered + deterministic — partial application
 * would leave an inconsistent fixture). The `manifestId` is what gets
 * stored in `manifestRefs[stepId]` so reset can reload the entries.
 */
export interface StepRunResult {
  ok: boolean;
  results: ActionResult[];
  /** Backend manifest blob id for this step (null when nothing mutated). */
  manifestId: ManifestRef;
  /** Aggregated log lines across all actions. */
  logs: ActionLogLine[];
}

/** Outcome of a reset (step or all). */
export interface ResetResult {
  ok: boolean;
  /** Per-step warnings from best-effort cleanup (incomplete manifests). */
  warnings: string[];
  logs: ActionLogLine[];
}

// ─────────────────────────────────────────────────────────────────────────────
// State file (.state.json)
// ─────────────────────────────────────────────────────────────────────────────

/** Per-step persisted status, keyed by step id from the scenario JSON. */
export type StepStatusMap = Record<string, Status>;

/**
 * Pastes/actual payload filenames in `actualsDir`, keyed by step id
 * (format: `<test-id>-<step-id>.json`). Raw only in v1
 * (idea.md → Data and paths).
 */
export type ActualsRefs = Record<string, string>;

/** Per-step manifest reference for reset (Phase 2). */
export type ManifestRefs = Record<string, ManifestRef>;

/** Per-scenario persisted state (unity.md → State file schema). */
export interface TestState {
  status: Status;
  stepStatus: StepStatusMap;
  startedAt: string | null;
  completedAt: string | null;
  actualsRefs: ActualsRefs;
  manifestRefs: ManifestRefs;
}

/** Active project + engine profile block. */
export interface ProjectState {
  path: string;
  engineProfileId: string;
  lastOpenedAt: string;
}

/**
 * The on-disk `.state.json` shape. Frozen in
 * engine-profiles/unity.md → State file schema. `version` exists only
 * to power the warn+reset policy; there is NO migration logic
 * (idea.md → Schema policy for v1).
 */
export interface SuiteState {
  /** Integer; bumped only when the shape changes. */
  version: number;
  project: ProjectState;
  tests: Record<string, TestState>;
}

/** The only supported state-file version (unity.md → State file schema). */
export const STATE_VERSION = 1;

// ─────────────────────────────────────────────────────────────────────────────
// Engine profile
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Path conventions declared by an engine profile (unity.md → Path
 * conventions). Relative to the selected project root unless noted.
 */
export interface ProfilePaths {
  /** Fixture root pattern; `<test-id>` is interpolated by the executor. */
  fixtureRoot: string;
  /** Operator progress + actuals + exports root. */
  stateRoot: string;
  /** Atomic read/write state file. */
  stateFile: string;
  /** Raw pasted actual payloads, one per step. */
  actualsDir: string;
  /** Run summary exports. */
  exportsDir: string;
}

/** Companion-artifact rule (unity.md → Companion artifacts), e.g. `.meta`. */
export interface CompanionRule {
  /** Glob-like primary extension, e.g. `*.prefab`. */
  primary: string;
  /** Companion extension, e.g. `*.prefab.meta`. */
  companion: string;
}

/** Marker files/dirs used to detect a valid project (unity.md → Project detection). */
export interface ProjectMarkers {
  /** Required directories (all must exist). */
  dirs: string[];
  /** Required files (any one suffices). */
  files: string[];
}

/** Placeholder tokens a profile expands in scenario params (unity.md → Path conventions). */
export type PlaceholderToken = "{fixtureRoot}" | "{projectRoot}";

/**
 * An engine profile (unity.md). v1 ships `unity`; the core is shaped to
 * be *extractable* for a second engine, but no abstraction is built
 * ahead of time (idea.md → Multi-engine reuse strategy).
 */
export interface EngineProfile {
  id: string;
  displayName: string;
  /** MCP CLI binary name (resolved from toolkit root or PATH). */
  mcpCliBinary: string;
  paths: ProfilePaths;
  markers: ProjectMarkers;
  companions: CompanionRule[];
  /** Placeholders this profile knows how to expand. */
  placeholders: PlaceholderToken[];
  /** MCP tool name prefix for agent-facing tools (default `unity_open_mcp_`). */
  toolNamePrefix: string;
}

// ─────────────────────────────────────────────────────────────────────────────
// Loader result
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Outcome of loading all scenarios for an engine. `errors` are surfaced
 * in the UI as readable load failures without crashing the app
 * (phase-1 validation: "Invalid scenario file produces readable UI
 * error without crashing").
 */
export interface ScenarioLoadResult {
  scenarios: Scenario[];
  /** Per-file load errors. Empty when all scenarios are valid. */
  errors: ScenarioLoadError[];
}

/** A single scenario file load/validation error. */
export interface ScenarioLoadError {
  /** Relative path of the offending file, for display. */
  source: string;
  /** Human-readable reason. */
  message: string;
}
