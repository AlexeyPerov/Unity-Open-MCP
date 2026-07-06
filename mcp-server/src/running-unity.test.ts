import test from "node:test";
import assert from "node:assert/strict";

import {
  parseProjectPathArg,
  isUnityCommandLine,
  splitArgs,
  splitArgsWindows,
  parsePsOutput,
  parsePowerShellLines,
  findUnityForProject,
  setUnityProcessScannerForTest,
  type UnityProcessScanner,
  type RunningUnity,
} from "./running-unity.js";

// ---------------------------------------------------------------------------
// Pure parsers — ported from hub/src-tauri/src/config/running_unity.rs tests.
// Keeping the assertions byte-for-byte aligned with the Rust suite pins the
// arg/ps/powershell parsing contract across the two implementations.
// ---------------------------------------------------------------------------

// ----- parseProjectPathArg -----

test("parseProjectPathArg: separate value form", () => {
  assert.equal(
    parseProjectPathArg(["Unity", "-projectPath", "/Users/me/Projects/MyGame"]),
    "/Users/me/Projects/MyGame",
  );
});

test("parseProjectPathArg: equals form", () => {
  assert.equal(
    parseProjectPathArg(["Unity", "-projectPath=/Users/me/Projects/MyGame"]),
    "/Users/me/Projects/MyGame",
  );
});

test("parseProjectPathArg: long --projectPath separate", () => {
  assert.equal(parseProjectPathArg(["Unity", "--projectPath", "/p"]), "/p");
});

test("parseProjectPathArg: long --projectPath equals", () => {
  assert.equal(parseProjectPathArg(["Unity", "--projectPath=/p"]), "/p");
});

test("parseProjectPathArg: strips surrounding double quotes", () => {
  // Hub-launched Windows command uses quoted paths with spaces.
  assert.equal(
    parseProjectPathArg(["Unity.exe", "-projectPath", '"C:\\Users\\me\\My Game"']),
    "C:\\Users\\me\\My Game",
  );
});

test("parseProjectPathArg: strips surrounding single quotes", () => {
  assert.equal(
    parseProjectPathArg(["Unity", "-projectPath", "'/Users/me/Has Space'"]),
    "/Users/me/Has Space",
  );
});

test("parseProjectPathArg: returns null when flag absent", () => {
  assert.equal(parseProjectPathArg(["Unity", "-batchmode", "-nographics"]), null);
});

test("parseProjectPathArg: returns null for flag at end without value", () => {
  assert.equal(parseProjectPathArg(["Unity", "-batchmode", "-projectPath"]), null);
});

test("parseProjectPathArg: takes first match only", () => {
  assert.equal(
    parseProjectPathArg(["Unity", "-projectPath", "/first", "-projectPath", "/second"]),
    "/first",
  );
});

test("parseProjectPathArg: skips preceding flags", () => {
  // Hub-launched Windows command commonly embeds -batchmode / -quit before
  // the project path; the parser must skip them.
  assert.equal(
    parseProjectPathArg([
      "Unity.exe",
      "-batchmode",
      "-nographics",
      "-projectPath",
      "/p",
      "-quit",
    ]),
    "/p",
  );
});

test("parseProjectPathArg: empty argv", () => {
  assert.equal(parseProjectPathArg([]), null);
});

// ----- isUnityCommandLine -----

test("isUnityCommandLine: matches Unity.app binary", () => {
  assert.ok(
    isUnityCommandLine(
      "/Applications/Unity/Hub/Editor/6000.0.1f1/Unity.app/Contents/MacOS/Unity -projectPath /p",
    ),
  );
});

test("isUnityCommandLine: matches bare Unity", () => {
  assert.ok(isUnityCommandLine("Unity -projectPath /p"));
});

test("isUnityCommandLine: rejects Unity Hub GUI", () => {
  // The Hub GUI and the unityhub:// URL handler are separate executables; they
  // must never be tagged as a running editor.
  assert.equal(
    isUnityCommandLine(
      "/Applications/Unity Hub.app/Contents/MacOS/Unity Hub -projectPath /p",
    ),
    false,
  );
});

test("isUnityCommandLine: rejects quoted path with wrong basename", () => {
  assert.equal(
    isUnityCommandLine('"/Applications/Unity Helper" -projectPath /p'),
    false,
  );
});

// ----- splitArgs -----

test("splitArgs: quoted path with spaces", () => {
  assert.deepEqual(
    splitArgs('Unity -projectPath "/Users/me/Has Space" -quit'),
    ["Unity", "-projectPath", "/Users/me/Has Space", "-quit"],
  );
});

test("splitArgs: unquoted args", () => {
  assert.deepEqual(splitArgs("Unity -projectPath /p -quit"), [
    "Unity",
    "-projectPath",
    "/p",
    "-quit",
  ]);
});

test("splitArgs: empty / whitespace only", () => {
  assert.deepEqual(splitArgs(""), []);
  assert.deepEqual(splitArgs("   "), []);
});

// ----- splitArgsWindows -----

test("splitArgsWindows: escaped quote inside double-quoted string", () => {
  const parts = splitArgsWindows(
    'Unity.exe -projectPath "C:\\Users\\me\\My \\"Quoted\\" Game" -quit',
  );
  assert.equal(parts[0], "Unity.exe");
  assert.equal(parts[1], "-projectPath");
  assert.equal(parts[2], 'C:\\Users\\me\\My "Quoted" Game');
  assert.equal(parts[3], "-quit");
});

test("splitArgsWindows: simple quoted path", () => {
  assert.deepEqual(
    splitArgsWindows('Unity.exe -projectPath "C:\\Users\\me\\My Game" -quit'),
    ["Unity.exe", "-projectPath", "C:\\Users\\me\\My Game", "-quit"],
  );
});

