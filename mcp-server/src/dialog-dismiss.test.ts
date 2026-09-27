// M13 T4.5 + M23 Plan 2 — startup dialog auto-dismiss unit tests.
//
// Covers:
//   - parseDismissOutput contract (dismissed/blocked/not-found/error, the
//     new `:kind` suffix, back-compat single-field form, chatter-before-token)
//   - constants the platform scripts depend on (legacy title fragments,
//     DISMISS_BUTTON_LABEL, prefix markers, xdotool regex escape)
//   - readDismissConfig (kill-switch, policy, project-upgrade opt-in,
//     timeout/interval tuning, manual disables the loop)
//   - pollAndDismissDialogs against a fake probe (dismissed → log with
//     dialog+policy, blocked → logged once per kind, transient error →
//     logged once, permanent error → bail, abort signal → early exit,
//     timeout → exit, repeated dismissals → one log each)
//
// The pure policy taxonomy + per-kind button tables are exercised in
// dialog-policy.test.ts; this suite covers the polling loop + platform
// script structure + config wiring.

import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import {
  DIALOG_FOCUSED_BUTTON_TOKENS,
  DIALOG_TITLE_FRAGMENTS,
  UNITY_EDITOR_WINDOW_TITLE_MARKER,
  normalizeDialogLabel,
  preferenceTokensForPolicy,
  type DialogKind,
  type DialogPolicy,
} from "./dialog-policy.js";

import {
  parseDismissOutput,
  LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS,
  WINDOWS_DISMISS_PS_SCRIPT,
  macosDismissAppleScript,
  macosDialogKindBlock,
  macosTitleMatch,
  MACOS_NORM_LABEL_HANDLER,
  focusDialogAction,
  linuxFocusAction,
  DISMISS_BUTTON_LABEL,
  LINUX_XDOTOOL_MISSING_PREFIX,
  UNSUPPORTED_PLATFORM_PREFIX,
  regexEscapeForXdotool,
  xdotoolTitlePattern,
  tryDismissDialog,
  _resetXdotoolPresenceForTests,
  readDismissConfig,
  pollAndDismissDialogs,
  DEFAULT_DISMISS_TIMEOUT_MS,
  DEFAULT_DISMISS_INTERVAL_MS,
  type DismissOutcome,
  type DismissPlatform,
} from "./dialog-dismiss.js";

const DEFAULT_PROBE_OPTS = {
  platform: "darwin" as DismissPlatform,
  policy: "ignore" as const,
  allowProjectUpgrade: false,
  allowUnsavedSceneDismiss: false,
  allowVersionMismatch: false,
};

const ALL_POLICIES: readonly DialogPolicy[] = [
  "auto",
  "ignore",
  "recover",
  "safe-mode",
  "cancel",
  "manual",
];

// ---------------------------------------------------------------------------
// parseDismissOutput
// ---------------------------------------------------------------------------

test("parseDismissOutput: empty output → not-found", () => {
  assert.equal(parseDismissOutput("").kind, "not-found");
});

test('parseDismissOutput: literal "not-found" token → not-found', () => {
  assert.deepEqual(parseDismissOutput("not-found\n"), { kind: "not-found" });
});

test("parseDismissOutput: dismissed:<button>:<kind> token", () => {
  assert.deepEqual(parseDismissOutput("dismissed:Ignore:launch_errors"), {
    kind: "dismissed",
    button: "Ignore",
    dialog: "launch_errors",
  });
});

test("parseDismissOutput: dismissed token trims trailing whitespace", () => {
  assert.deepEqual(parseDismissOutput("dismissed:Continue:non_matching_editor\n"), {
    kind: "dismissed",
    button: "Continue",
    dialog: "non_matching_editor",
  });
});

test("parseDismissOutput: dismissed with multi-word button label preserves the full label", () => {
  // Button labels can contain colons in theory; the parser takes everything
  // before the LAST colon as the button. This pins that contract.
  assert.deepEqual(parseDismissOutput("dismissed:Load Recovery:launch_errors"), {
    kind: "dismissed",
    button: "Load Recovery",
    dialog: "launch_errors",
  });
});

test("parseDismissOutput: dismissed back-compat single-field form (T4.5) → launch_errors", () => {
  // The old T4.5 producers wrote `dismissed:<button>` with no kind. The
  // generalized parser must still accept it (treats it as launch_errors).
  assert.deepEqual(parseDismissOutput("dismissed:Ignore"), {
    kind: "dismissed",
    button: "Ignore",
    dialog: "launch_errors",
  });
});

test("parseDismissOutput: dismissed: with empty suffix falls back to default button", () => {
  assert.deepEqual(parseDismissOutput("dismissed:"), {
    kind: "dismissed",
    button: DISMISS_BUTTON_LABEL,
    dialog: "launch_errors",
  });
});

test("parseDismissOutput: blocked:<kind> token", () => {
  assert.deepEqual(parseDismissOutput("blocked:project_upgrade"), {
    kind: "blocked",
    dialog: "project_upgrade",
    message: "Policy declined to dismiss project_upgrade dialog",
  });
});

test("parseDismissOutput: error:<message> token", () => {
  assert.deepEqual(parseDismissOutput("error:Accessibility permission missing"), {
    kind: "error",
    message: "Accessibility permission missing",
  });
});

test("parseDismissOutput: unrecognised output → not-found (defensive)", () => {
  assert.equal(parseDismissOutput("garbage banana 42").kind, "not-found");
});

test("parseDismissOutput: classifies on the LAST non-empty line (chatter before token)", () => {
  assert.deepEqual(parseDismissOutput("WARNING: deprecated\ndismissed:Ignore:launch_errors\n"), {
    kind: "dismissed",
    button: "Ignore",
    dialog: "launch_errors",
  });
  assert.equal(parseDismissOutput("some chatter\nnot-found\n").kind, "not-found");
  assert.deepEqual(parseDismissOutput("chatter\nerror:permission denied\n"), {
    kind: "error",
    message: "permission denied",
  });
});

test("parseDismissOutput: handles CRLF (Windows PowerShell stdout)", () => {
  assert.deepEqual(parseDismissOutput("WARN\r\ndismissed:Ignore:launch_errors\r\n"), {
    kind: "dismissed",
    button: "Ignore",
    dialog: "launch_errors",
  });
});

