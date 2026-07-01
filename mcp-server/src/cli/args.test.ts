// Tests for the CLI argument parser (src/cli/args.ts). Pure-function tests —
// no I/O, no process state.
//
// Built + run via the project test config (see package.json `test`):
//   tsc -p tsconfig.test.json  &&  node --test 'dist-test/**/*.test.js'

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  parseCliArgs,
  coerceArgValue,
  KNOWN_COMMANDS,
  type ParsedCli,
} from "./args.js";

function parse(argv: string[]): ParsedCli {
  return parseCliArgs(argv);
}

// ---------------------------------------------------------------------------
// command recognition
// ---------------------------------------------------------------------------

test("parseCliArgs: recognizes every known command", () => {
  for (const cmd of KNOWN_COMMANDS) {
    // Commands that need a second positional to be complete on their own.
    const needsSub: Record<string, string[]> = {
      "run-tool": ["run-tool", "unity_open_mcp_ping"],
      baseline: ["baseline", "create"],
      regression: ["regression", "check"],
    };
    const argv = needsSub[cmd] ?? [cmd];
    const p = parse(argv);
    assert.equal(p.command, cmd);
    assert.equal(p.error, undefined);
  }
});

test("parseCliArgs: --help short-circuits to help command", () => {
  assert.equal(parse(["--help"]).command, "help");
  assert.equal(parse(["-h"]).command, "help");
});

test("parseCliArgs: --version short-circuits to version command", () => {
  assert.equal(parse(["--version"]).command, "version");
  assert.equal(parse(["-V"]).command, "version");
});

test("parseCliArgs: unknown command is an error", () => {
  const p = parse(["bogus"]);
  assert.equal(p.command, null);
  assert.match(p.error ?? "", /Unknown command 'bogus'/);
});

test("parseCliArgs: no argv → no command (caller falls through to server)", () => {
  const p = parse([]);
  assert.equal(p.command, null);
  assert.equal(p.error, undefined);
});

// ---------------------------------------------------------------------------
// shared flags
// ---------------------------------------------------------------------------

test("parseCliArgs: --json is captured", () => {
  const p = parse(["ping", "--json"]);
  assert.equal(p.json, true);
});

test("parseCliArgs: --project / -P override", () => {
  assert.equal(parse(["ping", "--project", "/p"]).projectPath, "/p");
  assert.equal(parse(["ping", "-P", "/p"]).projectPath, "/p");
});

test("parseCliArgs: --port / -p override parses to a number", () => {
  assert.equal(parse(["ping", "--port", "19120"]).port, 19120);
  assert.equal(parse(["ping", "-p", "19120"]).port, 19120);
});

test("parseCliArgs: --port rejects non-numeric / out-of-range", () => {
  assert.match(parse(["ping", "--port", "abc"]).error ?? "", /--port/);
  assert.match(parse(["ping", "--port", "-5"]).error ?? "", /--port/);
  assert.match(parse(["ping", "--port", "0"]).error ?? "", /--port/);
});

test("parseCliArgs: --port requires a value", () => {
  assert.match(parse(["ping", "--port"]).error ?? "", /--port/);
  assert.match(parse(["ping", "--port", "--json"]).error ?? "", /--port/);
});

test("parseCliArgs: --timeout-ms and --interval-ms parse to numbers", () => {
  const p = parse(["wait-for-ready", "--timeout-ms", "5000", "--interval-ms", "250"]);
  assert.equal(p.timeoutMs, 5000);
  assert.equal(p.intervalMs, 250);
});

test("parseCliArgs: --timeout-ms rejects zero / negative / non-integer", () => {
  assert.match(parse(["ping", "--timeout-ms", "0"]).error ?? "", /--timeout-ms/);
  assert.match(parse(["ping", "--timeout-ms", "-1"]).error ?? "", /--timeout-ms/);
  assert.match(parse(["ping", "--timeout-ms", "1.5"]).error ?? "", /--timeout-ms/);
});

test("parseCliArgs: unknown flag is an error", () => {
  const p = parse(["ping", "--nonsense"]);
  assert.deepEqual(p.unknown, ["--nonsense"]);
  assert.match(p.error ?? "", /Unknown option/);
});

// ---------------------------------------------------------------------------
// run-tool arg passing
// ---------------------------------------------------------------------------

test("parseCliArgs: run-tool captures the tool name positional", () => {
  const p = parse(["run-tool", "unity_open_mcp_ping"]);
  assert.equal(p.command, "run-tool");
  assert.equal(p.toolName, "unity_open_mcp_ping");
});

test("parseCliArgs: run-tool without a tool name is an error", () => {
  const p = parse(["run-tool"]);
  assert.equal(p.command, "run-tool");
  assert.match(p.error ?? "", /requires a tool name/);
});

