// M23 Plan 2 — Modal dialog policy taxonomy + per-dialog button selection.
//
// Extends M13 T4.5 (launch-errors / Safe Mode auto-dismiss with a hard-coded
// "Ignore" click) with UCP's 6-variant dialog policy taxonomy so different
// automation workflows can pick different buttons on the SAME dialog:
//
//   UNITY_OPEN_MCP_DIALOG_POLICY=auto|manual|ignore|recover|safe-mode|cancel
//
// The taxonomy + per-kind-per-policy button preference tables are a faithful
// port of UCP's `preferred_dialog_button_label` (cli/src/discovery.rs), which
// is the most battle-tested reference for this heuristic. UCP's *cross-platform
// dismiss* is a non-Windows no-op stub — we deliberately do NOT copy that part
// (see dialog-dismiss.ts, which follows Unity-MCP's working macOS/Linux path).
//
// This module is PURE: no I/O, no platform calls, no globals. It is exhaustively
// unit-tested (dialog-policy.test.ts) — the tables are the contract.

// ---------------------------------------------------------------------------
// Policy taxonomy (UCP StartupDialogPolicy, kebab-case serialized)
// ---------------------------------------------------------------------------

/**
 * The 6-variant dialog policy taxonomy. Adopted verbatim from UCP's
 * `StartupDialogPolicy` (cli/src/config.rs). Each workflow picks a different
 * button on the same Unity modal:
 *
 *   - `auto`       — click the safest forward-progress button per dialog
 *                    (Ignore on launch-errors, Continue on version mismatch,
 *                    OK on graphics-api, Confirm on project-upgrade — the
 *                    last gated behind an explicit opt-in, see below).
 *   - `manual`     — never click anything (same as the T4.5 opt-out). The
 *                    polling loop is skipped entirely.
 *   - `ignore`     — dismiss launch-errors with Ignore; Continue/OK on the
 *                    safe net-new kinds; NEVER confirm a project upgrade.
 *                    This is the DEFAULT — it preserves the exact M13 T4.5
 *                    behaviour for launch-errors while adding two safe
 *                    dismissals and one safe block.
 *   - `recover`    — prefer Load Recovery / Recover / Restore on crash-recovery
 *                    dialogs; forward-progress otherwise.
 *   - `safe-mode`  — prefer Enter Safe Mode on the launch-errors dialog (debug
 *                    workflows); Cancel/Quit on dialogs that have no safe-mode
 *                    option.
 *   - `cancel`     — fail-fast: click Cancel/Quit/Close/No everywhere.
 *
 * Project Upgrade Required is special: it MUTATES the project irreversibly, so
 * no policy value auto-confirms it. A separate opt-in switch
 * (`UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE=1`) is required before ANY policy may
 * click Confirm on that dialog. See {@link isProjectUpgradeBlocked}.
 */
export type DialogPolicy =
  | "auto"
  | "manual"
  | "ignore"
  | "recover"
  | "safe-mode"
  | "cancel";

/** All valid policy values, lowercased kebab-case. */
export const DIALOG_POLICY_VALUES: readonly DialogPolicy[] = [
  "auto",
  "manual",
  "ignore",
  "recover",
  "safe-mode",
  "cancel",
];

/**
 * The policy that preserves M13 T4.5 behaviour on the launch-errors dialog.
 * Used as the default when `UNITY_OPEN_MCP_DIALOG_POLICY` is unset or invalid.
 */
export const DEFAULT_DIALOG_POLICY: DialogPolicy = "ignore";

/** Env var name that selects the policy. */
export const DIALOG_POLICY_ENV = "UNITY_OPEN_MCP_DIALOG_POLICY";

/**
 * Parse the policy from the environment. Unset / empty → default. Invalid →
 * default + a one-line warning on stderr so a typo does not silently downgrade
 * the user's stated intent. Pure aside from the optional warning sink.
 */
