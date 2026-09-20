import { test, type TestContext } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { packagePins, runSetupCommand, type SetupReport } from "./setup-command.js";

test("build bundles the canonical core skill bytes", async () => {
  const here = dirname(fileURLToPath(import.meta.url));
  const canonical = await readFile(
    join(here, "..", "..", "..", "skills", "unity-open-mcp", "SKILL.md"),
  );
  const bundled = await readFile(
    join(here, "..", "..", "dist", "skill", "SKILL.md"),
  );
  assert.deepEqual(bundled, canonical);
});

async function fixture(t: TestContext) {
  const root = await mkdtemp(join(tmpdir(), "unity-open-mcp-setup-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  for (const directory of ["Assets", "Packages", "ProjectSettings"]) {
    await mkdir(join(root, directory), { recursive: true });
  }
  const manifestPath = join(root, "Packages", "manifest.json");
  await writeFile(
    manifestPath,
    JSON.stringify({ dependencies: { "com.unity.test-framework": "1.1.0" } }, null, 2) + "\n",
  );
  const skillSourcePath = join(root, "source-skill.md");
  const skillBytes = Buffer.from("# Unity Open MCP\n\nbyte-for-byte fixture\n");
  await writeFile(skillSourcePath, skillBytes);
  return { root, manifestPath, skillSourcePath, skillBytes };
}

test("setup installs pins, Cursor config, and an exact skill byte copy", async (t) => {
  const f = await fixture(t);
  const configPath = join(f.root, ".cursor", "mcp.json");
  await mkdir(join(f.root, ".cursor"), { recursive: true });
  await writeFile(configPath, JSON.stringify({
    theme: "dark",
    mcpServers: {
      sibling: { command: "other" },
      "unity-open-mcp": {
        command: "old",
        args: [],
        env: { KEEP_ME: "yes", UNITY_PROJECT_PATH: "/old" },
      },
    },
  }));

  const result = await runSetupCommand({
    version: "9.8.7",
    projectPath: `${f.root}/`,
    client: "cursor",
    skipSkill: false,
    dryRun: false,
    skillSourcePath: f.skillSourcePath,
  });
  assert.equal(result.exitCode, 0);

  const manifest = JSON.parse(await readFile(f.manifestPath, "utf8"));
  const pins = packagePins("9.8.7");
  assert.equal(manifest.dependencies["com.alexeyperov.unity-open-mcp-bridge"], pins.bridge);
  assert.equal(manifest.dependencies["com.alexeyperov.unity-open-mcp-verify"], pins.verify);
  assert.equal(manifest.dependencies["com.unity.test-framework"], "1.1.0");

  const config = JSON.parse(await readFile(configPath, "utf8"));
  assert.equal(config.theme, "dark");
  assert.deepEqual(config.mcpServers.sibling, { command: "other" });
  assert.deepEqual(config.mcpServers["unity-open-mcp"].args, [
    "-y",
    "unity-open-mcp@9.8.7",
  ]);
  assert.equal(config.mcpServers["unity-open-mcp"].env.KEEP_ME, "yes");
  assert.equal(config.mcpServers["unity-open-mcp"].env.UNITY_PROJECT_PATH, f.root);

  const copied = await readFile(join(f.root, ".cursor", "skills", "unity-open-mcp", "SKILL.md"));
  assert.deepEqual(copied, f.skillBytes);
  const report = result.json as any;
  assert.equal(report.version, "9.8.7");
  assert.equal(report.skill.bytes, f.skillBytes.byteLength);
  assert.equal(report.skill.lines, 3);
});

test("setup creates a missing Cursor project config", async (t) => {
  const f = await fixture(t);
  const result = await runSetupCommand({
    version: "1.2.3",
    projectPath: f.root,
    client: "cursor",
    skipSkill: true,
    dryRun: false,
  });
  assert.equal(result.exitCode, 0);
  const config = JSON.parse(
    await readFile(join(f.root, ".cursor", "mcp.json"), "utf8"),
  );
  assert.equal(
    config.mcpServers["unity-open-mcp"].env.UNITY_PROJECT_PATH,
    f.root,
  );
});

test("setup overwrites stale pins and creates an OpenCode envelope", async (t) => {
  const f = await fixture(t);
  await writeFile(f.manifestPath, JSON.stringify({ dependencies: {
    "com.alexeyperov.unity-open-mcp-bridge": "file:../bridge",
    "com.alexeyperov.unity-open-mcp-verify": "https://old#verify-v0.6.1",
    unrelated: "keep",
  } }));
  await writeFile(join(f.root, "opencode.json"), JSON.stringify({
    $schema: "https://opencode.ai/config.json",
    mcp: {
      sibling: { type: "remote" },
      "unity-open-mcp": { environment: { EXTRA: "kept" } },
    },
  }));

  const result = await runSetupCommand({
    version: "1.2.3",
    projectPath: f.root,
    client: "opencode",
    skipSkill: true,
    dryRun: false,
  });
  assert.equal(result.exitCode, 0);
  const config = JSON.parse(await readFile(join(f.root, "opencode.json"), "utf8"));
  assert.equal(config.mcp.sibling.type, "remote");
  assert.deepEqual(config.mcp["unity-open-mcp"].command, [
    "npx", "-y", "unity-open-mcp@1.2.3",
  ]);
  assert.equal(config.mcp["unity-open-mcp"].environment.EXTRA, "kept");
  assert.equal((result.json as any).skill.skipped, true);
});

test("setup dry-run reports paths without writing any target", async (t) => {
  const f = await fixture(t);
  const before = await readFile(f.manifestPath, "utf8");
  const result = await runSetupCommand({
    version: "1.2.3",
    projectPath: f.root,
    client: "claude",
    skipSkill: false,
    dryRun: true,
    skillSourcePath: f.skillSourcePath,
  });
  assert.equal(result.exitCode, 0);
  assert.equal(await readFile(f.manifestPath, "utf8"), before);
  await assert.rejects(readFile(join(f.root, ".mcp.json")), { code: "ENOENT" });
  await assert.rejects(
    readFile(join(f.root, ".claude", "skills", "unity-open-mcp", "SKILL.md")),
    { code: "ENOENT" },
  );
  assert.equal((result.json as any).dryRun, true);
  assert.equal((result.json as any).manifest.written, false);
});

test("setup --skip-skill leaves an existing skill untouched", async (t) => {
  const f = await fixture(t);
  const target = join(f.root, ".agents", "skills", "unity-open-mcp", "SKILL.md");
  await mkdir(join(f.root, ".agents", "skills", "unity-open-mcp"), { recursive: true });
  await writeFile(target, "existing\n");
  const result = await runSetupCommand({
    version: "1.2.3",
    projectPath: f.root,
    client: "agents",
    skipSkill: true,
    dryRun: false,
  });
  assert.equal(result.exitCode, 0);
  assert.equal(await readFile(target, "utf8"), "existing\n");
});

test("setup validation errors use exit 2 and list known clients", async (t) => {
  const f = await fixture(t);
  const missingProject = await runSetupCommand({
    version: "1.2.3",
    projectPath: undefined,
    client: "cursor",
    skipSkill: true,
    dryRun: false,
  });
  assert.equal(missingProject.exitCode, 2);
  assert.equal(missingProject.errorLabel, "missing_project");

  const missingClient = await runSetupCommand({
    version: "1.2.3",
    projectPath: f.root,
    client: undefined,
    skipSkill: true,
    dryRun: false,
  });
  assert.equal(missingClient.exitCode, 2);
  assert.equal(missingClient.errorLabel, "missing_client");

  const relative = await runSetupCommand({
    version: "1.2.3",
    projectPath: "relative/project",
    client: "cursor",
    skipSkill: true,
    dryRun: false,
  });
  assert.equal(relative.exitCode, 2);
  assert.equal(relative.errorLabel, "project_not_absolute");

  const unknown = await runSetupCommand({
    version: "1.2.3",
    projectPath: f.root,
    client: "mystery",
    skipSkill: true,
    dryRun: false,
  });
  assert.equal(unknown.exitCode, 2);
  assert.match(unknown.human, /Known ids: cursor/);

  const unsupported = await runSetupCommand({
    version: "1.2.3",
    projectPath: f.root,
    client: "cline",
    skipSkill: true,
    dryRun: false,
  });
  assert.equal(unsupported.exitCode, 2);
  assert.match(unsupported.human, /Configure MCP manually; skill-only is not enough/);
});

test("setup parse failures use exit 1", async (t) => {
  const f = await fixture(t);
  await writeFile(f.manifestPath, "{not json");
  const result = await runSetupCommand({
    version: "1.2.3",
    projectPath: f.root,
    client: "cursor",
    skipSkill: true,
    dryRun: false,
  });
  assert.equal(result.exitCode, 1);
  assert.equal(result.errorLabel, "invalid_json");
});

// --- portable / monorepo config --------------------------------------------

/** Monorepo fixture: `<repo>/Client` is the Unity project, `<repo>` the workspace. */
async function monorepoFixture(t: TestContext) {
  const workspace = await mkdtemp(join(tmpdir(), "unity-open-mcp-monorepo-"));
  t.after(() => rm(workspace, { recursive: true, force: true }));
  const project = join(workspace, "Client");
  for (const directory of ["Assets", "Packages", "ProjectSettings"]) {
    await mkdir(join(project, directory), { recursive: true });
  }
  await writeFile(
    join(project, "Packages", "manifest.json"),
    JSON.stringify({ dependencies: {} }, null, 2) + "\n",
  );
  const skillSourcePath = join(workspace, "source-skill.md");
  await writeFile(skillSourcePath, "# skill\n");
  return { workspace, project, skillSourcePath };
}

function setupOpts(
  f: { project: string; skillSourcePath: string },
  overrides: Record<string, unknown>,
) {
  return {
    version: "1.2.3",
    projectPath: f.project,
    client: "cursor",
    skipSkill: true,
    dryRun: false,
    skillSourcePath: f.skillSourcePath,
    ...overrides,
  } as Parameters<typeof runSetupCommand>[0];
}

test("monorepo Cursor setup writes a committable ${workspaceFolder} snippet", async (t) => {
  const f = await monorepoFixture(t);
  const result = await runSetupCommand(
    setupOpts(f, { layout: "monorepo", unitySubpath: "Client" }),
  );
  assert.equal(result.exitCode, 0);
  const report = result.json as SetupReport;
  assert.equal(report.portable, true);
  assert.equal(report.layout, "monorepo");
  assert.equal(report.unitySubpath, "Client");
  assert.equal(report.configStrategy, "interpolation");
  assert.equal(report.workspace, f.workspace);

  // Config lands at the repo root, not inside the Unity project.
  const configPath = join(f.workspace, ".cursor", "mcp.json");
  assert.equal(report.mcpConfig.path, configPath);
  const body = await readFile(configPath, "utf8");
  assert.ok(!body.includes(f.workspace), "no machine path in the committed snippet");
  const config = JSON.parse(body);
  assert.equal(
    config.mcpServers["unity-open-mcp"].env.UNITY_PROJECT_PATH,
    "${workspaceFolder}/Client",
  );
  assert.deepEqual(config.mcpServers["unity-open-mcp"].args, [
    "-y",
    "unity-open-mcp@1.2.3",
  ]);

  // UPM pins still go to the Unity project, not the workspace.
  const manifest = JSON.parse(
    await readFile(join(f.project, "Packages", "manifest.json"), "utf8"),
  );
  assert.equal(
    manifest.dependencies["com.alexeyperov.unity-open-mcp-bridge"],
    packagePins("1.2.3").bridge,
  );
});

test("monorepo Claude Code setup writes args-only resolution flags", async (t) => {
  const f = await monorepoFixture(t);
  const result = await runSetupCommand(
    setupOpts(f, { client: "claude", layout: "monorepo", unitySubpath: "Client" }),
  );
  assert.equal(result.exitCode, 0);
  const report = result.json as SetupReport;
  assert.equal(report.configStrategy, "args");
  const config = JSON.parse(await readFile(join(f.workspace, ".mcp.json"), "utf8"));
  const entry = config.mcpServers["unity-open-mcp"];
  assert.deepEqual(entry.args, [
    "-y",
    "unity-open-mcp@1.2.3",
    "--project-from-cwd",
    "--unity-subpath",
    "Client",
  ]);
  assert.deepEqual(entry.env, {});
});

test("--workspace derives the subpath, and --unity-subpath derives the workspace", async (t) => {
  const f = await monorepoFixture(t);
  const fromWorkspace = await runSetupCommand(
    setupOpts(f, { workspacePath: f.workspace, dryRun: true }),
  );
  assert.equal((fromWorkspace.json as SetupReport).unitySubpath, "Client");
  assert.equal((fromWorkspace.json as SetupReport).layout, "monorepo");

  const fromSubpath = await runSetupCommand(
    setupOpts(f, { unitySubpath: "Client", dryRun: true }),
  );
  assert.equal((fromSubpath.json as SetupReport).workspace, f.workspace);
});

test("a dry-run monorepo report contains no absolute path in the snippet", async (t) => {
  const f = await monorepoFixture(t);
  const result = await runSetupCommand(
    setupOpts(f, { layout: "monorepo", unitySubpath: "Client", dryRun: true }),
  );
  const report = result.json as SetupReport;
  assert.equal(report.mcpConfig.written, false);
  assert.ok(!JSON.stringify(report.mcpConfig.entry).includes(f.workspace));
});

test("a portable re-run replaces the absolute path left by an earlier run", async (t) => {
  const f = await monorepoFixture(t);
  const absolute = await runSetupCommand(
    setupOpts(f, { workspacePath: f.workspace, portable: false }),
  );
  assert.equal((absolute.json as SetupReport).portable, false);
  let config = JSON.parse(
    await readFile(join(f.workspace, ".cursor", "mcp.json"), "utf8"),
  );
  assert.equal(config.mcpServers["unity-open-mcp"].env.UNITY_PROJECT_PATH, f.project);

  const portable = await runSetupCommand(
    setupOpts(f, { workspacePath: f.workspace, portable: true }),
  );
  assert.equal((portable.json as SetupReport).portable, true);
  config = JSON.parse(await readFile(join(f.workspace, ".cursor", "mcp.json"), "utf8"));
  assert.equal(
    config.mcpServers["unity-open-mcp"].env.UNITY_PROJECT_PATH,
    "${workspaceFolder}/Client",
  );
});

test("an args-strategy re-run drops a stale absolute UNITY_PROJECT_PATH", async (t) => {
  const f = await monorepoFixture(t);
  await runSetupCommand(
    setupOpts(f, { client: "claude", workspacePath: f.workspace, portable: false }),
  );
  await runSetupCommand(
    setupOpts(f, { client: "claude", workspacePath: f.workspace, portable: true }),
  );
  const config = JSON.parse(await readFile(join(f.workspace, ".mcp.json"), "utf8"));
  const entry = config.mcpServers["unity-open-mcp"];
  assert.equal(entry.env.UNITY_PROJECT_PATH, undefined);
  assert.ok(entry.args.includes("--project-from-cwd"));
});

test("--wrapper writes an executable, version-pinned wrapper script", async (t) => {
  const f = await monorepoFixture(t);
  const result = await runSetupCommand(
    setupOpts(f, { layout: "monorepo", unitySubpath: "Client", wrapper: true }),
  );
  const report = result.json as SetupReport;
  const wrapperPath = join(f.workspace, "scripts", "mcp", "unity-open-mcp.sh");
  assert.equal(report.wrapper.path, wrapperPath);
  assert.equal(report.wrapper.written, true);
  const body = await readFile(wrapperPath, "utf8");
  assert.match(body, /unity-open-mcp@1\.2\.3/);
  assert.ok(!body.includes(f.workspace), "the wrapper carries no machine path");
});

test("the unity-root layout keeps the absolute path by default (regression)", async (t) => {
  const f = await monorepoFixture(t);
  const result = await runSetupCommand(setupOpts(f, {}));
  const report = result.json as SetupReport;
  assert.equal(report.layout, "unity-root");
  assert.equal(report.portable, false);
  assert.equal(report.workspace, f.project);
  const config = JSON.parse(
    await readFile(join(f.project, ".cursor", "mcp.json"), "utf8"),
  );
  assert.equal(config.mcpServers["unity-open-mcp"].env.UNITY_PROJECT_PATH, f.project);
});

test("--portable on a unity-root layout emits ${workspaceFolder}", async (t) => {
  const f = await monorepoFixture(t);
  const result = await runSetupCommand(setupOpts(f, { portable: true, dryRun: true }));
  const entry = (result.json as SetupReport).mcpConfig.entry as {
    env: Record<string, string>;
  };
  assert.equal(entry.env.UNITY_PROJECT_PATH, "${workspaceFolder}");
});

test("layout and subpath conflicts are usage errors", async (t) => {
  const f = await monorepoFixture(t);

  const missingSubpath = await runSetupCommand(setupOpts(f, { layout: "monorepo" }));
  assert.equal(missingSubpath.exitCode, 2);
  assert.equal(missingSubpath.errorLabel, "missing_unity_subpath");

  const conflict = await runSetupCommand(
    setupOpts(f, { layout: "unity-root", unitySubpath: "Client" }),
  );
  assert.equal(conflict.exitCode, 2);
  assert.equal(conflict.errorLabel, "layout_conflict");

  const mismatch = await runSetupCommand(
    setupOpts(f, { workspacePath: f.workspace, unitySubpath: "Server" }),
  );
  assert.equal(mismatch.exitCode, 2);
  assert.equal(mismatch.errorLabel, "subpath_mismatch");

  const outside = await runSetupCommand(
    setupOpts(f, { workspacePath: join(f.workspace, "Client", "Assets") }),
  );
  assert.equal(outside.exitCode, 2);
  assert.equal(outside.errorLabel, "project_outside_workspace");

  const relativeWorkspace = await runSetupCommand(
    setupOpts(f, { workspacePath: "relative/repo" }),
  );
  assert.equal(relativeWorkspace.exitCode, 2);
  assert.equal(relativeWorkspace.errorLabel, "workspace_not_absolute");
});

test("the monorepo skill copy lands at the workspace root", async (t) => {
  const f = await monorepoFixture(t);
  const result = await runSetupCommand(
    setupOpts(f, { layout: "monorepo", unitySubpath: "Client", skipSkill: false }),
  );
  const report = result.json as SetupReport;
  assert.equal(
    report.skill.path,
    join(f.workspace, ".cursor", "skills", "unity-open-mcp", "SKILL.md"),
  );
  assert.equal(await readFile(report.skill.path!, "utf8"), "# skill\n");
});