// ---------------------------------------------------------------------------
// regexEscapeForXdotool
// ---------------------------------------------------------------------------

test("regexEscapeForXdotool: escapes regex metacharacters", () => {
  assert.equal(regexEscapeForXdotool("Hold On"), "Hold On");
  assert.equal(regexEscapeForXdotool("(Hold On)"), "\\(Hold On\\)");
  assert.equal(regexEscapeForXdotool("Compiler Errors v2.0+"), "Compiler Errors v2\\.0\\+");
  assert.equal(regexEscapeForXdotool("a*b?c[d]"), "a\\*b\\?c\\[d\\]");
});

test("regexEscapeForXdotool: every current title fragment is regex-safe", () => {
  for (const frag of LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS) {
    // Fragments are human-readable spellings; assert they have no unescaped
    // metacharacters so xdotool search treats them literally.
    const escaped = regexEscapeForXdotool(frag);
    // Re-running the escaper on the escaped form is idempotent for safe input.
    assert.equal(regexEscapeForXdotool(escaped), escaped);
  }
});

// ---------------------------------------------------------------------------
// LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS (back-compat superset of T4.5)
// ---------------------------------------------------------------------------

test("LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS: includes legacy + current Unity titles", () => {
  const lower = LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS.map((f) => f.toLowerCase());
  assert.ok(lower.some((f) => f.includes("compiler errors")));
  assert.ok(lower.some((f) => f.includes("hold on")));
  // Unity 2020.2+ renamed the launch-errors dialog to "Enter Safe Mode?" —
  // without this fragment every modern Unity (2022 LTS, 6000.x) boots past
  // the auto-dismiss path.
  assert.ok(lower.some((f) => f.includes("safe mode")));
});

test("LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS: matches the real Unity 2022.3+ title", () => {
  const realTitle = "Enter Safe Mode?";
  const matched = LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS.some((frag) =>
    realTitle.toLowerCase().includes(frag.toLowerCase()),
  );
  assert.equal(matched, true);
});

test("LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS: non-empty (zero fragments would never match)", () => {
  assert.ok(LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS.length > 0);
});

// ---------------------------------------------------------------------------
// WINDOWS_DISMISS_PS_SCRIPT
// ---------------------------------------------------------------------------

test("WINDOWS_DISMISS_PS_SCRIPT: uses Win32 BM_CLICK (0x00F5), not a synthetic mouse event", () => {
  assert.ok(WINDOWS_DISMISS_PS_SCRIPT.includes("0x00F5"));
});

test("WINDOWS_DISMISS_PS_SCRIPT: imports the user32 functions the strategy depends on", () => {
  for (const fn of [
    "EnumWindows",
    "EnumChildWindows",
    "GetWindowTextW",
    "GetClassNameW",
    "GetWindowThreadProcessId",
    "SendMessageW",
  ]) {
    assert.ok(WINDOWS_DISMISS_PS_SCRIPT.includes(fn), `missing ${fn}`);
  }
});

test("WINDOWS_DISMISS_PS_SCRIPT: reads the token table from stdin (so the script body is policy-agnostic)", () => {
  // The generalized script must NOT hard-code a button label — it reads the
  // per-kind-per-policy token table via stdin. This pins that contract so a
  // future edit does not silently regress to a single hard-coded button.
  assert.ok(WINDOWS_DISMISS_PS_SCRIPT.includes("[Console]::In.ReadToEnd()"));
});

test("WINDOWS_DISMISS_PS_SCRIPT: emits the dismissed:<button>:<kind> contract token", () => {
  assert.ok(WINDOWS_DISMISS_PS_SCRIPT.includes('"dismissed:"'));
});

// ---------------------------------------------------------------------------
// macosDismissAppleScript
// ---------------------------------------------------------------------------

test("macosDismissAppleScript: clicks on a Unity process window", () => {
  const script = macosDismissAppleScript(DEFAULT_PROBE_OPTS);
  assert.ok(script.includes('process "Unity"'));
  // key code 36 = the Return key. AppleScript treats bare `return` as the
  // return-from-handler keyword, so the script MUST use `key code 36` to
  // press the focused button.
  assert.ok(script.includes("key code 36"));
});

test('macosDismissAppleScript: returns "not-found" when no Unity process is present', () => {
  const script = macosDismissAppleScript(DEFAULT_PROBE_OPTS);
  assert.ok(script.includes('return "not-found"'));
});

test("macosDismissAppleScript: never auto-clicks a project-upgrade dialog (blocked outcome)", () => {
  // The default policy must NOT click Confirm on a Project Upgrade dialog.
  // macOS path detects the title and emits blocked:project_upgrade instead.
  const script = macosDismissAppleScript(DEFAULT_PROBE_OPTS);
  assert.ok(script.includes("Upgrade"));
  assert.ok(script.includes('"blocked:" & "project_upgrade"'));
});

test("macosDismissAppleScript: catches AppleScript errors (loop must not abort)", () => {
  assert.ok(macosDismissAppleScript(DEFAULT_PROBE_OPTS).includes("on error"));
});

test("macosDismissAppleScript: dismisses unsaved-scene sheets when opt-in is set", () => {
  const script = macosDismissAppleScript({
    ...DEFAULT_PROBE_OPTS,
    allowUnsavedSceneDismiss: true,
    policy: "cancel",
  });
  assert.ok(script.includes("sheets of w"), "must probe window sheets");
  assert.ok(script.includes("Don't Save"), "cancel policy clicks Don't Save");
  // `st` is reserved in AppleScript (ordinal suffix, as in `1st`); using it as
  // a loop variable makes the whole script fail to compile.
  assert.ok(!/\bst\b/.test(script), "must not use the reserved identifier `st`");
});

test("macosDismissAppleScript: blocks non_matching_editor under default policy", () => {
  const script = macosDismissAppleScript(DEFAULT_PROBE_OPTS);
  assert.ok(script.includes("dismissed:Focus:launch_errors"));
  assert.ok(script.includes('return "blocked:" & "non_matching_editor"'));
  assert.ok(script.includes("dismissed:Focus:auto_graphics_api"));
});

test("macosDismissAppleScript: mismatch opt-in allows non_matching_editor", () => {
  const script = macosDismissAppleScript({
    ...DEFAULT_PROBE_OPTS,
    allowVersionMismatch: true,
  });
  assert.ok(script.includes("dismissed:Focus:non_matching_editor"));
  assert.ok(!script.includes('"blocked:" & "non_matching_editor"'));
});

