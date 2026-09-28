import { test, type TestContext } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { packagePins, runSetupCommand, type SetupReport } from "./setup-command.js";
import { renderCodexServer } from "../setup/codex-toml.js";

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
  const notUnity = await mkdtemp(join(tmpdir(), "unity-open-mcp-not-unity-"));
  t.after(() => rm(notUnity, { recursive: true, force: true }));
  const cwdNotUnity = await runSetupCommand({
    version: "1.2.3",
    projectPath: undefined,
    client: "cursor",
    skipSkill: true,
    dryRun: false,
    cwd: notUnity,
  });
  assert.equal(cwdNotUnity.exitCode, 2);
  assert.equal(cwdNotUnity.errorLabel, "not_unity_project");
  assert.match(cwdNotUnity.human, /Run setup in the folder that contains Assets\/, Packages\/, and ProjectSettings\/, or pass --project/);

  const missingClient = await runSetupCommand({
    version: "1.2.3",
    projectPath: f.root,
    client: undefined,
    skipSkill: true,
    dryRun: false,
  });
  assert.equal(missingClient.exitCode, 2);
  assert.equal(missingClient.errorLabel, "missing_client");

  const relativeMissing = await runSetupCommand({
    version: "1.2.3",
    projectPath: "relative/project",
    client: "cursor",
    skipSkill: true,
    dryRun: false,
    cwd: notUnity,
  });
  assert.equal(relativeMissing.exitCode, 2);
  assert.equal(relativeMissing.errorLabel, "not_unity_project");
  assert.ok(relativeMissing.human.includes(`${join(notUnity, "relative", "project")} is not a Unity project root`));

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
    setupOpts(f, { workspacePath: basename(f.workspace), cwd: dirname(f.workspace), dryRun: true }),
  );
  assert.equal(relativeWorkspace.exitCode, 0);
  assert.equal((relativeWorkspace.json as SetupReport).workspace, f.workspace);
  assert.equal((relativeWorkspace.json as SetupReport).unitySubpath, "Client");
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

// --- project resolution ----------------------------------------------------

test("without --project the working directory is the Unity project", async (t) => {
  const f = await monorepoFixture(t);
  const result = await runSetupCommand(
    setupOpts(f, { projectPath: undefined, cwd: f.project, dryRun: true, env: {} }),
  );
  assert.equal(result.exitCode, 0);
  const report = result.json as SetupReport;
  assert.equal(report.project, f.project);
  assert.equal(report.projectSource, "cwd");
  assert.match(result.human, /Project: .* \(from cwd\)/);
});

test("a relative --project resolves against the working directory", async (t) => {
  const f = await monorepoFixture(t);
  const result = await runSetupCommand(
    setupOpts(f, { projectPath: "Client", cwd: f.workspace, dryRun: true, env: {} }),
  );
  assert.equal(result.exitCode, 0);
  const report = result.json as SetupReport;
  assert.equal(report.project, f.project);
  assert.equal(report.projectSource, "flag");
  assert.equal(report.workspace, f.project, "no subpath: the Unity root is the workspace");
});

test("--unity-subpath without --project is a monorepo run from the repository root", async (t) => {
  const f = await monorepoFixture(t);
  const result = await runSetupCommand(
    setupOpts(f, { projectPath: undefined, unitySubpath: "Client", cwd: f.workspace, dryRun: true, env: {} }),
  );
  assert.equal(result.exitCode, 0);
  const report = result.json as SetupReport;
  assert.equal(report.project, f.project);
  assert.equal(report.projectSource, "cwd+subpath");
  assert.equal(report.workspace, f.workspace);
  assert.equal(report.layout, "monorepo");
  assert.equal(report.portable, true);
});

test("a working directory above a Unity project suggests --unity-subpath", async (t) => {
  const f = await monorepoFixture(t);
  const result = await runSetupCommand(
    setupOpts(f, { projectPath: undefined, cwd: f.workspace, dryRun: true, env: {} }),
  );
  assert.equal(result.exitCode, 2);
  assert.equal(result.errorLabel, "not_unity_project");
  assert.match(result.human, /Found a Unity project at Client\/ — run again with --unity-subpath Client\./);
});

test("a UNITY_PROJECT_PATH naming another project is ignored with a warning", async (t) => {
  const f = await monorepoFixture(t);
  const other = await runSetupCommand(
    setupOpts(f, { dryRun: true, env: { UNITY_PROJECT_PATH: "/somewhere/else" } }),
  );
  const report = other.json as SetupReport;
  assert.equal(report.project, f.project);
  assert.ok(
    report.warnings.some((w) => w.includes("UNITY_PROJECT_PATH is set to /somewhere/else")),
    report.warnings.join("\n"),
  );

  const same = await runSetupCommand(
    setupOpts(f, { dryRun: true, env: { UNITY_PROJECT_PATH: f.project } }),
  );
  assert.deepEqual((same.json as SetupReport).warnings, []);
});