export function parseDialogPolicy(
  env: NodeJS.ProcessEnv = process.env,
  warn: (msg: string) => void = defaultWarn,
): DialogPolicy {
  const raw = env[DIALOG_POLICY_ENV];
  if (raw === undefined || raw === "") return DEFAULT_DIALOG_POLICY;
  const norm = raw.trim().toLowerCase();
  if ((DIALOG_POLICY_VALUES as readonly string[]).includes(norm)) {
    return norm as DialogPolicy;
  }
  warn(
    `[unity-open-mcp] invalid ${DIALOG_POLICY_ENV}=${JSON.stringify(raw)}; ` +
      `expected one of ${DIALOG_POLICY_VALUES.join(", ")}. ` +
      `Falling back to '${DEFAULT_DIALOG_POLICY}'.`,
  );
  return DEFAULT_DIALOG_POLICY;
}

function defaultWarn(msg: string): void {
  // console.warn so it lands on stderr alongside the dismiss-loop audit lines.
  console.warn(msg);
}

// ---------------------------------------------------------------------------
// Dialog kinds (UCP per-title keyword matching)
// ---------------------------------------------------------------------------

/**
 * The four Unity startup modal families this helper knows how to classify and
 * (under the active policy) dismiss. Each maps to a window-title keyword set
 * (see {@link DIALOG_TITLE_FRAGMENTS}) and a per-policy button preference
 * table (see {@link preferredDialogButtonLabel}).
 */
export type DialogKind =
  | "launch_errors"
  | "non_matching_editor"
  | "project_upgrade"
  | "auto_graphics_api";

/**
 * Window-title fragments per dialog kind. Case-insensitive substring match
 * against the normalized title (alphanumeric-only, lowercased — see
 * {@link normalizeDialogLabel}). Both legacy and modern Unity spellings are
 * listed; Unity's actual title varies by version.
 *
 * The `launch_errors` set mirrors the M13 T4.5 fragment list so the new
 * classifier stays a strict superset of the old matcher.
 */
export const DIALOG_TITLE_FRAGMENTS: Readonly<Record<DialogKind, readonly string[]>> = {
  // Unity 2020.2+ renamed the launch-errors dialog to "Enter Safe Mode?".
  // Without these fragments every modern Unity (2022 LTS, 6000.x) boots past
  // the auto-dismiss path.
  launch_errors: [
    "entersafemode",
    "safemode",
    "compilererrors",
    "holdon",
    "compileerrors",
    "scripthavecompilererrors",
  ],
  // "Opening Project in Non-Matching Editor Installation" — typically
  // Continue / Quit. Surfaces when a project is forced open in a different
  // editor version than ProjectVersion.txt specifies.
  non_matching_editor: ["openingprojectinnonmatchingeditorinstallation"],
  // "Project Upgrade Required" — typically Confirm / Quit. MUTATES the
  // project metadata; never auto-confirm unless explicitly opted in.
  project_upgrade: ["projectupgraderequired"],
  // "Auto Graphics API Notice" — informational, typically OK only.
  auto_graphics_api: ["autographicsapi"],
};

/**
 * Classify a Unity window title into a known dialog kind, or `null` when it
 * does not match any. Pure / case-insensitive.
 *
 * The title is normalized (alphanumeric-only, lowercased) before matching, so
 * "Enter Safe Mode?" → "entersafemode" matches the `launch_errors` fragment.
 * Kinds are checked in declaration order; the first match wins.
 */
export function classifyDialogTitle(title: string): DialogKind | null {
  const norm = normalizeDialogLabel(title);
  if (norm === "") return null;
  for (const kind of Object.keys(DIALOG_TITLE_FRAGMENTS) as DialogKind[]) {
    for (const frag of DIALOG_TITLE_FRAGMENTS[kind]) {
      if (norm.includes(frag)) return kind;
    }
  }
  return null;
}

/**
 * Normalize a label for matching: strip every non-alphanumeric char and
 * lowercase. Mirrors UCP `normalize_dialog_label`. "Enter Safe Mode?" →
 * "entersafemode"; "Load Recovery..." → "loadrecovery".
 */
