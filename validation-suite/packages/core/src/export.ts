/**
 * Run-summary export (phase-5 deliverable).
 *
 * Builds a sign-off markdown summary from the loaded scenarios + suite
 * state + project/profile context. The output is suitable for pasting
 * into a milestone checklist or changelog (convention-patch-outline →
 * checklist becomes an index + sign-off artifact; the export is the
 * "representative run summary" entry).
 *
 * Pure functions: the caller supplies all inputs (scenarios, state,
 * project path, profile id, and an optional `generatedAt` timestamp for
 * deterministic tests). The UI wires this to a clipboard copy + optional
 * file write under `UserSettings/ValidationSuite/exports/`.
 */

import type {
  RequirementLevel,
  Scenario,
  Status,
  SuiteState,
} from "./types.ts";

/** Inputs to {@link buildExportMarkdown}. All values are operator-facing. */
export interface ExportInput {
  /** Scenarios in their natural display order (milestone → order → id). */
  scenarios: Scenario[];
  /** The persisted suite state (status per scenario + project/profile). */
  state: SuiteState | null;
  /** Absolute selected project root, for the "Project" line. */
  projectPath: string | null;
  /** Active engine profile id, e.g. `unity`. */
  engineProfileId: string | null;
  /**
   * ISO-8601 timestamp for the "Generated" line. Defaults to now; tests
   * pass a fixed value for deterministic output.
   */
  generatedAt?: string;
}

/** Per-tier breakdown counts (idea.md → Coverage policy tiers). */
export interface TierCounts {
  total: number;
  done: number;
  blocked: number;
  awaiting: number;
}

/** The per-tier rollup used by the export + the UI export button. */
export function tierCounts(
  scenarios: Scenario[],
  state: SuiteState | null,
  tier: RequirementLevel,
): TierCounts {
  const scoped = scenarios.filter((s) => s.requirementLevel === tier);
  return rollup(scoped, state);
}

/** Roll up status across an arbitrary scenario slice. */
export function rollup(scenarios: Scenario[], state: SuiteState | null): TierCounts {
  const counts: TierCounts = { total: 0, done: 0, blocked: 0, awaiting: 0 };
  for (const s of scenarios) {
    counts.total += 1;
    const status = statusOf(state, s.id);
    counts[status] += 1;
  }
  return counts;
}

function statusOf(state: SuiteState | null, id: string): Status {
  return state?.tests[id]?.status ?? "awaiting";
}

/**
 * Build the sign-off markdown. Shape (frozen so checklists can reference
 * the headings): H1 title, a one-line meta block, a tier-breakdown table,
 * one status table per milestone (core/extended grouped, optional folded
 * under an "Optional" subheading), and a closeout-gate verdict line.
 */
export function buildExportMarkdown(input: ExportInput): string {
  const generatedAt = input.generatedAt ?? new Date().toISOString();
  const projectPath = input.projectPath ?? "(no project selected)";
  const engineProfileId = input.engineProfileId ?? "(no profile)";
  const scenarios = input.scenarios;

  const core = tierCounts(scenarios, input.state, "required-core");
  const extended = tierCounts(scenarios, input.state, "required-extended");
  const optional = tierCounts(scenarios, input.state, "optional");

  // Milestone groups, ordered milestone → order → id (matches the UI).
  const milestones = groupByMilestone(scenarios);

  const lines: string[] = [];
  lines.push("# Validation Suite — run summary");
  lines.push("");
  lines.push(`- **Generated:** ${generatedAt}`);
  lines.push(`- **Project:** \`${projectPath}\``);
  lines.push(`- **Engine profile:** ${engineProfileId}`);
  lines.push("");
  lines.push("## Requirement-tier breakdown");
  lines.push("");
  lines.push("| Tier | Total | Done | Blocked | Awaiting |");
  lines.push("|---|---|---|---|---|");
  lines.push(`| Required · core | ${core.total} | ${core.done} | ${core.blocked} | ${core.awaiting} |`);
  lines.push(`| Required · extended | ${extended.total} | ${extended.done} | ${extended.blocked} | ${extended.awaiting} |`);
  lines.push(`| Optional | ${optional.total} | ${optional.done} | ${optional.blocked} | ${optional.awaiting} |`);
  lines.push("");

  const gateVerdict = closeoutGateVerdict(core);
  lines.push(`**Closeout gate (required-core):** ${gateVerdict}`);
  lines.push("");

  for (const group of milestones) {
    lines.push(`## ${group.milestone}`);
    lines.push("");
    const required = group.scenarios.filter((s) => s.requirementLevel !== "optional");
    const opt = group.scenarios.filter((s) => s.requirementLevel === "optional");
    if (required.length > 0) {
      lines.push(...scenarioTable(required, input.state));
      lines.push("");
    }
    if (opt.length > 0) {
      lines.push("### Optional (automated-covered; runnable)");
      lines.push("");
      lines.push(...scenarioTable(opt, input.state));
      lines.push("");
    }
  }

  return lines.join("\n").trimEnd() + "\n";
}

/** One milestone group with its scenarios in display order. */
function groupByMilestone(
  scenarios: Scenario[],
): { milestone: string; scenarios: Scenario[] }[] {
  const groups = new Map<string, Scenario[]>();
  for (const s of scenarios) {
    const list = groups.get(s.milestone) ?? [];
    list.push(s);
    groups.set(s.milestone, list);
  }
  return [...groups.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([milestone, list]) => ({
      milestone,
      scenarios: [...list].sort(
        (a, b) => a.order - b.order || a.id.localeCompare(b.id),
      ),
    }));
}

/** Render a status table for a slice of scenarios. */
function scenarioTable(scenarios: Scenario[], state: SuiteState | null): string[] {
  const lines: string[] = [];
  lines.push("| Scenario | Tier | Status | Automated coverage |");
  lines.push("|---|---|---|---|");
  for (const s of scenarios) {
    const status = statusOf(state, s.id);
    const coverage = (s.automatedCoverage ?? []).join("; ") || "—";
    lines.push(
      `| \`${s.id}\` — ${s.title} | ${tierWord(s.requirementLevel)} | ${statusWord(status)} | ${coverage} |`,
    );
  }
  return lines;
}

function tierWord(level: RequirementLevel): string {
  switch (level) {
    case "required-core":
      return "core";
    case "required-extended":
      return "extended";
    case "optional":
      return "optional";
  }
}

function statusWord(status: Status): string {
  switch (status) {
    case "done":
      return "✅ done";
    case "blocked":
      return "⛔ blocked";
    case "awaiting":
      return "⏳ awaiting";
  }
}

/**
 * The closeout-gate verdict from the core tier. The milestone closeout
 * gate is the `required-core` set (idea.md → Coverage policy): it passes
 * only when every core scenario is `done` (blocked/awaiting both fail).
 */
export function closeoutGateVerdict(core: TierCounts): string {
  if (core.total === 0) return "no required-core scenarios loaded";
  if (core.done === core.total) {
    return `PASS — all ${core.total} required-core scenario(s) done`;
  }
  const remaining = core.total - core.done;
  return `NOT YET — ${remaining} of ${core.total} required-core scenario(s) not done`;
}
