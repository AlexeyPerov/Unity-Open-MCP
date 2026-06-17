// M13 T4.5 — launch-errors dialog auto-dismiss unit tests.
//
// Covers:
//   - parseDismissOutput contract (every token kind, chatter-before-token,
//     CRLF, defensive fallbacks)
//   - constants the platform scripts and bailout matcher depend on
//     (title fragments, button label, PowerShell user32 imports + BM_CLICK,
//     AppleScript Unity-process + Ignore-button targeting, xdotool regex
//     escape)
//   - readDismissConfig (env opt-out + tunable timeout/interval)
//   - pollAndDismissLaunchErrors against a fake probe (dismissed → log,
//     transient error → logged once, permanent error → bail, abort signal
//     → early exit, timeout → exit, repeated dismissals → one log each)

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  parseDismissOutput,
  LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS,
  WINDOWS_DISMISS_PS_SCRIPT,
  MACOS_DISMISS_APPLESCRIPT,
  DISMISS_BUTTON_LABEL,
  LINUX_XDOTOOL_MISSING_PREFIX,
  UNSUPPORTED_PLATFORM_PREFIX,
  regexEscapeForXdotool,
  tryDismissLaunchErrorsDialog,
  _resetXdotoolPresenceForTests,
  readDismissConfig,
  pollAndDismissLaunchErrors,
  DEFAULT_DISMISS_TIMEOUT_MS,
  DEFAULT_DISMISS_INTERVAL_MS,
  type DismissOutcome,
  type DismissPlatform,
} from "./dialog-dismiss.js";

// ---------------------------------------------------------------------------
// parseDismissOutput
// ---------------------------------------------------------------------------

test("parseDismissOutput: empty output → not-found", () => {
  assert.equal(parseDismissOutput("").kind, "not-found");
});

test('parseDismissOutput: literal "not-found" token → not-found', () => {
  assert.deepEqual(parseDismissOutput("not-found\n"), { kind: "not-found" });
});

test("parseDismissOutput: dismissed:<button> token", () => {
  assert.deepEqual(parseDismissOutput("dismissed:Ignore"), {
    kind: "dismissed",
    button: "Ignore",
  });
});

test("parseDismissOutput: dismissed token trims trailing whitespace", () => {
  assert.deepEqual(parseDismissOutput("dismissed:Ignore\n"), {
    kind: "dismissed",
    button: "Ignore",
  });
});

