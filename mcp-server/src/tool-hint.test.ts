// specs/feedback.md 2026-08-14 (hint regression) — string-level guard.
//
// The "name a non-default tool, carry its activation call" rule has now been
// broken twice: the suffix was added by hand at three sites, then a release
// shipped hints without it, and the agent that followed the hint found the
// tool unreachable (`ToolSearch` → "No matching deferred tools found") and
// hand-rolled AssetDatabase.Refresh + RequestScriptCompilation instead —
// exactly the behaviour recompile_scripts exists to replace.
//
// These tests are what stops a third occurrence: they assert the invariant on
// the EMITTED strings (tool descriptions + the runtime hint builders), not on
// the helper in isolation, so a site that stops calling the helper fails here.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, rm, writeFile, utimes } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { ALL_TOOLS } from "./tools/index.js";
import { toolHintReference, isDefaultVisible, activateInstruction } from "./tool-hint.js";
import { detectStaleAssembly, detectStaleLog } from "./unity-log.js";
import { groupFor, DEFAULT_ENABLED_GROUPS } from "./capabilities/tool-groups.js";

// Tools that live outside the default session surface and are named in agent-
// facing prose. Any string that mentions one of these MUST also carry the
// manage_tools activation call, or an agent cannot act on it.
const NON_DEFAULT_TOOLS_NAMED_IN_HINTS = [
  "unity_open_mcp_recompile_scripts",
  "unity_open_mcp_reimport_package",
];

test("the tools named in hints really are outside the default surface", () => {
  // Guards the premise of every assertion below: if one of these moves into a
  // default-enabled group, toolHintReference stops appending the activation
  // call and the "must contain manage_tools" assertions would be wrong.
  for (const name of NON_DEFAULT_TOOLS_NAMED_IN_HINTS) {
    const group = groupFor(name);
    assert.ok(group !== null, `${name} must have a group assignment`);
    assert.equal(
      DEFAULT_ENABLED_GROUPS.has(group!),
      false,
      `${name} is expected to be outside the default surface`,
    );
  }
});

test("toolHintReference appends the activation call for a non-default tool", () => {
  const ref = toolHintReference("unity_open_mcp_recompile_scripts");
  assert.ok(ref.includes("unity_open_mcp_recompile_scripts"));
  assert.ok(ref.includes("typed-editor"), "names the group");
  assert.ok(ref.includes("manage_tools"), "carries the activation call");
  assert.equal(ref.includes(activateInstruction("typed-editor")), true);
});

test("toolHintReference leaves a default-visible tool bare", () => {
  // compile_check is in `core` (default-enabled); read_compile_errors has no
  // group at all (always visible). Neither may grow an activation call.
  for (const name of [
    "unity_open_mcp_compile_check",
    "unity_open_mcp_read_compile_errors",
  ]) {
    assert.equal(isDefaultVisible(name), true, `${name} should be default-visible`);
    assert.equal(toolHintReference(name), name);
  }
});

test("toolHintReference leaves an unknown tool bare (never invents a group)", () => {
  assert.equal(toolHintReference("unity_open_mcp_not_a_real_tool"), "unity_open_mcp_not_a_real_tool");
});

test("every tool description naming a non-default tool carries manage_tools", () => {
  const offenders: string[] = [];
  for (const tool of ALL_TOOLS) {
    const description = tool.description ?? "";
    for (const named of NON_DEFAULT_TOOLS_NAMED_IN_HINTS) {
      if (description.includes(named) && !description.includes("manage_tools")) {
        offenders.push(`${tool.name} names ${named}`);
      }
    }
  }
  assert.deepEqual(
    offenders,
    [],
    "a tool description that names a tool outside the default surface must also " +
      "say how to activate it (use toolHintReference): " + offenders.join("; "),
  );
});

// --- Runtime hint builders --------------------------------------------------

/** Project fixture whose newest source is strictly newer than its newest DLL. */
async function makeStaleProject(): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), "uomcp-hint-"));
  const asmDir = join(root, "Library", "ScriptAssemblies");
  const srcDir = join(root, "Assets", "Scripts");
  await mkdir(asmDir, { recursive: true });
  await mkdir(srcDir, { recursive: true });

  const dll = join(asmDir, "Assembly-CSharp.dll");
  await writeFile(dll, "not-a-real-dll");
  const src = join(srcDir, "Thing.cs");
  await writeFile(src, "// source");

  // Pin explicit mtimes: DLL old, source new. Writing in order is not enough —
  // a coarse filesystem timestamp can make both equal, and the detector's
  // comparison is strict.
  const old = new Date(Date.now() - 60_000);
  const recent = new Date();
  await utimes(dll, old, old);
  await utimes(src, recent, recent);
  return root;
}

test("detectStaleAssembly's hint names recompile_scripts WITH the activation call", async () => {
  const root = await makeStaleProject();
  try {
    const result = detectStaleAssembly(root);
    assert.equal(result.staleAssembly, true, "fixture must be detected as stale");
    assert.ok(result.hint.includes("unity_open_mcp_recompile_scripts"));
    assert.ok(
      result.hint.includes("manage_tools"),
      "the stale-assembly hint must carry the activation call — this is the exact " +
        "string that shipped without it and sent an agent hand-rolling a recompile",
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("detectStaleLog's hint prescribes the same tool as the stale-assembly hint", async () => {
  const root = await makeStaleProject();
  try {
    // A log older than the cited source is the stale-log signature.
    const logPath = join(root, "Editor.log");
    await writeFile(logPath, "old log");
    const old = new Date(Date.now() - 120_000);
    await utimes(logPath, old, old);

    const result = detectStaleLog(logPath, ["Assets/Scripts/Thing.cs(1,1)"], root);
    assert.equal(result.staleLogSuspected, true, "fixture must be detected as stale");
    assert.ok(
      result.hint.includes("unity_open_mcp_recompile_scripts"),
      "one recommended path per situation — the stale-LOG hint used to name " +
        "reimport_package/compile_check while the description named recompile_scripts",
    );
    assert.ok(result.hint.includes("manage_tools"));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("no emitted hint names a non-default tool without manage_tools", async () => {
  const root = await makeStaleProject();
  try {
    const logPath = join(root, "Editor.log");
    await writeFile(logPath, "old log");
    const old = new Date(Date.now() - 120_000);
    await utimes(logPath, old, old);

    const hints = [
      detectStaleAssembly(root).hint,
      detectStaleLog(logPath, ["Assets/Scripts/Thing.cs(1,1)"], root).hint,
    ];
    for (const hint of hints) {
      for (const named of NON_DEFAULT_TOOLS_NAMED_IN_HINTS) {
        if (hint.includes(named)) {
          assert.ok(
            hint.includes("manage_tools"),
            `hint names ${named} without an activation call: ${hint}`,
          );
        }
      }
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