export function normalizeDialogLabel(value: string): string {
  let out = "";
  for (let i = 0; i < value.length; i++) {
    const ch = value.charCodeAt(i);
    // ASCII alphanumeric only — keeps the matcher locale-independent and
    // punctuation-insensitive (Unity decorates titles with ?, !, ...).
    if (
      (ch >= 48 && ch <= 57) || // 0-9
      (ch >= 65 && ch <= 90) || // A-Z
      (ch >= 97 && ch <= 122) // a-z
    ) {
      out += value[i].toLowerCase();
    }
  }
  return out;
}

// ---------------------------------------------------------------------------
// Per-kind-per-policy button preference tables (UCP port)
// ---------------------------------------------------------------------------

/**
 * The normalized button-label tokens a policy prefers for a given dialog kind,
 * in priority order. The first token that matches a button actually present on
 * the dialog wins. `null` means "this policy does not dismiss this kind" (the
 * probe reports `blocked` instead of clicking).
 *
 * Ported from UCP `preferred_dialog_button_label` title-specific branches
 * (cli/src/discovery.rs:154-199). UCP's `Manual` arm returns `None` for every
 * kind — preserved here.
 *
 * Project Upgrade is gated: even when a policy WOULD confirm it (auto/ignore/
 * recover), this function returns `null` unless `allowProjectUpgrade` is true.
 * See {@link isProjectUpgradeBlocked}.
 */
export function preferenceTokensForPolicy(
  kind: DialogKind,
  policy: DialogPolicy,
  allowProjectUpgrade = false,
): readonly string[] | null {
  // Project Upgrade: never confirm unless the dedicated opt-in is set,
  // regardless of policy. This is the irreversible-mutation guard.
  if (kind === "project_upgrade" && !allowProjectUpgrade) return null;

  switch (kind) {
    case "launch_errors":
      return launchErrorsTokens(policy);
    case "non_matching_editor":
      return nonMatchingEditorTokens(policy);
    case "project_upgrade":
      // Only reached when allowProjectUpgrade === true.
      return projectUpgradeTokens(policy);
    case "auto_graphics_api":
      return autoGraphicsApiTokens(policy);
  }
}

function launchErrorsTokens(policy: DialogPolicy): readonly string[] | null {
  switch (policy) {
    case "auto":
    case "ignore":
      return ["ignore", "continue", "ok"];
    case "recover":
    case "safe-mode":
      // recover: prefer entering safe mode so the crash can be inspected, then
      // fall back to Ignore. safe-mode: enter safe mode explicitly.
      return ["entersafemode", "safemode"];
    case "cancel":
      return ["quit", "cancel", "close", "no"];
    case "manual":
      return null;
  }
}

function nonMatchingEditorTokens(policy: DialogPolicy): readonly string[] | null {
  switch (policy) {
    case "auto":
    case "ignore":
    case "recover":
      return ["continue", "openproject", "openanyway", "ok"];
    case "safe-mode":
      return ["quit", "cancel"];
    case "cancel":
      return ["quit", "cancel", "close", "no"];
    case "manual":
      return null;
  }
}

function projectUpgradeTokens(policy: DialogPolicy): readonly string[] | null {
  // Caller has already checked allowProjectUpgrade === true.
  switch (policy) {
    case "auto":
    case "ignore":
    case "recover":
      return ["confirm", "continue", "openproject", "openanyway", "ok", "yes"];
    case "cancel":
      return ["quit", "cancel", "close", "no"];
    case "safe-mode":
    case "manual":
      return null;
  }
}

function autoGraphicsApiTokens(policy: DialogPolicy): readonly string[] | null {
  switch (policy) {
    case "auto":
    case "ignore":
    case "recover":
      return ["ok", "continue", "confirm", "yes"];
    case "cancel":
      return ["quit", "cancel", "close", "no"];
    case "safe-mode":
    case "manual":
      return null;
  }
}

/**
 * Generic per-policy fallback token list, applied when a dialog title does NOT
 * match any known kind (an unknown Unity modal). Ported from UCP's generic
 * fallback switch (cli/src/discovery.rs:217-259). `manual` returns `[]` (no
 * click) rather than `null` so the caller can distinguish "no preference"
 * from "known kind, this policy declines".
 */