test("parseCliArgs: --args parses a JSON object and merges into toolArgs", () => {
  const p = parse(["run-tool", "t", "--args", '{"folder":"Assets","max":5}']);
  assert.deepEqual(p.toolArgs, { folder: "Assets", max: 5 });
});

test("parseCliArgs: --args rejects non-object JSON", () => {
  assert.match(parse(["run-tool", "t", "--args", "[1,2]"]).error ?? "", /JSON object/);
  assert.match(parse(["run-tool", "t", "--args", "42"]).error ?? "", /JSON object/);
  assert.match(parse(["run-tool", "t", "--args", "not-json"]).error ?? "", /valid JSON/);
});

test("parseCliArgs: --arg key=value sets one value (string fallback)", () => {
  const p = parse(["run-tool", "t", "--arg", "folder=Assets"]);
  assert.deepEqual(p.toolArgs, { folder: "Assets" });
});

test("parseCliArgs: --arg JSON-parses valid JSON values", () => {
  const p = parse([
    "run-tool", "t",
    "--arg", "timeout_ms=30000",
    "--arg", "include_planned=true",
    "--arg", "count=0",
  ]);
  assert.deepEqual(p.toolArgs, {
    timeout_ms: 30000,
    include_planned: true,
    count: 0,
  });
});

test("parseCliArgs: --arg is repeatable and merges", () => {
  const p = parse([
    "run-tool", "t",
    "--arg", "a=1",
    "--arg", "b=two",
  ]);
  assert.deepEqual(p.toolArgs, { a: 1, b: "two" });
});

test("parseCliArgs: --arg requires key=value shape", () => {
  assert.match(parse(["run-tool", "t", "--arg", "novalue"]).error ?? "", /key=value/);
  assert.match(parse(["run-tool", "t", "--arg", "=novalue"]).error ?? "", /empty key/);
});

test("parseCliArgs: --args and --arg merge together (--arg wins on conflict)", () => {
  const p = parse([
    "run-tool", "t",
    "--args", '{"a":1,"b":2}',
    "--arg", "b=20",
  ]);
  assert.deepEqual(p.toolArgs, { a: 1, b: 20 });
});

test("parseCliArgs: --set is an alias for --arg", () => {
  const p = parse(["run-tool", "t", "--set", "k=v"]);
  assert.deepEqual(p.toolArgs, { k: "v" });
});

test("parseCliArgs: rejects unexpected positional beyond run-tool's tool name", () => {
  const p = parse(["run-tool", "t", "extra"]);
  assert.match(p.error ?? "", /Unexpected positional/);
});

// ---------------------------------------------------------------------------
// coerceArgValue
// ---------------------------------------------------------------------------

test("coerceArgValue: parses JSON numbers / booleans / null", () => {
  assert.equal(coerceArgValue("42"), 42);
  assert.equal(coerceArgValue("3.14"), 3.14);
  assert.equal(coerceArgValue("true"), true);
  assert.equal(coerceArgValue("false"), false);
  assert.equal(coerceArgValue("null"), null);
});

test("coerceArgValue: keeps non-JSON strings as-is", () => {
  assert.equal(coerceArgValue("Assets"), "Assets");
  assert.equal(coerceArgValue("/path/to/thing"), "/path/to/thing");
});

test("coerceArgValue: a JSON string stays a string (not double-unwrapped)", () => {
  // '"foo"' is valid JSON → JSON.parse yields 'foo'. That is intentional: a
  // user who wants the literal string "foo" passes --arg key=foo.
  assert.equal(coerceArgValue('"foo"'), "foo");
});

// ---------------------------------------------------------------------------
// flag ordering / position invariance
// ---------------------------------------------------------------------------

test("parseCliArgs: flags may appear before or after the command", () => {
  assert.equal(parse(["--json", "ping"]).command, "ping");
  assert.equal(parse(["--json", "ping"]).json, true);
  assert.equal(parse(["ping", "--json"]).json, true);
});

test("parseCliArgs: --project may appear after run-tool args", () => {
  const p = parse([
    "run-tool", "unity_open_mcp_ping",
    "--project", "/proj",
    "--arg", "k=v",
  ]);
  assert.equal(p.projectPath, "/proj");
  assert.equal(p.toolName, "unity_open_mcp_ping");
  assert.deepEqual(p.toolArgs, { k: "v" });
});

// ---------------------------------------------------------------------------
// stream-events
// ---------------------------------------------------------------------------

test("parseCliArgs: stream-events recognized with no extra positionals", () => {
  const p = parse(["stream-events"]);
  assert.equal(p.command, "stream-events");
  assert.equal(p.error, undefined);
});

test("parseCliArgs: stream-events --max-events parses to a number", () => {
  const p = parse(["stream-events", "--max-events", "100"]);
  assert.equal(p.maxEvents, 100);
});

test("parseCliArgs: stream-events --follow sets the flag", () => {
  const p = parse(["stream-events", "--follow"]);
  assert.equal(p.follow, true);
});