// --- repository detection ----------------------------------------------------

test("a Unity project that is a repository root gets the portable form by default", async (t) => {
  const f = await monorepoFixture(t);
  await mkdir(join(f.project, ".git"));
  const result = await runSetupCommand(setupOpts(f, { dryRun: true, env: {} }));
  const report = result.json as SetupReport;
  assert.equal(report.portable, true);
  assert.equal(
    (report.mcpConfig.entry as { env: Record<string, string> }).env.UNITY_PROJECT_PATH,
    "${workspaceFolder}",
  );

  const opted = await runSetupCommand(setupOpts(f, { dryRun: true, env: {}, portable: false }));
  assert.equal((opted.json as SetupReport).portable, false);
});

test("running inside the Unity folder of a monorepo warns with the right command", async (t) => {
  const f = await monorepoFixture(t);
  await mkdir(join(f.workspace, ".git"));
  const result = await runSetupCommand(
    setupOpts(f, { projectPath: undefined, cwd: f.project, dryRun: true, env: {} }),
  );
  assert.equal(result.exitCode, 0);
  const report = result.json as SetupReport;
  assert.equal(report.workspace, f.project);
  assert.equal(report.portable, false);
  const warning = report.warnings.find((w) => w.includes("is inside the repository"));
  assert.ok(warning, report.warnings.join("\n"));
  assert.match(warning!, /npx -y unity-open-mcp@1\.2\.3 setup --client cursor --unity-subpath Client$/);

  // An explicit --workspace is a deliberate choice: no second-guessing.
  const explicit = await runSetupCommand(
    setupOpts(f, { workspacePath: f.project, dryRun: true, env: {} }),
  );
  assert.ok(!(explicit.json as SetupReport).warnings.some((w) => w.includes("is inside the repository")));
});

test("a repository in the home directory does not count", async (t) => {
  const f = await monorepoFixture(t);
  await mkdir(join(f.workspace, ".git"));
  const result = await runSetupCommand(
    setupOpts(f, { dryRun: true, env: {}, homeDir: f.workspace }),
  );
  const report = result.json as SetupReport;
  assert.equal(report.portable, false);
  assert.ok(!report.warnings.some((w) => w.includes("is inside the repository")));
});

// --- VS Code -----------------------------------------------------------------

test("vscode writes .vscode/mcp.json under servers and keeps siblings", async (t) => {
  const f = await monorepoFixture(t);
  const configPath = join(f.project, ".vscode", "mcp.json");
  await mkdir(dirname(configPath), { recursive: true });
  await writeFile(configPath, JSON.stringify({
    inputs: [{ id: "token" }],
    servers: { github: { type: "http", url: "https://example" } },
  }));
  const result = await runSetupCommand(setupOpts(f, { client: "vscode", skipSkill: false, env: {} }));
  assert.equal(result.exitCode, 0);
  const config = JSON.parse(await readFile(configPath, "utf8"));
  assert.deepEqual(config.inputs, [{ id: "token" }]);
  assert.deepEqual(config.servers.github, { type: "http", url: "https://example" });
  assert.deepEqual(config.servers["unity-open-mcp"], {
    type: "stdio",
    command: "npx",
    args: ["-y", "unity-open-mcp@1.2.3"],
    env: { UNITY_PROJECT_PATH: f.project },
  });
  assert.equal(
    (result.json as SetupReport).skill.path,
    join(f.project, ".vscode", "skills", "unity-open-mcp", "SKILL.md"),
  );
});

test("vscode portable monorepo entry uses ${workspaceFolder}/Client", async (t) => {
  const f = await monorepoFixture(t);
  const result = await runSetupCommand(
    setupOpts(f, { client: "vscode", layout: "monorepo", unitySubpath: "Client", dryRun: true, env: {} }),
  );
  const report = result.json as SetupReport;
  assert.equal(report.mcpConfig.path, join(f.workspace, ".vscode", "mcp.json"));
  assert.equal(report.configStrategy, "interpolation");
  assert.deepEqual(report.mcpConfig.entry, {
    type: "stdio",
    command: "npx",
    args: ["-y", "unity-open-mcp@1.2.3"],
    env: { UNITY_PROJECT_PATH: "${workspaceFolder}/Client" },
  });
});

test("a JSONC config is left untouched and the entry is printed instead", async (t) => {
  const f = await monorepoFixture(t);
  const configPath = join(f.project, ".vscode", "mcp.json");
  await mkdir(dirname(configPath), { recursive: true });
  const jsonc = '{\n  // my servers\n  "servers": {\n    "github": { "type": "http", "url": "https://example" },\n  },\n}\n';
  await writeFile(configPath, jsonc);
  const result = await runSetupCommand(setupOpts(f, { client: "vscode", env: {} }));
  assert.equal(result.exitCode, 1);
  assert.equal(result.errorLabel, "jsonc_config");
  assert.match(result.human, /comments or trailing commas/);
  assert.match(result.human, /"unity-open-mcp"/);
  assert.equal(await readFile(configPath, "utf8"), jsonc);
});

