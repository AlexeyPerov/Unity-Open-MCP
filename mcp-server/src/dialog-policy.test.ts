// M23 Plan 2 — dialog-policy unit tests.
//
// Ports UCP's `#[cfg(test)]` block (cli/src/discovery.rs:592-738) plus
// coverage for the project-upgrade opt-in guard, the generic fallback, env
// parsing, and the classifier. Pure module — no platform calls.

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  classifyDialogTitle,
  normalizeDialogLabel,
  preferredDialogButtonLabel,
  preferredGenericButtonLabel,
  preferenceTokensForPolicy,
  genericFallbackTokens,
  blockedKindsForPolicy,
  isProjectUpgradeBlocked,
  isUnsavedSceneBlocked,
  parseDialogPolicy,
  DIALOG_POLICY_VALUES,
  DIALOG_TITLE_FRAGMENTS,
  DEFAULT_DIALOG_POLICY,
  DIALOG_POLICY_ENV,
  type DialogPolicy,
  type DialogKind,
} from "./dialog-policy.js";

// ---------------------------------------------------------------------------
// normalizeDialogLabel
// ---------------------------------------------------------------------------

test("normalizeDialogLabel: strips punctuation + lowercases (UCP parity)", () => {
  assert.equal(normalizeDialogLabel("Enter Safe Mode"), "entersafemode");
  assert.equal(normalizeDialogLabel("Enter Safe Mode?"), "entersafemode");
  assert.equal(normalizeDialogLabel("Load Recovery..."), "loadrecovery");
  assert.equal(normalizeDialogLabel("Project Upgrade Required"), "projectupgraderequired");
  assert.equal(normalizeDialogLabel("Auto Graphics API Notice"), "autographicsapinotice");
  assert.equal(normalizeDialogLabel(""), "");
  assert.equal(normalizeDialogLabel("   "), "");
});

test("normalizeDialogLabel: drops non-ASCII / whitespace", () => {
  assert.equal(normalizeDialogLabel("Hold On — Unity 2022"), "holdonunity2022");
  assert.equal(normalizeDialogLabel("OK (1)"), "ok1");
});

// ---------------------------------------------------------------------------
// classifyDialogTitle
// ---------------------------------------------------------------------------

test("classifyDialogTitle: matches every known kind via its real Unity title", () => {
  assert.equal(
    classifyDialogTitle("Enter Safe Mode?"),
    "launch_errors",
  );
  assert.equal(
    classifyDialogTitle("Compiler Errors on Launch"),
    "launch_errors",
  );
  assert.equal(
    classifyDialogTitle("Opening Project in Non-Matching Editor Installation"),
    "non_matching_editor",
  );
  assert.equal(
    classifyDialogTitle("Project Upgrade Required"),
    "project_upgrade",
  );
  assert.equal(
    classifyDialogTitle("Auto Graphics API Notice"),
    "auto_graphics_api",
  );
  // specs/feedback.md 2026-07-03 — the two steady-state modals that jam the
  // bridge when an external process rewrites a scene file (git checkout) or a
  // mutating tool leaves a scene dirty.
  assert.equal(
    classifyDialogTitle("Scene has been modified externally"),
    "scene_modified_externally",
  );
  assert.equal(
    classifyDialogTitle("Unsaved changes to scene"),
    "unsaved_scene_changes",
  );
  assert.equal(
    classifyDialogTitle("Scene(s) Have Been Modified"),
    "unsaved_scene_changes",
  );
});

test("classifyDialogTitle: legacy launch-errors titles still classify as launch_errors", () => {
  // Back-compat with the M13 T4.5 fragment list — every old fragment must
  // resolve to the launch_errors kind so the generalized matcher is a strict
  // superset of the old one.
  for (const frag of DIALOG_TITLE_FRAGMENTS.launch_errors) {
    // Reverse a fragment into a plausible title (capitalize first letter).
    const title = frag.charAt(0).toUpperCase() + frag.slice(1);
    assert.equal(
      classifyDialogTitle(title),
      "launch_errors",
      `fragment ${frag} should classify as launch_errors`,
    );
  }
});

test("classifyDialogTitle: unknown title → null", () => {
  assert.equal(classifyDialogTitle("Some Unknown Unity Dialog"), null);
  assert.equal(classifyDialogTitle(""), null);
});