test("macosDismissAppleScript: non_matching_editor action follows policy × mismatch opt-in", () => {
  const gen = (policy: DialogPolicy, allowVersionMismatch: boolean) =>
    macosDismissAppleScript({ ...DEFAULT_PROBE_OPTS, policy, allowVersionMismatch });
  const focus = "dismissed:Focus:non_matching_editor";
  const blocked = '"blocked:" & "non_matching_editor"';
  const named = '":non_matching_editor"';
  for (const policy of ["auto", "ignore", "recover"] as const) {
    const off = gen(policy, false);
    assert.ok(off.includes(blocked), `${policy} without opt-in → blocked`);
    assert.ok(!off.includes(focus), `${policy} without opt-in must not Return-click`);
    const on = gen(policy, true);
    assert.ok(on.includes(focus), `${policy} with opt-in → Return-click Continue`);
    assert.ok(!on.includes(blocked), `${policy} with opt-in is not blocked`);
  }
  for (const allow of [false, true]) {
    // cancel: fail fast — named Quit/Cancel click, never Return on Continue.
    const cancel = gen("cancel", allow);
    assert.ok(!cancel.includes(focus), `cancel (opt-in=${allow}) must not Return-click`);
    assert.ok(cancel.includes('repeat with tok in {"quit", "cancel", "close", "no"}'));
    assert.ok(cancel.includes(named));
    assert.ok(cancel.includes(blocked), "cancel falls back to blocked when no named button");
    // safe-mode: Quit/Cancel as well.
    const safe = gen("safe-mode", allow);
    assert.ok(!safe.includes(focus), `safe-mode (opt-in=${allow}) must not Return-click`);
    assert.ok(safe.includes('repeat with tok in {"quit", "cancel"}'));
    // manual: no non_matching_editor handling at all.
    const manual = gen("manual", allow);
    assert.ok(!manual.includes(focus), `manual (opt-in=${allow}) never acts`);
    assert.ok(!manual.includes(named));
    assert.ok(!manual.includes(blocked));
  }
});

// ---------------------------------------------------------------------------
// macosDialogKindBlock — script shape per policy × focus-classified kind
// ---------------------------------------------------------------------------

type FocusAction = ReturnType<typeof focusDialogAction>;

/**
 * Assert the macOS block for `kind` under every policy has the shape its
 * expected action demands: `focus` presses Return; `named` clicks the policy
 * tokens by name in priority order and falls back to `blocked:`, never
 * pressing Return; `skip` emits nothing actionable.
 */
function assertMacosBlockMatrix(
  kind: Parameters<typeof focusDialogAction>[0],
  expected: Record<DialogPolicy, FocusAction>,
  flags: Partial<typeof DEFAULT_PROBE_OPTS> = {},
): void {
  for (const policy of ALL_POLICIES) {
    const opts = { ...DEFAULT_PROBE_OPTS, ...flags, policy };
    const label = `${kind} under ${policy} (${JSON.stringify(flags)})`;
    assert.equal(focusDialogAction(kind, opts), expected[policy], label);
    const block = macosDialogKindBlock(kind, opts);
    const focus = `return "dismissed:Focus:${kind}"`;
    const blocked = `return "blocked:" & "${kind}"`;
    switch (expected[policy]) {
      case "focus":
        assert.ok(block.includes("key code 36"), `${label}: presses Return`);
        assert.ok(block.includes(focus), label);
        assert.ok(!block.includes("click btn"), `${label}: no named click`);
        break;
      case "named": {
        const tokens = preferenceTokensForPolicy(
          kind,
          policy,
          opts.allowProjectUpgrade,
          opts.allowUnsavedSceneDismiss,
          opts.allowVersionMismatch,
        );
        assert.ok(tokens, label);
        assert.ok(
          block.includes(`repeat with tok in {${tokens.map((t) => `"${t}"`).join(", ")}}`),
          `${label}: tries the policy tokens in priority order`,
        );
        assert.ok(block.includes("if my normLabel(bt) is (contents of tok) then"), label);
        assert.ok(block.includes("click btn"), `${label}: clicks the named button`);
        assert.ok(block.includes(`return "dismissed:" & bt & ":${kind}"`), label);
        assert.ok(block.includes(blocked), `${label}: blocked when the named button is missing`);
        assert.ok(!block.includes("key code 36"), `${label}: never presses Return`);
        assert.ok(!block.includes(focus), label);
        break;
      }
      case "blocked":
        assert.ok(block.includes(blocked), label);
        assert.ok(!block.includes("key code 36"), label);
        assert.ok(!block.includes("click btn"), label);
        break;
      case "skip":
        assert.equal(block, `-- policy declines ${kind}; skip`, label);
        break;
    }
  }
}

test("macosDialogKindBlock: launch_errors clicks Enter Safe Mode / Quit by name, Return only for Ignore", () => {
  assertMacosBlockMatrix("launch_errors", {
    auto: "focus",
    ignore: "focus",
    // Return would press the focused Ignore, not Enter Safe Mode / Quit.
    recover: "named",
    "safe-mode": "named",
    cancel: "named",
    manual: "skip",
  });
  const safe = macosDialogKindBlock("launch_errors", { ...DEFAULT_PROBE_OPTS, policy: "safe-mode" });
  assert.ok(safe.includes('repeat with tok in {"entersafemode", "safemode"}'));
  const cancel = macosDialogKindBlock("launch_errors", { ...DEFAULT_PROBE_OPTS, policy: "cancel" });
  assert.ok(cancel.includes('repeat with tok in {"quit", "cancel", "close", "no"}'));
});

test("macosDialogKindBlock: auto_graphics_api presses Return for OK, clicks Quit/Cancel by name under cancel", () => {
  assertMacosBlockMatrix("auto_graphics_api", {
    auto: "focus",
    ignore: "focus",
    recover: "focus",
    "safe-mode": "skip",
    cancel: "named",
    manual: "skip",
  });
});

test("macosDialogKindBlock: scene_modified_externally presses Return for Reload, clicks Quit/Cancel by name under cancel", () => {
  assertMacosBlockMatrix("scene_modified_externally", {
    auto: "focus",
    ignore: "focus",
    recover: "focus",
    "safe-mode": "skip",
    cancel: "named",
    manual: "skip",
  });
});