export function genericFallbackTokens(policy: DialogPolicy): readonly string[] {
  switch (policy) {
    case "auto":
      return [
        "ignore",
        "continue",
        "confirm",
        "skiprecovery",
        "skip",
        "openproject",
        "openanyway",
        "ok",
        "yes",
        "loadrecovery",
        "recover",
        "restore",
        "entersafemode",
        "safemode",
      ];
    case "ignore":
      return [
        "ignore",
        "continue",
        "confirm",
        "skiprecovery",
        "skip",
        "openproject",
        "openanyway",
        "ok",
        "yes",
      ];
    case "recover":
      return [
        "continue",
        "confirm",
        "openproject",
        "openanyway",
        "loadrecovery",
        "recover",
        "restore",
        "ok",
        "yes",
      ];
    case "safe-mode":
      return ["entersafemode", "safemode"];
    case "cancel":
      return ["cancel", "quit", "close", "no"];
    case "manual":
      return [];
  }
}

/**
 * The kinds a policy explicitly declines to dismiss. Used by the polling loop
 * to report a `blocked` outcome (audit line, no click) instead of silently
 * ignoring the dialog. Currently only `project_upgrade` under the default
 * (no opt-in) — the one irreversible-mutation guard.
 */
export function blockedKindsForPolicy(
  policy: DialogPolicy,
  allowProjectUpgrade = false,
): readonly DialogKind[] {
  if (policy === "manual") return []; // manual declines everything — not "blocked"
  if (!allowProjectUpgrade) return ["project_upgrade"];
  return [];
}

/**
 * Whether a project-upgrade confirm would be blocked under the given flags.
 * True unless the dedicated opt-in is set. Independent of policy because NO
 * policy value implies consent to mutate the project.
 */
export function isProjectUpgradeBlocked(
  _policy: DialogPolicy,
  allowProjectUpgrade = false,
): boolean {
  return !allowProjectUpgrade;
}

/**
 * Select the button to click on a dialog, given its kind, the visible button
 * labels, the active policy, and the project-upgrade opt-in.
 *
 * Returns `{ button, token }` — `button` is the original (un-normalized) label
 * from `buttonLabels` that matched the highest-priority token; `token` is the
 * normalized token that won. Returns `null` when:
 *   - the policy declines this kind (e.g. `manual`, or project_upgrade without
 *     the opt-in) → caller reports `blocked`,
 *   - or no preferred token matches any visible button → caller reports
 *     `not-found` (the dialog is there but we have no safe button to press).
 *
 * Pure. Ported from UCP `preferred_dialog_button_label`.
 */
export function preferredDialogButtonLabel(
  kind: DialogKind,
  buttonLabels: readonly string[],
  policy: DialogPolicy,
  opts: { allowProjectUpgrade?: boolean } = {},
): { button: string; token: string } | null {
  const allowProjectUpgrade = opts.allowProjectUpgrade ?? false;
  const tokens = preferenceTokensForPolicy(kind, policy, allowProjectUpgrade);
  if (tokens === null) return null;
  return matchTokens(tokens, buttonLabels);
}

/**
 * Select a button on an UNKNOWN dialog using the generic per-policy fallback.
 * Returns `null` when the policy is `manual` (empty token list) or no token
 * matches. Pure.
 */
export function preferredGenericButtonLabel(
  buttonLabels: readonly string[],
  policy: DialogPolicy,
): { button: string; token: string } | null {
  const tokens = genericFallbackTokens(policy);
  if (tokens.length === 0) return null;
  return matchTokens(tokens, buttonLabels);
}

/**
 * Find the first visible button whose normalized label contains the given
 * token, in token-priority order. Returns the original label + winning token.
 */
function matchTokens(
  tokens: readonly string[],
  buttonLabels: readonly string[],
): { button: string; token: string } | null {
  // Pre-normalize once per button.
  const normalized = buttonLabels.map((label) => ({
    raw: label,
    norm: normalizeDialogLabel(label),
  }));
  for (const token of tokens) {
    const hit = normalized.find((n) => n.norm.includes(token));
    if (hit) return { button: hit.raw, token };
  }
  return null;
}