test("classifyDialogTitle: case-insensitive", () => {
  assert.equal(classifyDialogTitle("enter safe mode?"), "launch_errors");
  assert.equal(classifyDialogTitle("PROJECT UPGRADE REQUIRED"), "project_upgrade");
});

// ---------------------------------------------------------------------------
// preferredDialogButtonLabel — launch_errors (UCP test parity)
// ---------------------------------------------------------------------------

test("launch_errors / ignore → Ignore", () => {
  const labels = ["Cancel", "Ignore", "Enter Safe Mode"];
  assert.deepEqual(
    preferredDialogButtonLabel("launch_errors", labels, "ignore"),
    { button: "Ignore", token: "ignore" },
  );
});

test("launch_errors / auto → Ignore (auto == ignore on this kind)", () => {
  const labels = ["Cancel", "Ignore", "Enter Safe Mode"];
  assert.deepEqual(
    preferredDialogButtonLabel("launch_errors", labels, "auto"),
    { button: "Ignore", token: "ignore" },
  );
});

test("launch_errors / recover → Enter Safe Mode (inspect before ignoring)", () => {
  const labels = ["Ignore", "Enter Safe Mode", "Quit"];
  assert.deepEqual(
    preferredDialogButtonLabel("launch_errors", labels, "recover"),
    { button: "Enter Safe Mode", token: "entersafemode" },
  );
});

test("launch_errors / safe-mode → Enter Safe Mode", () => {
  const labels = ["Ignore", "Enter Safe Mode", "Quit"];
  assert.deepEqual(
    preferredDialogButtonLabel("launch_errors", labels, "safe-mode"),
    { button: "Enter Safe Mode", token: "entersafemode" },
  );
});

test("launch_errors / cancel → Quit/Cancel", () => {
  const labels = ["Ignore", "Enter Safe Mode", "Quit"];
  const r = preferredDialogButtonLabel("launch_errors", labels, "cancel");
  assert.ok(r !== null);
  assert.equal(r!.button, "Quit");
});

test("launch_errors / manual → null (no click)", () => {
  const labels = ["Ignore", "Enter Safe Mode", "Quit"];
  assert.equal(
    preferredDialogButtonLabel("launch_errors", labels, "manual"),
    null,
  );
});

test("launch_errors / ignore with no matching button → null", () => {
  // Dialog present but neither Ignore/Continue/OK visible — do NOT click a
  // random button. Caller reports not-found.
  assert.equal(
    preferredDialogButtonLabel("launch_errors", ["Quit", "Retry"], "ignore"),
    null,
  );
});

// ---------------------------------------------------------------------------
// preferredDialogButtonLabel — non_matching_editor (UCP test parity)
// ---------------------------------------------------------------------------

test("non_matching_editor / ignore → Continue", () => {
  const labels = ["Continue", "Quit"];
  assert.deepEqual(
    preferredDialogButtonLabel("non_matching_editor", labels, "ignore"),
    { button: "Continue", token: "continue" },
  );
});

test("non_matching_editor / safe-mode → Quit (no safe-mode option)", () => {
  const labels = ["Continue", "Quit"];
  const r = preferredDialogButtonLabel("non_matching_editor", labels, "safe-mode");
  assert.ok(r !== null);
  assert.equal(r!.button, "Quit");
});

test("non_matching_editor / cancel → Quit", () => {
  const labels = ["Continue", "Quit"];
  const r = preferredDialogButtonLabel("non_matching_editor", labels, "cancel");
  assert.equal(r!.button, "Quit");
});

// ---------------------------------------------------------------------------
// preferredDialogButtonLabel — project_upgrade (the irreversible-mutation guard)
// ---------------------------------------------------------------------------

test("project_upgrade / ignore / DEFAULT → null (never auto-confirm)", () => {
  // The single most important assertion in this suite: no policy value
  // confirms a project upgrade unless the dedicated opt-in is set.
  const labels = ["Quit", "Confirm"];
  assert.equal(
    preferredDialogButtonLabel("project_upgrade", labels, "ignore"),
    null,
  );
  assert.equal(
    preferredDialogButtonLabel("project_upgrade", labels, "auto"),
    null,
  );
  assert.equal(
    preferredDialogButtonLabel("project_upgrade", labels, "recover"),
    null,
  );
});