test("macosDialogKindBlock: non_matching_editor follows the shared action", () => {
  const off = { allowVersionMismatch: false };
  assertMacosBlockMatrix(
    "non_matching_editor",
    {
      auto: "blocked",
      ignore: "blocked",
      recover: "blocked",
      "safe-mode": "named",
      cancel: "named",
      manual: "skip",
    },
    off,
  );
  assertMacosBlockMatrix(
    "non_matching_editor",
    {
      auto: "focus",
      ignore: "focus",
      recover: "focus",
      "safe-mode": "named",
      cancel: "named",
      manual: "skip",
    },
    { allowVersionMismatch: true },
  );
});

test("macosDismissAppleScript: cancel policy never presses Return on any dialog", () => {
  for (const allow of [false, true]) {
    const script = macosDismissAppleScript({
      ...DEFAULT_PROBE_OPTS,
      policy: "cancel",
      allowProjectUpgrade: allow,
      allowUnsavedSceneDismiss: allow,
      allowVersionMismatch: allow,
    });
    assert.ok(!script.includes("key code 36"), `cancel (opt-ins=${allow})`);
    for (const kind of ["launch_errors", "auto_graphics_api", "scene_modified_externally"]) {
      assert.ok(script.includes(`return "blocked:" & "${kind}"`), `${kind} falls back to blocked`);
    }
  }
});

test("macosDismissAppleScript: defines the normLabel handler the named clicks call", () => {
  const script = macosDismissAppleScript({ ...DEFAULT_PROBE_OPTS, policy: "recover" });
  assert.ok(script.includes("my normLabel(bt)"));
  assert.ok(script.includes("on normLabel(s)"));
  assert.ok(script.includes("end normLabel"));
});

// Real Unity dialog titles per focus-classified kind, and main Editor window
// titles whose normalized form contains a dialog fragment.
const REAL_DIALOG_TITLES: readonly [string, DialogKind][] = [
  ["Enter Safe Mode?", "launch_errors"],
  ["Hold On", "launch_errors"],
  ["Compiler Errors on Launch", "launch_errors"],
  ["Opening Project in Non-Matching Editor Installation", "non_matching_editor"],
  ["Opening Project in Non Matching Editor Installation", "non_matching_editor"],
  ["Auto Graphics API Notice", "auto_graphics_api"],
  ["Scene has been modified externally", "scene_modified_externally"],
];
const EDITOR_WINDOW_TITLES: readonly string[] = [
  "MyGame - SAFE MODE - Unity 6.3 (6000.3.21f1) <Metal>",
  "HoldOn - Main - macOS - Unity 6.3 (6000.3.21f1) <Metal>",
  "Demo - SceneModifiedTest - Linux - Unity 6000.0.23f1 <Vulkan>",
];
const MAC_TITLE_KINDS: readonly DialogKind[] = [
  "launch_errors",
  "non_matching_editor",
  "auto_graphics_api",
  "scene_modified_externally",
];

test("macosTitleMatch: matches the normalized title, never the raw one", () => {
  for (const kind of MAC_TITLE_KINDS) {
    const cond = macosTitleMatch(kind);
    // AppleScript `contains` ignores case but not spaces, so a raw-title match
    // against a space-free fragment could never fire on a real title.
    assert.ok(!cond.includes("wt contains"), `${kind}: ${cond}`);
    assert.match(cond, /^\(wn contains "[a-z0-9]+"\)( or \(wn contains "[a-z0-9]+"\))*$/, kind);
  }
  for (const frag of DIALOG_TITLE_FRAGMENTS.launch_errors) {
    assert.ok(macosTitleMatch("launch_errors").includes(`(wn contains "${frag}")`), frag);
  }
});

test("macosDismissAppleScript: normalizes each window title and skips the main Editor window", () => {
  for (const policy of ALL_POLICIES) {
    const script = macosDismissAppleScript({ ...DEFAULT_PROBE_OPTS, policy });
    assert.ok(script.includes("set wn to my normLabel(wt)"), policy);
    assert.ok(
      script.includes(`if not (wt contains "${UNITY_EDITOR_WINDOW_TITLE_MARKER}") then`),
      policy,
    );
    for (const frag of Object.values(DIALOG_TITLE_FRAGMENTS).flat()) {
      assert.ok(!script.includes(`wt contains "${frag}"`), `${policy}: raw match on ${frag}`);
    }
  }
});

test("macosDismissAppleScript: unsaved-scene sheets are scanned outside the Editor-window guard", () => {
  const script = macosDismissAppleScript({
    ...DEFAULT_PROBE_OPTS,
    allowUnsavedSceneDismiss: true,
  });
  // The guard closes right after the last title-classified kind; the sheet
  // scan (Unity 6 attaches the save prompt to the main Editor window) follows.
  const guardEnd = script.indexOf(
    "end if",
    script.lastIndexOf('return "dismissed:Focus:scene_modified_externally"'),
  );
  const closeGuard = script.indexOf("end if", guardEnd + 1);
  assert.ok(closeGuard > 0);
  assert.ok(script.indexOf("sheets of w") > closeGuard, "sheet scan runs for the Editor window too");
});

test("macosDialogKindBlock: focus presses Return only when the focused button is present", () => {
  for (const kind of MAC_TITLE_KINDS) {
    const opts = { ...DEFAULT_PROBE_OPTS, allowVersionMismatch: true };
    assert.equal(focusDialogAction(kind, opts), "focus", kind);
    const block = macosDialogKindBlock(kind, opts);
    const gate = `if my normLabel(bt) is "${DIALOG_FOCUSED_BUTTON_TOKENS[kind]}" then`;
    assert.ok(block.includes("repeat with btn in buttons of w"), kind);
    assert.ok(block.includes(gate), kind);
    assert.ok(block.indexOf(gate) < block.indexOf("key code 36"), `${kind}: gate before Return`);
  }
});

// Runs the generated title conditions through osascript, so the test proves
// the AppleScript semantics (case-insensitive, not space-insensitive) rather
// than the script's text.
const hasOsascript =
  process.platform === "darwin" && spawnSync("osascript", ["-e", "return 1"]).status === 0;