// ----- parsePsOutput -----

test("parsePsOutput: extracts pid + path, rejects Hub GUI", () => {
  const sample = [
    "1 /sbin/launchd",
    "42 /Applications/Unity/Hub/Editor/6000.0.1f1/Unity.app/Contents/MacOS/Unity -projectPath /Users/me/MyGame -batchmode",
    "100 /Applications/Unity Hub.app/Contents/MacOS/Unity Hub",
    "200 /usr/bin/some-tool -projectPath /not/unity",
  ].join("\n");
  const found = parsePsOutput(sample);
  assert.equal(found.length, 1);
  assert.equal(found[0].pid, 42);
  assert.equal(found[0].projectPath, "/Users/me/MyGame");
});

test("parsePsOutput: keeps pid when path unparseable", () => {
  // Unity launched without -projectPath (e.g. via the Hub's "Open Editor"
  // button). The scanner records the PID so a caller with a persisted launch
  // PID could still match.
  const sample =
    "5 /Applications/Unity/Hub/Editor/6000.0.1f1/Unity.app/Contents/MacOS/Unity\n";
  const found = parsePsOutput(sample);
  assert.equal(found.length, 1);
  assert.equal(found[0].pid, 5);
  assert.equal(found[0].projectPath, null);
});

test("parsePsOutput: ignores blank and unparseable lines", () => {
  const sample = "\n   \nnot-a-pid /Applications/Unity/Unity\n";
  assert.deepEqual(parsePsOutput(sample), []);
});

// ----- parsePowerShellLines -----

test("parsePowerShellLines: extracts pid + path", () => {
  const sample = '1234|Unity.exe -projectPath "C:\\Users\\me\\My Game" -quit\n';
  const found = parsePowerShellLines(sample);
  assert.equal(found.length, 1);
  assert.equal(found[0].pid, 1234);
  assert.ok(found[0].projectPath?.startsWith("C:/Users/me/My Game"));
});

test("parsePowerShellLines: keeps pid when CommandLine is null", () => {
  const found = parsePowerShellLines("9999|null\n");
  assert.equal(found.length, 1);
  assert.equal(found[0].pid, 9999);
  assert.equal(found[0].projectPath, null);
});

test("parsePowerShellLines: skips blank and unparseable", () => {
  assert.deepEqual(parsePowerShellLines("\n   \nnot-a-pid|Unity.exe\n"), []);
});

// ---------------------------------------------------------------------------
// findUnityForProject — the single-project lookup. Uses the injectable
// scanner so no real ps / PowerShell process is spawned in CI.
// ---------------------------------------------------------------------------

function makeFakeScanner(records: RunningUnity[]): UnityProcessScanner {
  return {
    scan() {
      return records;
    },
  };
}

test("findUnityForProject: returns matching pid when project is running", () => {
  const restore = setUnityProcessScannerForTest(
    makeFakeScanner([
      {
        pid: 4242,
        projectPath: "/Users/me/MyGame",
      },
    ]),
  );
  try {
    assert.deepEqual(findUnityForProject("/Users/me/MyGame"), { pid: 4242 });
  } finally {
    restore();
  }
});

test("findUnityForProject: trailing-slash tolerant", () => {
  const restore = setUnityProcessScannerForTest(
    makeFakeScanner([{ pid: 7, projectPath: "/Users/me/MyGame" }]),
  );
  try {
    assert.deepEqual(findUnityForProject("/Users/me/MyGame/"), { pid: 7 });
  } finally {
    restore();
  }
});

test("findUnityForProject: backslash path normalizes to forward slashes", () => {
  const restore = setUnityProcessScannerForTest(
    makeFakeScanner([
      { pid: 99, projectPath: "C:/Users/me/MyGame" },
    ]),
  );
  try {
    assert.deepEqual(findUnityForProject("C:\\Users\\me\\MyGame"), { pid: 99 });
  } finally {
    restore();
  }
});

test("findUnityForProject: returns null on path mismatch", () => {
  const restore = setUnityProcessScannerForTest(
    makeFakeScanner([{ pid: 1, projectPath: "/some/other/project" }]),
  );
  try {
    assert.equal(findUnityForProject("/Users/me/MyGame"), null);
  } finally {
    restore();
  }
});

test("findUnityForProject: returns null when no Unity process is running", () => {
  const restore = setUnityProcessScannerForTest(makeFakeScanner([]));
  try {
    assert.equal(findUnityForProject("/Users/me/MyGame"), null);
  } finally {
    restore();
  }
});

test("findUnityForProject: returns null for empty/undefined project path", () => {
  assert.equal(findUnityForProject(null), null);
  assert.equal(findUnityForProject(undefined), null);
  assert.equal(findUnityForProject(""), null);
});

test("findUnityForProject: returns null when scanner throws", () => {
  const restore = setUnityProcessScannerForTest({
    scan(): RunningUnity[] {
      throw new Error("scan failed");
    },
  });
  try {
    // A scanner failure must never mask the underlying offline state — return
    // null so the caller falls through to its pre-feature behavior.
    assert.equal(findUnityForProject("/Users/me/MyGame"), null);
  } finally {
    restore();
  }
});

test("findUnityForProject: known false negative — Unity without -projectPath", () => {
  // Unity opened via the Hub's "Open Editor" button (no -projectPath arg).
  // The MCP server has no persisted launch PID (unlike the Hub's lastLaunchPid),
  // so the bare-editor case cannot be matched. Documented in the plan.
  const restore = setUnityProcessScannerForTest(
    makeFakeScanner([{ pid: 50, projectPath: null }]),
  );
  try {
    assert.equal(findUnityForProject("/Users/me/MyGame"), null);
  } finally {
    restore();
  }
});