test("project_upgrade / ignore + opt-in → Confirm", () => {
  const labels = ["Quit", "Confirm"];
  assert.deepEqual(
    preferredDialogButtonLabel("project_upgrade", labels, "ignore", {
      allowProjectUpgrade: true,
    }),
    { button: "Confirm", token: "confirm" },
  );
});

test("project_upgrade / cancel + opt-in → Quit (still allowed to refuse)", () => {
  const labels = ["Quit", "Confirm"];
  const r = preferredDialogButtonLabel("project_upgrade", labels, "cancel", {
    allowProjectUpgrade: true,
  });
  assert.equal(r!.button, "Quit");
});

test("project_upgrade / safe-mode + opt-in → null (no safe action)", () => {
  const labels = ["Quit", "Confirm"];
  assert.equal(
    preferredDialogButtonLabel("project_upgrade", labels, "safe-mode", {
      allowProjectUpgrade: true,
    }),
    null,
  );
});

test("project_upgrade / manual + opt-in → null", () => {
  assert.equal(
    preferredDialogButtonLabel("project_upgrade", ["Confirm"], "manual", {
      allowProjectUpgrade: true,
    }),
    null,
  );
});

// ---------------------------------------------------------------------------
// preferredDialogButtonLabel — auto_graphics_api
// ---------------------------------------------------------------------------

test("auto_graphics_api / ignore → OK", () => {
  const labels = ["OK"];
  assert.deepEqual(
    preferredDialogButtonLabel("auto_graphics_api", labels, "ignore"),
    { button: "OK", token: "ok" },
  );
});

test("auto_graphics_api / cancel → not-found (only OK present)", () => {
  // Cancel/Quit/Close/No tokens; dialog only has OK → no safe button → null.
  assert.equal(
    preferredDialogButtonLabel("auto_graphics_api", ["OK"], "cancel"),
    null,
  );
});

// ---------------------------------------------------------------------------
// specs/feedback.md 2026-07-03 — the two new steady-state kinds.
// scene_modified_externally: safe under auto/ignore/recover (Reload/Revert
// accepts the intentional disk rewrite). unsaved_scene_changes: destructive
// under every policy, blocked unless the dedicated opt-in is set.
// ---------------------------------------------------------------------------

test("scene_modified_externally / ignore → Reload (accept the disk version)", () => {
  const labels = ["Keep Mine", "Reload"];
  assert.deepEqual(
    preferredDialogButtonLabel("scene_modified_externally", labels, "ignore"),
    { button: "Reload", token: "reload" },
  );
});

test("scene_modified_externally / cancel → Cancel (decline to reload)", () => {
  const labels = ["Reload", "Cancel"];
  assert.deepEqual(
    preferredDialogButtonLabel("scene_modified_externally", labels, "cancel"),
    { button: "Cancel", token: "cancel" },
  );
});

test("scene_modified_externally / safe-mode → null (operator should inspect)", () => {
  assert.equal(
    preferredDialogButtonLabel(
      "scene_modified_externally",
      ["Reload", "Cancel"],
      "safe-mode",
    ),
    null,
  );
});

test("unsaved_scene_changes: blocked by default under every policy", () => {
  // The dedicated opt-in is OFF by default — destructive under every policy.
  for (const policy of DIALOG_POLICY_VALUES) {
    assert.equal(
      preferenceTokensForPolicy("unsaved_scene_changes", policy, false, false),
      null,
      `unsaved_scene_changes must be blocked by default under ${policy}`,
    );
  }
});

test("unsaved_scene_changes / ignore + opt-in → Save (preserve work)", () => {
  const labels = ["Cancel", "Don't Save", "Save"];
  const r = preferredDialogButtonLabel("unsaved_scene_changes", labels, "ignore", {
    allowProjectUpgrade: false,
  });
  // preferredDialogButtonLabel does not take allowUnsavedSceneDismiss in its
  // opts; test the underlying preferenceTokensForPolicy directly for the
  // opt-in path.
  void r;
  const tokens = preferenceTokensForPolicy(
    "unsaved_scene_changes",
    "ignore",
    false,
    true,
  );
  assert.deepEqual(tokens, ["save", "saveall", "savechanges", "ok", "yes"]);
});