// --- Codex -------------------------------------------------------------------

test("codex appends its table and keeps every other byte", async (t) => {
  const f = await monorepoFixture(t);
  const configPath = join(f.project, ".codex", "config.toml");
  await mkdir(dirname(configPath), { recursive: true });
  const existing = '# mine\n[mcp_servers.fal_ai]\nurl = "https://mcp.fal.ai/mcp"\nhttp_headers = { Authorization = "Bearer x" }\n';
  await writeFile(configPath, existing);
  const result = await runSetupCommand(setupOpts(f, { client: "codex", skipSkill: false, env: {} }));
  assert.equal(result.exitCode, 0);
  const body = await readFile(configPath, "utf8");
  assert.ok(body.startsWith(existing));
  assert.match(body, /\[mcp_servers\.unity-open-mcp\]\nenabled = true\ncommand = "npx"\nargs = \["-y", "unity-open-mcp@1\.2\.3"\]\n/);
  assert.ok(body.includes(`UNITY_PROJECT_PATH = "${f.project}"`));
  const report = result.json as SetupReport;
  assert.equal(report.skill.path, join(f.project, ".agents", "skills", "unity-open-mcp", "SKILL.md"));
  assert.ok(report.userAction.some((a) => a.includes("Trust")));

  // Idempotent.
  await runSetupCommand(setupOpts(f, { client: "codex", env: {} }));
  assert.equal(await readFile(configPath, "utf8"), body);
});

test("codex portable monorepo entry runs the wrapper", async (t) => {
  const f = await monorepoFixture(t);
  const result = await runSetupCommand(
    setupOpts(f, { client: "codex", layout: "monorepo", unitySubpath: "Client", env: {}, platform: "darwin" }),
  );
  assert.equal(result.exitCode, 0);
  const report = result.json as SetupReport;
  assert.equal(report.configStrategy, "wrapper");
  const body = await readFile(join(f.workspace, ".codex", "config.toml"), "utf8");
  assert.equal(
    body,
    renderCodexServer("unity-open-mcp", {
      command: "bash",
      args: ["scripts/mcp/unity-open-mcp.sh"],
      env: {},
      removeEnv: [],
    }),
  );
  assert.equal(report.wrapper.written, true);
  assert.ok(!body.includes(f.workspace));
});

test("codex on Windows falls back to the absolute path unless --portable is explicit", async (t) => {
  const f = await monorepoFixture(t);
  const byDefault = await runSetupCommand(
    setupOpts(f, { client: "codex", unitySubpath: "Client", dryRun: true, env: {}, platform: "win32" }),
  );
  const report = byDefault.json as SetupReport;
  assert.equal(report.portable, false);
  assert.ok(report.warnings.some((w) => w.includes("Git Bash or WSL")));

  const explicit = await runSetupCommand(
    setupOpts(f, { client: "codex", unitySubpath: "Client", dryRun: true, env: {}, platform: "win32", portable: true }),
  );
  assert.equal((explicit.json as SetupReport).configStrategy, "wrapper");
  assert.ok((explicit.json as SetupReport).warnings.some((w) => w.includes("Git Bash or WSL")));
});

test("an unsupported Codex shape fails without touching the file", async (t) => {
  const f = await monorepoFixture(t);
  const configPath = join(f.project, ".codex", "config.toml");
  await mkdir(dirname(configPath), { recursive: true });
  const dotted = 'mcp_servers.unity-open-mcp.command = "npx"\n';
  await writeFile(configPath, dotted);
  const result = await runSetupCommand(setupOpts(f, { client: "codex", env: {} }));
  assert.equal(result.exitCode, 1);
  assert.equal(result.errorLabel, "unsupported_codex_config");
  assert.match(result.human, /Add this entry by hand:\n\[mcp_servers\.unity-open-mcp\]/);
  assert.equal(await readFile(configPath, "utf8"), dotted);
});

test("dry-run for the new clients writes nothing", async (t) => {
  const f = await monorepoFixture(t);
  for (const client of ["vscode", "codex"]) {
    const result = await runSetupCommand(setupOpts(f, { client, dryRun: true, skipSkill: false, env: {} }));
    assert.equal(result.exitCode, 0, client);
    assert.equal((result.json as SetupReport).mcpConfig.written, false);
  }
  await assert.rejects(readFile(join(f.project, ".vscode", "mcp.json")));
  await assert.rejects(readFile(join(f.project, ".codex", "config.toml")));
  await assert.rejects(readFile(join(f.project, ".agents", "skills", "unity-open-mcp", "SKILL.md")));
});