test(
  "macOS title matching: real dialog titles classify, main Editor window titles do not (osascript)",
  { skip: hasOsascript ? false : "osascript unavailable" },
  () => {
    const classify = `
on run argv
  set wt to item 1 of argv
  set wn to my normLabel(wt)
  if wt contains "${UNITY_EDITOR_WINDOW_TITLE_MARKER}" then return "editor"
${MAC_TITLE_KINDS.map((k) => `  if ${macosTitleMatch(k)} then return "${k}"`).join("\n")}
  return "none"
end run
${MACOS_NORM_LABEL_HANDLER}`;
    const run = (title: string): string =>
      execFileSync("osascript", ["-e", classify, title], { encoding: "utf8" }).trim();
    for (const [title, kind] of REAL_DIALOG_TITLES) {
      assert.equal(run(title), kind, title);
    }
    for (const title of EDITOR_WINDOW_TITLES) {
      assert.equal(run(title), "editor", title);
    }
    assert.equal(run("Inspector"), "none");
  },
);

test("xdotoolTitlePattern: matches exactly the titles whose normalized form contains the fragment", () => {
  assert.equal(
    xdotoolTitlePattern("holdon"),
    "[hH][^A-Za-z0-9]*[oO][^A-Za-z0-9]*[lL][^A-Za-z0-9]*[dD][^A-Za-z0-9]*[oO][^A-Za-z0-9]*[nN]",
  );
  const titles = [
    ...REAL_DIALOG_TITLES.map(([t]) => t),
    ...EDITOR_WINDOW_TITLES,
    "Project Upgrade Required",
    "Scene(s) Have Been Modified",
    "Safe-Moder",
    "Inspector",
  ];
  // xdotool compiles the pattern as a POSIX ERE; it uses only bracket
  // expressions and `*`, which mean the same in a JS RegExp.
  for (const frag of Object.values(DIALOG_TITLE_FRAGMENTS).flat()) {
    const re = new RegExp(xdotoolTitlePattern(frag));
    for (const title of titles) {
      assert.equal(
        re.test(title),
        normalizeDialogLabel(title).includes(frag),
        `${frag} vs ${JSON.stringify(title)}`,
      );
    }
  }
  assert.ok(new RegExp(xdotoolTitlePattern("safemode")).test("Enter Safe Mode?"));
  assert.ok(!new RegExp(regexEscapeForXdotool("safemode")).test("Enter Safe Mode?"));
});

test("WINDOWS_DISMISS_PS_SCRIPT: skips the main Editor window before classifying", () => {
  const skip = `if (title.IndexOf("${UNITY_EDITOR_WINDOW_TITLE_MARKER}", StringComparison.OrdinalIgnoreCase) >= 0) continue;`;
  assert.ok(WINDOWS_DISMISS_PS_SCRIPT.includes(skip));
  assert.ok(WINDOWS_DISMISS_PS_SCRIPT.indexOf(skip) < WINDOWS_DISMISS_PS_SCRIPT.indexOf("var norm = Norm(title);"));
});

// ---------------------------------------------------------------------------
// focusDialogAction / linuxFocusAction — policy × opt-ins
// ---------------------------------------------------------------------------

test("focusDialogAction: named/focus split follows the kind's focused button", () => {
  const kinds = Object.keys(DIALOG_FOCUSED_BUTTON_TOKENS) as (keyof typeof DIALOG_FOCUSED_BUTTON_TOKENS)[];
  for (const kind of kinds) {
    for (const policy of ALL_POLICIES) {
      for (const allow of [false, true]) {
        const opts = {
          ...DEFAULT_PROBE_OPTS,
          policy,
          allowProjectUpgrade: allow,
          allowUnsavedSceneDismiss: allow,
          allowVersionMismatch: allow,
        };
        const action = focusDialogAction(kind, opts);
        const tokens = preferenceTokensForPolicy(kind, policy, allow, allow, allow);
        const label = `${kind} under ${policy} (opt-ins=${allow})`;
        if (action === "focus") assert.equal(tokens?.[0], DIALOG_FOCUSED_BUTTON_TOKENS[kind], label);
        if (action === "named") {
          assert.ok(tokens && tokens.length > 0, label);
          assert.notEqual(tokens[0], DIALOG_FOCUSED_BUTTON_TOKENS[kind], label);
        }
        if (action === "skip") assert.equal(tokens, null, label);
        // Linux folds `named` into `blocked`: xdotool cannot click by name.
        const linux = linuxFocusAction(kind, opts);
        assert.equal(linux, action === "named" ? "blocked" : action, label);
      }
    }
  }
});

test("linuxFocusAction: non_matching_editor presses Return only for Continue with opt-in", () => {
  const expected: Record<DialogPolicy, [off: string, on: string]> = {
    auto: ["blocked", "focus"],
    ignore: ["blocked", "focus"],
    recover: ["blocked", "focus"],
    // xdotool cannot click the named Quit/Cancel button and the focused
    // button is Continue, so refusal policies report blocked.
    "safe-mode": ["blocked", "blocked"],
    cancel: ["blocked", "blocked"],
    manual: ["skip", "skip"],
  };
  for (const policy of ALL_POLICIES) {
    const [off, on] = expected[policy];
    assert.equal(
      linuxFocusAction("non_matching_editor", { ...DEFAULT_PROBE_OPTS, policy, allowVersionMismatch: false }),
      off,
      `${policy} without opt-in`,
    );
    assert.equal(
      linuxFocusAction("non_matching_editor", { ...DEFAULT_PROBE_OPTS, policy, allowVersionMismatch: true }),
      on,
      `${policy} with opt-in`,
    );
  }
});

type LinuxAction = ReturnType<typeof linuxFocusAction>;

function assertLinuxMatrix(
  kind: Parameters<typeof linuxFocusAction>[0],
  expected: Record<DialogPolicy, LinuxAction>,
  flags: Partial<typeof DEFAULT_PROBE_OPTS> = {},
): void {
  for (const policy of ALL_POLICIES) {
    assert.equal(
      linuxFocusAction(kind, { ...DEFAULT_PROBE_OPTS, ...flags, policy }),
      expected[policy],
      `${kind} under ${policy} (${JSON.stringify(flags)})`,
    );
  }
}

test("linuxFocusAction: launch_errors presses Return only when the policy picks the focused Ignore", () => {
  assertLinuxMatrix("launch_errors", {
    auto: "focus",
    ignore: "focus",
    // Enter Safe Mode / Quit are not the focused button; Return would press
    // Ignore instead, so report blocked for a human.
    recover: "blocked",
    "safe-mode": "blocked",
    cancel: "blocked",
    manual: "skip",
  });
});