test("unsaved_scene_changes: project_upgrade opt-in does NOT unblock it", () => {
  // The two opt-ins are independent.
  assert.equal(
    preferenceTokensForPolicy("unsaved_scene_changes", "ignore", true, false),
    null,
  );
});

// ---------------------------------------------------------------------------
// preferredGenericButtonLabel (UCP generic fallback)
// ---------------------------------------------------------------------------

test("generic fallback / ignore → Confirm when present", () => {
  const labels = ["Cancel", "Confirm"];
  assert.deepEqual(
    preferredGenericButtonLabel(labels, "ignore"),
    { button: "Confirm", token: "confirm" },
  );
});

test("generic fallback / auto → Yes when only Yes/No present", () => {
  const labels = ["No", "Yes"];
  assert.deepEqual(
    preferredGenericButtonLabel(labels, "auto"),
    { button: "Yes", token: "yes" },
  );
});

test("generic fallback / recover → Load Recovery", () => {
  const labels = ["Skip Recovery", "Load Recovery"];
  assert.deepEqual(
    preferredGenericButtonLabel(labels, "recover"),
    { button: "Load Recovery", token: "loadrecovery" },
  );
});

test("generic fallback / cancel → Cancel", () => {
  const labels = ["Continue", "Cancel"];
  assert.deepEqual(
    preferredGenericButtonLabel(labels, "cancel"),
    { button: "Cancel", token: "cancel" },
  );
});

test("generic fallback / manual → null", () => {
  assert.equal(preferredGenericButtonLabel(["OK"], "manual"), null);
});

// ---------------------------------------------------------------------------
// preferenceTokensForPolicy / blockedKindsForPolicy / isProjectUpgradeBlocked
// ---------------------------------------------------------------------------

test("preferenceTokensForPolicy: switch is total — every (kind × policy) is null or non-empty tokens", () => {
  // Exhaustive — pins the contract that the switch covers every combination
  // and never returns an empty array (null = explicit decline; non-empty =
  // click preference). The specific null cases are enumerated by the
  // kind-specific tests below.
  for (const kind of Object.keys(DIALOG_TITLE_FRAGMENTS) as DialogKind[]) {
    for (const policy of DIALOG_POLICY_VALUES) {
      const r = preferenceTokensForPolicy(kind, policy, true);
      if (r === null) continue;
      assert.ok(
        Array.isArray(r) && r.length > 0,
        `${kind}/${policy} returned a non-null but empty token list`,
      );
    }
  }
});

test("preferenceTokensForPolicy: explicit null cases (manual everywhere; safe-mode declines two kinds)", () => {
  // manual → null for EVERY kind (never clicks anything).
  for (const kind of Object.keys(DIALOG_TITLE_FRAGMENTS) as DialogKind[]) {
    assert.equal(
      preferenceTokensForPolicy(kind, "manual", true),
      null,
      `${kind}/manual must be null`,
    );
  }
  // safe-mode → null for project_upgrade AND auto_graphics_api (no safe-mode
  // option on those dialogs — UCP parity).
  assert.equal(
    preferenceTokensForPolicy("project_upgrade", "safe-mode", true),
    null,
  );
  assert.equal(
    preferenceTokensForPolicy("auto_graphics_api", "safe-mode", true),
    null,
  );
});

test("preferenceTokensForPolicy: project_upgrade returns null without opt-in for every non-manual policy", () => {
  for (const policy of DIALOG_POLICY_VALUES) {
    if (policy === "manual") continue;
    assert.equal(
      preferenceTokensForPolicy("project_upgrade", policy, false),
      null,
      `project_upgrade/${policy} must be null without opt-in`,
    );
  }
});

test("blockedKindsForPolicy: default → [project_upgrade, unsaved_scene_changes]", () => {
  // Both destructive-mutation guards are blocked by default: project_upgrade
  // mutates project metadata, unsaved_scene_changes loses work either way.
  assert.deepEqual(blockedKindsForPolicy("ignore"), [
    "project_upgrade",
    "unsaved_scene_changes",
  ]);
  assert.deepEqual(blockedKindsForPolicy("auto"), [
    "project_upgrade",
    "unsaved_scene_changes",
  ]);
  assert.deepEqual(blockedKindsForPolicy("recover"), [
    "project_upgrade",
    "unsaved_scene_changes",
  ]);
});