test("parseCliArgs: stream-events --max-events rejects non-positive", () => {
  assert.match(parse(["stream-events", "--max-events", "0"]).error ?? "", /--max-events/);
  assert.match(parse(["stream-events", "--max-events", "-1"]).error ?? "", /--max-events/);
});

// ---------------------------------------------------------------------------
// verify
// ---------------------------------------------------------------------------

test("parseCliArgs: verify with no paths → empty verifyPaths (whole-project scan_all)", () => {
  const p = parse(["verify"]);
  assert.equal(p.command, "verify");
  assert.deepEqual(p.verifyPaths, []);
  assert.equal(p.verifyMode, undefined);
});

test("parseCliArgs: verify captures variadic path positionals", () => {
  const p = parse(["verify", "Assets/Prefabs", "Assets/Scripts", "--json"]);
  assert.deepEqual(p.verifyPaths, ["Assets/Prefabs", "Assets/Scripts"]);
  assert.equal(p.json, true);
});

test("parseCliArgs: verify --mode validates the value", () => {
  assert.equal(parse(["verify", "--mode", "auto"]).verifyMode, "auto");
  assert.equal(parse(["verify", "--mode", "scan-paths"]).verifyMode, "scan-paths");
  assert.equal(parse(["verify", "--mode", "validate-edit"]).verifyMode, "validate-edit");
  assert.match(parse(["verify", "--mode", "bogus"]).error ?? "", /--mode/);
});

test("parseCliArgs: verify --fail-on-severity validates the value", () => {
  assert.equal(parse(["verify", "--fail-on-severity", "warn"]).failOnSeverity, "warn");
  assert.match(parse(["verify", "--fail-on-severity", "bogus"]).error ?? "", /--fail-on-severity/);
});

test("parseCliArgs: verify --profile validates the value", () => {
  assert.equal(parse(["verify", "--profile", "balanced"]).profile, "balanced");
  assert.match(parse(["verify", "--profile", "bogus"]).error ?? "", /--profile/);
});

test("parseCliArgs: verify --include-rules / --exclude-rules split on comma", () => {
  const p = parse([
    "verify",
    "--include-rules", "missing_references,orphan_meta",
    "--exclude-rules", "duplicate_guid",
  ]);
  assert.deepEqual(p.includeRules, ["missing_references", "orphan_meta"]);
  assert.deepEqual(p.excludeRules, ["duplicate_guid"]);
});

test("parseCliArgs: verify --platform-profile validates the value", () => {
  assert.equal(parse(["verify", "--platform-profile", "mobile"]).platformProfile, "mobile");
  assert.match(parse(["verify", "--platform-profile", "bogus"]).error ?? "", /--platform-profile/);
});

// ---------------------------------------------------------------------------
// baseline / regression subcommands
// ---------------------------------------------------------------------------

test("parseCliArgs: baseline create is recognized", () => {
  const p = parse(["baseline", "create"]);
  assert.equal(p.command, "baseline");
  assert.equal(p.subcommand, "create");
  assert.equal(p.error, undefined);
});

test("parseCliArgs: baseline update is recognized", () => {
  const p = parse(["baseline", "update"]);
  assert.equal(p.subcommand, "update");
});

test("parseCliArgs: baseline without a subcommand is an error", () => {
  const p = parse(["baseline"]);
  assert.equal(p.command, "baseline");
  assert.match(p.error ?? "", /requires a subcommand/);
});

test("parseCliArgs: baseline with an invalid subcommand is an error", () => {
  const p = parse(["baseline", "bogus"]);
  assert.match(p.error ?? "", /must be 'create' or 'update'/);
});

test("parseCliArgs: baseline --baseline-path captures the path", () => {
  const p = parse(["baseline", "create", "--baseline-path", "CI/baseline.json"]);
  assert.equal(p.baselinePath, "CI/baseline.json");
});

test("parseCliArgs: regression check is recognized", () => {
  const p = parse(["regression", "check"]);
  assert.equal(p.command, "regression");
  assert.equal(p.subcommand, "check");
  assert.equal(p.error, undefined);
});

test("parseCliArgs: regression without a subcommand is an error", () => {
  const p = parse(["regression"]);
  assert.match(p.error ?? "", /requires a subcommand/);
});

test("parseCliArgs: regression with an invalid subcommand is an error", () => {
  const p = parse(["regression", "bogus"]);
  assert.match(p.error ?? "", /must be 'check'/);
});

test("parseCliArgs: regression --regression-threshold allows zero", () => {
  const p = parse(["regression", "check", "--regression-threshold", "0"]);
  assert.equal(p.regressionThreshold, 0);
});

test("parseCliArgs: regression --regression-threshold rejects negative", () => {
  assert.match(
    parse(["regression", "check", "--regression-threshold", "-1"]).error ?? "",
    /--regression-threshold/,
  );
});