test("linuxFocusAction: auto_graphics_api presses Return only for the focused OK", () => {
  assertLinuxMatrix("auto_graphics_api", {
    auto: "focus",
    ignore: "focus",
    recover: "focus",
    "safe-mode": "skip",
    cancel: "blocked",
    manual: "skip",
  });
});

test("linuxFocusAction: scene_modified_externally presses Return only for the focused Reload", () => {
  assertLinuxMatrix("scene_modified_externally", {
    auto: "focus",
    ignore: "focus",
    recover: "focus",
    "safe-mode": "skip",
    cancel: "blocked",
    manual: "skip",
  });
});

test("linuxFocusAction: project_upgrade is blocked without opt-in; with it, only the focused Confirm", () => {
  assertLinuxMatrix("project_upgrade", {
    auto: "blocked",
    ignore: "blocked",
    recover: "blocked",
    "safe-mode": "blocked",
    cancel: "blocked",
    manual: "skip",
  });
  assertLinuxMatrix(
    "project_upgrade",
    {
      auto: "focus",
      ignore: "focus",
      recover: "focus",
      "safe-mode": "skip",
      cancel: "blocked",
      manual: "skip",
    },
    { allowProjectUpgrade: true },
  );
});

test("linuxFocusAction: unsaved_scene_changes never gets a Return press", () => {
  assertLinuxMatrix("unsaved_scene_changes", {
    auto: "blocked",
    ignore: "blocked",
    recover: "blocked",
    "safe-mode": "blocked",
    cancel: "blocked",
    manual: "skip",
  });
  // No focused button is relied on for this destructive prompt, so even the
  // opt-in cannot make a Return press select the policy's choice.
  assertLinuxMatrix(
    "unsaved_scene_changes",
    {
      auto: "blocked",
      ignore: "blocked",
      recover: "blocked",
      "safe-mode": "skip",
      cancel: "blocked",
      manual: "skip",
    },
    { allowUnsavedSceneDismiss: true },
  );
});

test("linuxFocusAction: focus implies the policy's first choice is the focused button", () => {
  const kinds = Object.keys(DIALOG_FOCUSED_BUTTON_TOKENS) as (keyof typeof DIALOG_FOCUSED_BUTTON_TOKENS)[];
  for (const kind of kinds) {
    for (const policy of ALL_POLICIES) {
      for (const allow of [false, true]) {
        const flags = {
          allowProjectUpgrade: allow,
          allowUnsavedSceneDismiss: allow,
          allowVersionMismatch: allow,
        };
        if (linuxFocusAction(kind, { ...DEFAULT_PROBE_OPTS, ...flags, policy }) !== "focus") continue;
        const tokens = preferenceTokensForPolicy(kind, policy, allow, allow, allow);
        assert.equal(tokens?.[0], DIALOG_FOCUSED_BUTTON_TOKENS[kind], `${kind} under ${policy} (opt-ins=${allow})`);
      }
    }
  }
});

// ---------------------------------------------------------------------------
// tryDismissDialog — guard branches (no real OS clicks)
// ---------------------------------------------------------------------------

test("tryDismissDialog: unsupported platform → error outcome", async () => {
  const result = await tryDismissDialog({
    platform: "plan9" as unknown as DismissPlatform,
    policy: "ignore",
    allowProjectUpgrade: false,
    allowUnsavedSceneDismiss: false,
    allowVersionMismatch: false,
  });
  assert.equal(result.kind, "error");
  if (result.kind !== "error") return;
  assert.ok(result.message.includes(UNSUPPORTED_PLATFORM_PREFIX));
});

test("tryDismissDialog: linux with no xdotool on PATH → error mentioning xdotool", async () => {
  _resetXdotoolPresenceForTests();
  const originalPath = process.env.PATH;
  process.env.PATH = "/nonexistent/path/that/will/not/find/xdotool";
  try {
    const result = await tryDismissDialog({
      platform: "linux",
      policy: "ignore",
      allowProjectUpgrade: false,
      allowUnsavedSceneDismiss: false,
      allowVersionMismatch: false,
    });
    assert.ok(["error", "not-found"].includes(result.kind));
    if (result.kind === "error") {
      assert.ok(result.message.toLowerCase().includes("xdotool"));
      assert.ok(result.message.includes(LINUX_XDOTOOL_MISSING_PREFIX));
    }
  } finally {
    process.env.PATH = originalPath;
    _resetXdotoolPresenceForTests();
  }
});

// ---------------------------------------------------------------------------
// readDismissConfig
// ---------------------------------------------------------------------------

test("readDismissConfig: enabled by default with policy=ignore", () => {
  assert.deepEqual(readDismissConfig({}), {
    enabled: true,
    timeoutMs: DEFAULT_DISMISS_TIMEOUT_MS,
    intervalMs: DEFAULT_DISMISS_INTERVAL_MS,
    policy: "ignore",
    allowProjectUpgrade: false,
    allowUnsavedSceneDismiss: false,
    allowVersionMismatch: false,
  });
});

test("readDismissConfig: UNITY_OPEN_MCP_ALLOW_VERSION_MISMATCH=1 opts in", () => {
  assert.equal(
    readDismissConfig({ UNITY_OPEN_MCP_ALLOW_VERSION_MISMATCH: "1" }).allowVersionMismatch,
    true,
  );
  assert.equal(
    readDismissConfig({ UNITY_OPEN_MCP_ALLOW_VERSION_MISMATCH: "true" }).allowVersionMismatch,
    false,
  );
});

test('readDismissConfig: UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1 disables (kill-switch)', () => {
  const cfg = readDismissConfig({
    UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS: "1",
  });
  assert.equal(cfg.enabled, false);
  // Policy is still recorded even when the kill-switch is off, so callers
  // can tell "operator opted out entirely" from "operator chose manual".
  assert.equal(cfg.policy, "ignore");
});

test('readDismissConfig: policy=manual disables the loop (manual == no clicks)', () => {
  const cfg = readDismissConfig({
    UNITY_OPEN_MCP_DIALOG_POLICY: "manual",
  });
  assert.equal(cfg.enabled, false);
  assert.equal(cfg.policy, "manual");
});