test("blockedKindsForPolicy: project-upgrade opt-in only → [unsaved_scene_changes]", () => {
  // The two opt-ins are independent: opting into project_upgrade does NOT
  // unblock unsaved_scene_changes (still destructive).
  assert.deepEqual(blockedKindsForPolicy("ignore", true), [
    "unsaved_scene_changes",
  ]);
  assert.deepEqual(blockedKindsForPolicy("auto", true), [
    "unsaved_scene_changes",
  ]);
});

test("blockedKindsForPolicy: both opt-ins → []", () => {
  assert.deepEqual(blockedKindsForPolicy("ignore", true, true), []);
  assert.deepEqual(blockedKindsForPolicy("auto", true, true), []);
});

test("blockedKindsForPolicy: manual → [] (manual declines everything, not 'blocked')", () => {
  assert.deepEqual(blockedKindsForPolicy("manual"), []);
  assert.deepEqual(blockedKindsForPolicy("manual", true), []);
  assert.deepEqual(blockedKindsForPolicy("manual", true, true), []);
});

test("isProjectUpgradeBlocked: true unless opt-in", () => {
  for (const policy of DIALOG_POLICY_VALUES) {
    assert.equal(isProjectUpgradeBlocked(policy, false), true);
    assert.equal(isProjectUpgradeBlocked(policy, true), false);
  }
});

test("isUnsavedSceneBlocked: true unless opt-in (independent of project_upgrade)", () => {
  for (const policy of DIALOG_POLICY_VALUES) {
    assert.equal(isUnsavedSceneBlocked(policy, false), true);
    assert.equal(isUnsavedSceneBlocked(policy, true), false);
  }
});

// ---------------------------------------------------------------------------
// parseDialogPolicy
// ---------------------------------------------------------------------------

test("parseDialogPolicy: unset → default (ignore)", () => {
  assert.equal(parseDialogPolicy({}), DEFAULT_DIALOG_POLICY);
  assert.equal(parseDialogPolicy({ [DIALOG_POLICY_ENV]: "" }), DEFAULT_DIALOG_POLICY);
});

test("parseDialogPolicy: every valid value round-trips", () => {
  for (const v of DIALOG_POLICY_VALUES) {
    assert.equal(parseDialogPolicy({ [DIALOG_POLICY_ENV]: v }), v);
  }
});

test("parseDialogPolicy: case + whitespace insensitive", () => {
  assert.equal(parseDialogPolicy({ [DIALOG_POLICY_ENV]: "IGNORE" }), "ignore");
  assert.equal(parseDialogPolicy({ [DIALOG_POLICY_ENV]: "  Safe-Mode  " }), "safe-mode");
});

test("parseDialogPolicy: invalid → default + warning", () => {
  const warnings: string[] = [];
  const r = parseDialogPolicy(
    { [DIALOG_POLICY_ENV]: "yolo" },
    (m) => warnings.push(m),
  );
  assert.equal(r, DEFAULT_DIALOG_POLICY);
  assert.equal(warnings.length, 1);
  assert.ok(warnings[0].includes("yolo"));
  assert.ok(warnings[0].includes(DIALOG_POLICY_ENV));
});

test("parseDialogPolicy: warning lists every valid value (helps the operator fix the typo)", () => {
  const warnings: string[] = [];
  parseDialogPolicy({ [DIALOG_POLICY_ENV]: "bad" }, (m) => warnings.push(m));
  for (const v of DIALOG_POLICY_VALUES) {
    assert.ok(warnings[0].includes(v), `warning should mention ${v}`);
  }
});

// ---------------------------------------------------------------------------
// DEFAULT_DIALOG_POLICY preserves T4.5 (the foundational contract)
// ---------------------------------------------------------------------------

test("DEFAULT_DIALOG_POLICY is 'ignore' (preserves M13 T4.5 launch-errors behaviour)", () => {
  // This is the single most load-bearing default in the module: under the
  // default policy the launch-errors dialog is dismissed with Ignore, exactly
  // as M13 T4.5 hard-coded. Changing this default would silently alter every
  // existing deployment's safe-mode recovery semantics.
  assert.equal(DEFAULT_DIALOG_POLICY, "ignore");
});