test("parseDismissOutput: dismissed: with empty suffix falls back to default button", () => {
  assert.deepEqual(parseDismissOutput("dismissed:"), {
    kind: "dismissed",
    button: DISMISS_BUTTON_LABEL,
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
  assert.deepEqual(parseDismissOutput("WARNING: deprecated\ndismissed:Ignore\n"), {
    kind: "dismissed",
    button: "Ignore",
  });
  assert.equal(parseDismissOutput("some chatter\nnot-found\n").kind, "not-found");
  assert.deepEqual(parseDismissOutput("chatter\nerror:permission denied\n"), {
    kind: "error",
    message: "permission denied",
  });
});

test("parseDismissOutput: handles CRLF (Windows PowerShell stdout)", () => {
  assert.deepEqual(parseDismissOutput("WARN\r\ndismissed:Ignore\r\n"), {
    kind: "dismissed",
    button: "Ignore",
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
  // Locks down the contract that current fragments are literal — a future
  // contributor adding a fragment with metacharacters gets an immediate
  // failure here pointing at the regex hazard.
  for (const frag of LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS) {
    assert.equal(regexEscapeForXdotool(frag), frag);
  }
});

// ---------------------------------------------------------------------------
// LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS
// ---------------------------------------------------------------------------

test("LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS: includes legacy + current Unity titles", () => {
  const lower = LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS.map((f) => f.toLowerCase());
  assert.ok(lower.includes("compiler errors"));
  assert.ok(lower.includes("hold on"));
  assert.ok(lower.includes("compile errors"));
  // Unity 2020.2+ renamed the launch-errors dialog to "Enter Safe Mode?" —
  // without this fragment every modern Unity (2022 LTS, 6000.x) boots past
  // the auto-dismiss path.
  assert.ok(lower.includes("safe mode"));
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

test("WINDOWS_DISMISS_PS_SCRIPT: mentions the Ignore button label", () => {
  assert.ok(WINDOWS_DISMISS_PS_SCRIPT.includes(DISMISS_BUTTON_LABEL));
});

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

test("WINDOWS_DISMISS_PS_SCRIPT: includes every documented title fragment", () => {
  for (const frag of LAUNCH_ERROR_DIALOG_TITLE_FRAGMENTS) {
    assert.ok(WINDOWS_DISMISS_PS_SCRIPT.includes(frag), `missing fragment ${frag}`);
  }
});

// ---------------------------------------------------------------------------
// MACOS_DISMISS_APPLESCRIPT
// ---------------------------------------------------------------------------

test("MACOS_DISMISS_APPLESCRIPT: clicks Ignore on a Unity process window", () => {
  assert.ok(MACOS_DISMISS_APPLESCRIPT.includes('process "Unity"'));
  assert.ok(MACOS_DISMISS_APPLESCRIPT.includes('button "Ignore"'));
  assert.ok(MACOS_DISMISS_APPLESCRIPT.includes("click button"));
});

test('MACOS_DISMISS_APPLESCRIPT: returns "not-found" when no Unity/button present', () => {
  assert.ok(MACOS_DISMISS_APPLESCRIPT.includes('return "not-found"'));
});

test("MACOS_DISMISS_APPLESCRIPT: catches AppleScript errors (loop must not abort)", () => {
  assert.ok(MACOS_DISMISS_APPLESCRIPT.includes("on error"));
});

// ---------------------------------------------------------------------------
// tryDismissLaunchErrorsDialog — guard branches (no real OS clicks)
// ---------------------------------------------------------------------------

test("tryDismissLaunchErrorsDialog: unsupported platform → error outcome", async () => {
  const result = await tryDismissLaunchErrorsDialog(
    "plan9" as unknown as DismissPlatform,
  );
  assert.equal(result.kind, "error");
  if (result.kind !== "error") return;
  assert.ok(result.message.includes(UNSUPPORTED_PLATFORM_PREFIX));
});

test("tryDismissLaunchErrorsDialog: linux with no xdotool on PATH → error mentioning xdotool", async () => {
  _resetXdotoolPresenceForTests();
  const originalPath = process.env.PATH;
  process.env.PATH = "/nonexistent/path/that/will/not/find/xdotool";
  try {
    const result = await tryDismissLaunchErrorsDialog("linux");
    // Acceptable: error (xdotool missing) OR not-found (installed system-wide
    // and no matching window). Both are valid structured outcomes — we only
    // assert the helper never throws.
    assert.ok(["error", "not-found"].includes(result.kind));
    if (result.kind === "error") {
      assert.ok(result.message.toLowerCase().includes("xdotool"));
      // The producer message must use the constant the bailout matcher reads.
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

test("readDismissConfig: enabled by default", () => {
  assert.deepEqual(readDismissConfig({}), {
    enabled: true,
    timeoutMs: DEFAULT_DISMISS_TIMEOUT_MS,
    intervalMs: DEFAULT_DISMISS_INTERVAL_MS,
  });
});

test('readDismissConfig: UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1 disables', () => {
  const cfg = readDismissConfig({
    UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS: "1",
  });
  assert.equal(cfg.enabled, false);
});

test("readDismissConfig: only the literal '1' opts out (not 'true', 'yes', etc.)", () => {
  // The opt-out is a single documented switch value — not a fuzzy boolean —
  // so accidental env leakage (e.g. 'true' from another tool) does NOT
  // silently disable the feature.
  for (const v of ["true", "yes", "0", "", "false"]) {
    const cfg = readDismissConfig({
      UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS: v,
    });
    assert.equal(cfg.enabled, true, `value ${JSON.stringify(v)} must not opt out`);
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
// pollAndDismissLaunchErrors — against a scriptable fake probe
// ---------------------------------------------------------------------------

/** Build a fake probe that replays a queued list of outcomes, looping the last. */
function makeFakeProbe(
  outcomes: DismissOutcome[],
): typeof tryDismissLaunchErrorsDialog {
  let i = 0;
  return async () => {
    i += 1;
    return outcomes[Math.min(i - 1, outcomes.length - 1)];
  };
}

test("pollAndDismissLaunchErrors: logs each dismissal once (re-entrant case emits multiple)", async () => {
  const logs: string[] = [];
  // Dismissed three times in a row (resolver-fix → dialog re-appears cycle),
  // then settles to not-found. We abort from INSIDE the probe the tick after
  // the third dismissal settles — no timer race, fully deterministic.
  let ticks = 0;
  const probe = async (): Promise<DismissOutcome> => {
    ticks += 1;
    if (ticks <= 3) return { kind: "dismissed", button: "Ignore" };
    return { kind: "not-found" };
  };
  const ac = new AbortController();
  // Let the loop drain its own queue: abort two ticks after the last
  // dismissal (not-found outcomes) so we observe all three dismiss logs and
  // confirm the loop would keep going past 3 if not aborted.
  const stopAfter = 5;

  await pollAndDismissLaunchErrors({
    timeoutMs: 5000,
    intervalMs: 1,
    platform: "darwin",
    probe: async () => {
      const r = await probe();
      if (ticks >= stopAfter) ac.abort();
      return r;
    },
    abortSignal: ac.signal,
    log: (line) => logs.push(line),
  });

  const dismissLogs = logs.filter((l) => l.includes("dismissed Unity launch-errors"));
  assert.equal(dismissLogs.length, 3, "each of the 3 dismissals logs once");
  // The log line must surface button + platform for auditability.
  assert.ok(dismissLogs[0].includes("button=Ignore"));
  assert.ok(dismissLogs[0].includes("platform=darwin"));
});

test("pollAndDismissLaunchErrors: transient error logged once then suppressed", async () => {
  const logs: string[] = [];
  const probe = makeFakeProbe([
    { kind: "error", message: "momentary osascript hiccup" },
    { kind: "error", message: "momentary osascript hiccup" },
    { kind: "error", message: "momentary osascript hiccup" },
    { kind: "dismissed", button: "Ignore" },
  ]);
  const ac = new AbortController();
  setTimeout(() => ac.abort(), 60);

  await pollAndDismissLaunchErrors({
    timeoutMs: 5000,
    intervalMs: 5,
    platform: "darwin",
    probe,
    abortSignal: ac.signal,
    log: (line) => logs.push(line),
  });

  const errLogs = logs.filter((l) => l.includes("auto-dismiss: momentary osascript hiccup"));
  assert.equal(errLogs.length, 1, "identical transient error logged once, not three times");
});

test("pollAndDismissLaunchErrors: permanent error bails out immediately (no further ticks)", async () => {
  const logs: string[] = [];
  let ticks = 0;
  const probe = async (): Promise<DismissOutcome> => {
    ticks += 1;
    return { kind: "error", message: LINUX_XDOTOOL_MISSING_PREFIX };
  };
  const ac = new AbortController();
  // If the loop ignored the permanent-error bail-out, this abort would be
  // the only exit and ticks would climb into the dozens. The bail keeps
  // ticks at 1.
  setTimeout(() => ac.abort(), 60);

  await pollAndDismissLaunchErrors({
    timeoutMs: 5000,
    intervalMs: 5,
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

test("pollAndDismissLaunchErrors: abort signal that is ALREADY aborted exits without probing", async () => {
  let ticks = 0;
  const probe = async (): Promise<DismissOutcome> => {
    ticks += 1;
    return { kind: "not-found" };
  };
  const ac = new AbortController();
  ac.abort();

  await pollAndDismissLaunchErrors({
    timeoutMs: 5000,
    intervalMs: 5,
    platform: "darwin",
    probe,
    abortSignal: ac.signal,
    log: () => {},
  });

  assert.equal(ticks, 0, "pre-aborted signal must skip the first probe entirely");
});

test("pollAndDismissLaunchErrors: abort fired mid-probe does not emit a stale dismissal log", async () => {
  const logs: string[] = [];
  // Probe resolves to dismissed, but we abort WHILE the probe is in flight —
  // applying that stale outcome would emit a misleading log. The loop must
  // re-check the abort flag after awaiting probe() and before logging.
  const ac = new AbortController();
  const probe = async (): Promise<DismissOutcome> => {
    ac.abort(); // abort during the in-flight probe
    return { kind: "dismissed", button: "Ignore" };
  };

  await pollAndDismissLaunchErrors({
    timeoutMs: 5000,
    intervalMs: 5,
    platform: "darwin",
    probe,
    abortSignal: ac.signal,
    log: (line) => logs.push(line),
  });

  assert.equal(
    logs.filter((l) => l.includes("dismissed Unity launch-errors")).length,
    0,
    "stale in-flight dismissal must not be logged after abort",
  );
});

test("pollAndDismissLaunchErrors: timeout alone exits the loop (no abort signal)", async () => {
  // When the caller does not supply an abort signal (e.g. a top-level-only
  // invocation), the loop must still terminate on timeout.
  const logs: string[] = [];
  let ticks = 0;
  const probe = async (): Promise<DismissOutcome> => {
    ticks += 1;
    return { kind: "not-found" };
  };

  await pollAndDismissLaunchErrors({
    timeoutMs: 40,
    intervalMs: 5,
    platform: "darwin",
    probe,
    log: () => {},
  });

  assert.ok(ticks >= 1, "probe ran at least once");
  // not-found never logs anything.
  assert.equal(logs.length, 0);
});

test("pollAndDismissLaunchErrors: respects minimum interval clamp (interval below 50ms)", async () => {
  // An interval of 1ms must not busy-loop faster than the 50ms floor.
  const start = Date.now();
  let ticks = 0;
  const probe = async (): Promise<DismissOutcome> => {
    ticks += 1;
    return { kind: "not-found" };
  };

  await pollAndDismissLaunchErrors({
    timeoutMs: 120,
    intervalMs: 1,
    platform: "darwin",
    probe,
    log: () => {},
  });

  const elapsed = Date.now() - start;
  // At a 50ms floor over ~120ms budget we expect at most ~3 ticks; if the
  // clamp failed we would see dozens. Assert a generous upper bound.
  assert.ok(ticks <= 5, `interval clamp failed: ${ticks} ticks in ${elapsed}ms`);
});