test("readDismissConfig: every valid policy value round-trips", () => {
  for (const p of ["auto", "ignore", "recover", "safe-mode", "cancel"] as const) {
    const cfg = readDismissConfig({ UNITY_OPEN_MCP_DIALOG_POLICY: p });
    assert.equal(cfg.policy, p);
    assert.equal(cfg.enabled, true, `${p} should keep the loop enabled`);
  }
});

test("readDismissConfig: UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE=1 opts in", () => {
  const cfg = readDismissConfig({ UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE: "1" });
  assert.equal(cfg.allowProjectUpgrade, true);
  // Off by default for any other value.
  assert.equal(
    readDismissConfig({ UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE: "true" }).allowProjectUpgrade,
    false,
  );
});

test("readDismissConfig: only the literal '1' opts out / opts in (not fuzzy booleans)", () => {
  for (const v of ["true", "yes", "0", "", "false"]) {
    const cfg = readDismissConfig({
      UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS: v,
      UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE: v,
    });
    assert.equal(cfg.enabled, true, `kill-switch value ${JSON.stringify(v)} must not disable`);
    assert.equal(cfg.allowProjectUpgrade, false, `upgrade value ${JSON.stringify(v)} must not opt in`);
  }
});

test("readDismissConfig: timeout/interval env overrides honored", () => {
  const cfg = readDismissConfig({
    UNITY_OPEN_MCP_DISMISS_TIMEOUT_MS: "7000",
    UNITY_OPEN_MCP_DISMISS_INTERVAL_MS: "250",
  });
  assert.equal(cfg.timeoutMs, 7000);
  assert.equal(cfg.intervalMs, 250);
});

test("readDismissConfig: non-positive / NaN overrides fall back to defaults", () => {
  for (const v of ["0", "-5", "abc", " "]) {
    const cfg = readDismissConfig({
      UNITY_OPEN_MCP_DISMISS_TIMEOUT_MS: v,
      UNITY_OPEN_MCP_DISMISS_INTERVAL_MS: v,
    });
    assert.equal(cfg.timeoutMs, DEFAULT_DISMISS_TIMEOUT_MS);
    assert.equal(cfg.intervalMs, DEFAULT_DISMISS_INTERVAL_MS);
  }
});

// ---------------------------------------------------------------------------
// pollAndDismissDialogs — against a scriptable fake probe
// ---------------------------------------------------------------------------

/** Build a fake probe that replays a queued list of outcomes, looping the last. */
function makeFakeProbe(
  outcomes: DismissOutcome[],
): typeof tryDismissDialog {
  let i = 0;
  return async () => {
    i += 1;
    return outcomes[Math.min(i - 1, outcomes.length - 1)];
  };
}

const LOOP_OPTS = {
  timeoutMs: 5000,
  intervalMs: 1,
  policy: "ignore" as const,
  allowProjectUpgrade: false,
  allowUnsavedSceneDismiss: false,
  allowVersionMismatch: false,
};

test("pollAndDismissDialogs: logs each dismissal once with dialog + policy", async () => {
  const logs: string[] = [];
  let ticks = 0;
  const probe = async (): Promise<DismissOutcome> => {
    ticks += 1;
    if (ticks <= 3) {
      return { kind: "dismissed", button: "Ignore", dialog: "launch_errors" };
    }
    return { kind: "not-found" };
  };
  const ac = new AbortController();
  const stopAfter = 5;

  await pollAndDismissDialogs({
    ...LOOP_OPTS,
    platform: "darwin",
    probe: async () => {
      const r = await probe();
      if (ticks >= stopAfter) ac.abort();
      return r;
    },
    abortSignal: ac.signal,
    log: (line) => logs.push(line),
  });

  const dismissLogs = logs.filter((l) => l.includes("dismissed Unity"));
  assert.equal(dismissLogs.length, 3, "each of the 3 dismissals logs once");
  // The log line must surface dialog + button + policy for auditability.
  assert.ok(dismissLogs[0].includes("launch_errors"));
  assert.ok(dismissLogs[0].includes("button=Ignore"));
  assert.ok(dismissLogs[0].includes("policy=ignore"));
  assert.ok(dismissLogs[0].includes("platform=darwin"));
});

test("pollAndDismissDialogs: blocked project_upgrade logged once per kind, then keeps polling", async () => {
  const logs: string[] = [];
  const probe = makeFakeProbe([
    { kind: "blocked", dialog: "project_upgrade", message: "declined" },
    { kind: "blocked", dialog: "project_upgrade", message: "declined" },
    { kind: "blocked", dialog: "project_upgrade", message: "declined" },
    { kind: "not-found" },
  ]);
  const ac = new AbortController();
  setTimeout(() => ac.abort(), 60);

  await pollAndDismissDialogs({
    ...LOOP_OPTS,
    platform: "darwin",
    probe,
    abortSignal: ac.signal,
    log: (line) => logs.push(line),
  });

  const blockedLogs = logs.filter((l) => l.includes("blocked on project_upgrade"));
  assert.equal(
    blockedLogs.length,
    1,
    "identical blocked outcome logged once per kind, not three times",
  );
  // The audit line must surface the opt-in env var so the operator knows how
  // to permit the upgrade if they intended to.
  assert.ok(blockedLogs[0].includes("UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE"));
});

test("pollAndDismissDialogs: transient error logged once then suppressed", async () => {
  const logs: string[] = [];
  const probe = makeFakeProbe([
    { kind: "error", message: "momentary osascript hiccup" },
    { kind: "error", message: "momentary osascript hiccup" },
    { kind: "error", message: "momentary osascript hiccup" },
    { kind: "dismissed", button: "Ignore", dialog: "launch_errors" },
  ]);
  const ac = new AbortController();
  setTimeout(() => ac.abort(), 60);

  await pollAndDismissDialogs({
    ...LOOP_OPTS,
    platform: "darwin",
    probe,
    abortSignal: ac.signal,
    log: (line) => logs.push(line),
  });

  const errLogs = logs.filter((l) => l.includes("auto-dismiss: momentary osascript hiccup"));
  assert.equal(errLogs.length, 1, "identical transient error logged once, not three times");
});

