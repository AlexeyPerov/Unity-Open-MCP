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

import {
  parseDismissOutput,
  LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS,
  WINDOWS_DISMISS_PS_SCRIPT,
  macosDismissAppleScript,
  MACOS_DISMISS_APPLESCRIPT,
  DISMISS_BUTTON_LABEL,
  LINUX_XDOTOOL_MISSING_PREFIX,
  UNSUPPORTED_PLATFORM_PREFIX,
  regexEscapeForXdotool,
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
};

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

test("MACOS_DISMISS_APPLESCRIPT: back-compat constant is the default-policy script", () => {
  // The deprecated export must equal the function output under the default
  // policy so legacy references keep working.
  assert.equal(
    MACOS_DISMISS_APPLESCRIPT,
    macosDismissAppleScript(DEFAULT_PROBE_OPTS),
  );
});

test("macosDismissAppleScript: dismisses launch-errors / non_matching_editor / auto_graphics_api under default policy", () => {
  const script = macosDismissAppleScript(DEFAULT_PROBE_OPTS);
  assert.ok(script.includes("dismissed:Focus:launch_errors"));
  assert.ok(script.includes("dismissed:Focus:non_matching_editor"));
  assert.ok(script.includes("dismissed:Focus:auto_graphics_api"));
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
  });
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
