import { test, type TestContext } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { packagePins, runSetupCommand } from "./setup-command.js";

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