test("pollAndDismissDialogs: permanent error bails out immediately (no further ticks)", async () => {
  const logs: string[] = [];
  let ticks = 0;
  const probe = async (): Promise<DismissOutcome> => {
    ticks += 1;
    return { kind: "error", message: LINUX_XDOTOOL_MISSING_PREFIX };
  };
  const ac = new AbortController();
  setTimeout(() => ac.abort(), 60);

  await pollAndDismissDialogs({
    ...LOOP_OPTS,
    platform: "linux",
    probe,
    abortSignal: ac.signal,
    log: (line) => logs.push(line),
  });

  assert.equal(ticks, 1, "permanent error must bail after first probe");
  assert.ok(
    logs.some((l) => l.includes(LINUX_XDOTOOL_MISSING_PREFIX)),
    "permanent error logged once",
  );
});

test("pollAndDismissDialogs: abort signal that is ALREADY aborted exits without probing", async () => {
  let ticks = 0;
  const probe = async (): Promise<DismissOutcome> => {
    ticks += 1;
    return { kind: "not-found" };
  };
  const ac = new AbortController();
  ac.abort();

  await pollAndDismissDialogs({
    ...LOOP_OPTS,
    platform: "darwin",
    probe,
    abortSignal: ac.signal,
    log: () => {},
  });

  assert.equal(ticks, 0, "pre-aborted signal must skip the first probe entirely");
});

test("pollAndDismissDialogs: abort fired mid-probe does not emit a stale dismissal log", async () => {
  const logs: string[] = [];
  const ac = new AbortController();
  const probe = async (): Promise<DismissOutcome> => {
    ac.abort(); // abort during the in-flight probe
    return { kind: "dismissed", button: "Ignore", dialog: "launch_errors" };
  };

  await pollAndDismissDialogs({
    ...LOOP_OPTS,
    platform: "darwin",
    probe,
    abortSignal: ac.signal,
    log: (line) => logs.push(line),
  });

  assert.equal(
    logs.filter((l) => l.includes("dismissed Unity")).length,
    0,
    "stale in-flight dismissal must not be logged after abort",
  );
});

test("pollAndDismissDialogs: timeout alone exits the loop (no abort signal)", async () => {
  const logs: string[] = [];
  let ticks = 0;
  const probe = async (): Promise<DismissOutcome> => {
    ticks += 1;
    return { kind: "not-found" };
  };

  await pollAndDismissDialogs({
    ...LOOP_OPTS,
    timeoutMs: 40,
    platform: "darwin",
    probe,
    log: () => {},
  });

  assert.ok(ticks >= 1, "probe ran at least once");
  assert.equal(logs.length, 0);
});

test("pollAndDismissDialogs: respects minimum interval clamp (interval below 50ms)", async () => {
  const start = Date.now();
  let ticks = 0;
  const probe = async (): Promise<DismissOutcome> => {
    ticks += 1;
    return { kind: "not-found" };
  };

  await pollAndDismissDialogs({
    ...LOOP_OPTS,
    timeoutMs: 120,
    intervalMs: 1,
    platform: "darwin",
    probe,
    log: () => {},
  });

  const elapsed = Date.now() - start;
  void elapsed;
  assert.ok(ticks <= 5, `interval clamp failed: ${ticks} ticks`);
});

// ---------------------------------------------------------------------------
// SIGKILL escalation watchdog (fd-leak review 2026-08-19)
//
// execFile's `timeout` sends a single SIGTERM; a wedged probe that traps it
// never fires its callback, so pollAndDismissDialogs would hang forever with
// the child + its pipes alive. armSigkillEscalation escalates to SIGKILL.
// ---------------------------------------------------------------------------

test("armSigkillEscalation: SIGKILLs a probe that ignores its timeout SIGTERM", async (t) => {
  if (process.platform === "win32") t.skip("POSIX signal semantics");
  const { spawn } = await import("node:child_process");
  const { armSigkillEscalation } = await import("./dialog-dismiss.js");

  // The wedge case: a child that traps SIGTERM and refuses to die. execFile's
  // timeout behavior is simulated by sending the SIGTERM at `timeoutMs`. The
  // child announces readiness so the SIGTERM can't race the handler install.
  const child = spawn(
    process.execPath,
    ["-e", "process.on('SIGTERM', () => {}); console.log('ready'); setInterval(() => {}, 1000);"],
    { stdio: ["ignore", "pipe", "ignore"] },
  );
  await new Promise<void>((resolve, reject) => {
    const fail = setTimeout(() => reject(new Error("probe child never became ready")), 10_000);
    child.stdout?.on("data", () => { clearTimeout(fail); resolve(); });
    child.on("close", () => { clearTimeout(fail); reject(new Error("probe child exited before ready")); });
  });
  let settled = false;
  const disarm = armSigkillEscalation(child, 300, () => settled);
  try {
    const outcome = await new Promise<{ signal: string | null }>((resolve, reject) => {
      const fail = setTimeout(
        () => reject(new Error("watchdog did not SIGKILL the wedged probe in time")),
        10_000,
      );
      child.on("close", (_code, signal) => {
        clearTimeout(fail);
        resolve({ signal: signal ?? null });
      });
      // Simulate execFile's timeout SIGTERM (which this child ignores).
      setTimeout(() => {
        try { child.kill("SIGTERM"); } catch { /* dead */ }
      }, 300);
    });
    assert.equal(outcome.signal, "SIGKILL", "unblockable escalation must fire");
  } finally {
    settled = true;
    disarm();
    try { child.kill("SIGKILL"); } catch { /* already dead */ }
  }
});

test("armSigkillEscalation: disarmed watchdog leaves a settled probe alone", async () => {
  const { armSigkillEscalation } = await import("./dialog-dismiss.js");
  // A fake "child" — the disarmed watchdog is never fired, so kill is never
  // called; assert via the returned disarm being callable and the isSettled
  // gate preventing any signal on a live child.
  const { spawn } = await import("node:child_process");
  const child = spawn(
    process.execPath,
    ["-e", "setTimeout(() => {}, 400);"],
    { stdio: ["ignore", "ignore", "ignore"] },
  );
  let settled = false;
  const disarm = armSigkillEscalation(child, 100, () => settled);
  // Settle + disarm well before the watchdog tick; the child exits on its own.
  settled = true;
  disarm();
  await new Promise((resolve, reject) => {
    const fail = setTimeout(() => reject(new Error("child did not exit on its own")), 10_000);
    child.on("close", () => {
      clearTimeout(fail);
      resolve(null);
    });
  });
});
