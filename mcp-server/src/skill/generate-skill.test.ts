import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, rm, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
  readProjectState,
  generateSkillMarkdown,
  writeSkillToClients,
  generateSkill,
} from "./generate-skill.ts";
import {
  buildCapabilities,
  type CapabilitiesResult,
} from "../capabilities/build-capabilities.ts";
import {
  RULE_CATALOG,
  FIX_CATALOG,
} from "../capabilities/rule-catalog.ts";
import type { Tool } from "@modelcontextprotocol/sdk/types.js";

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const FIXTURE_TOOLS: Tool[] = [
  {
    name: "unity_open_mcp_ping",
    description: "Bridge health check.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "unity_open_mcp_execute_csharp",
    description: "Compile and run a C# snippet.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "unity_open_mcp_scan_paths",
    description: "Run verify rules scoped to paths.",
    inputSchema: { type: "object", properties: {} },
  },
  {
    name: "unity_open_mcp_capabilities",
    description: "Discover the full capability surface.",
    inputSchema: { type: "object", properties: {} },
  },
];

const FIXTURE_BATCH_NAMES: ReadonlySet<string> = new Set([
  "unity_open_mcp_scan_paths",
]);

function buildFixtureCaps(): CapabilitiesResult {
  return buildCapabilities(
    {
      tools: FIXTURE_TOOLS,
      batchToolNames: FIXTURE_BATCH_NAMES,
      rules: RULE_CATALOG,
      fixes: FIX_CATALOG,
    },
    { includePlanned: false },
  );
}

async function makeFakeProject(root: string): Promise<void> {
  await mkdir(join(root, "ProjectSettings"), { recursive: true });
  await mkdir(join(root, "Packages"), { recursive: true });
  await mkdir(join(root, "Assets", "Scripts"), { recursive: true });

  await writeFile(
    join(root, "ProjectSettings", "ProjectVersion.txt"),
    "m_EditorVersion: 6000.0.1f1\n",
  );

  await writeFile(
    join(root, "Packages", "manifest.json"),
    JSON.stringify({
      dependencies: {
        "com.alexeyperov.unity-open-mcp-bridge": "0.3.0",
        "com.alexeyperov.unity-open-mcp-verify": "0.3.0",
        "com.unity.ugui": "2.0.0",
      },
    }),
  );

  await writeFile(
    join(root, "Assets", "Scripts", "PlayerController.cs"),
    `namespace MyGame {
    public class PlayerController : MonoBehaviour
    {
        public float Speed = 5f;
    }
}`,
  );

  await writeFile(
    join(root, "Assets", "Scripts", "GameConfig.cs"),
    `namespace MyGame.Data {
    [CreateAssetMenu]
    public class GameConfig : ScriptableObject
    {
        public int MaxLevel = 10;
    }
}`,
  );
}

// ---------------------------------------------------------------------------
// readProjectState
// ---------------------------------------------------------------------------

test("readProjectState reads Unity version from ProjectVersion.txt", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uomcp-skill-"));
  try {
    await makeFakeProject(dir);
    const state = await readProjectState(dir);
    assert.equal(state.unityVersion, "6000.0.1f1");
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("readProjectState returns unknown when ProjectVersion.txt is missing", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uomcp-skill-"));
  try {
    const state = await readProjectState(dir);
    assert.equal(state.unityVersion, "unknown");
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("readProjectState detects bridge and verify package versions", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uomcp-skill-"));
  try {
    await makeFakeProject(dir);
    const state = await readProjectState(dir);
    assert.equal(state.bridgeVersion, "0.3.0");
    assert.equal(state.verifyVersion, "0.3.0");
    assert.ok(state.packages.length >= 3);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("readProjectState returns null versions when packages not installed", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uomcp-skill-"));
  try {
    await mkdir(join(dir, "Packages"), { recursive: true });
    await writeFile(
      join(dir, "Packages", "manifest.json"),
      JSON.stringify({ dependencies: { "com.unity.ugui": "2.0.0" } }),
    );
    const state = await readProjectState(dir);
    assert.equal(state.bridgeVersion, null);
    assert.equal(state.verifyVersion, null);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("readProjectState scans MonoBehaviour and ScriptableObject types", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uomcp-skill-"));
  try {
    await makeFakeProject(dir);
    const state = await readProjectState(dir);
    assert.ok(state.monoBehaviours.length >= 1);
    const player = state.monoBehaviours.find((m) => m.name === "PlayerController");
    assert.ok(player, "PlayerController should be detected");
    assert.equal(player!.namespace, "MyGame");
    assert.ok(player!.filePath.includes("PlayerController.cs"));

    assert.ok(state.scriptableObjects.length >= 1);
    const config = state.scriptableObjects.find((s) => s.name === "GameConfig");
    assert.ok(config, "GameConfig should be detected");
    assert.equal(config!.namespace, "MyGame.Data");
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("readProjectState handles missing Assets directory gracefully", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uomcp-skill-"));
  try {
    const state = await readProjectState(dir);
    assert.deepEqual(state.monoBehaviours, []);
    assert.deepEqual(state.scriptableObjects, []);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// generateSkillMarkdown (pure)
// ---------------------------------------------------------------------------

test("generateSkillMarkdown includes project name and Unity version", () => {
  const caps = buildFixtureCaps();
  const md = generateSkillMarkdown(
    {
      projectName: "MyGame",
      unityVersion: "6000.0.1f1",
      packages: [{ id: "com.unity.ugui", version: "2.0.0" }],
      bridgeVersion: "0.3.0",
      verifyVersion: "0.3.0",
      monoBehaviours: [],
      scriptableObjects: [],
    },
    caps,
  );
  assert.ok(md.includes("# Unity Agent Skill — MyGame"));
  assert.ok(md.includes("6000.0.1f1"));
  assert.ok(md.includes("0.3.0"));
});

test("generateSkillMarkdown lists implemented tools grouped by category", () => {
  const caps = buildFixtureCaps();
  const md = generateSkillMarkdown(
    {
      projectName: "Test",
      unityVersion: "6000.0.1f1",
      packages: [],
      bridgeVersion: null,
      verifyVersion: null,
      monoBehaviours: [],
      scriptableObjects: [],
    },
    caps,
  );
  assert.ok(md.includes("unity_open_mcp_ping"));
  assert.ok(md.includes("unity_open_mcp_execute_csharp"));
  assert.ok(md.includes("unity_open_mcp_capabilities"));
});

test("generateSkillMarkdown lists implemented verify rules with issue codes", () => {
  const caps = buildFixtureCaps();
  const md = generateSkillMarkdown(
    {
      projectName: "Test",
      unityVersion: "6000.0.1f1",
      packages: [],
      bridgeVersion: null,
      verifyVersion: null,
      monoBehaviours: [],
      scriptableObjects: [],
    },
    caps,
  );
  assert.ok(md.includes("Missing references"));
  assert.ok(md.includes("missing_script"));
  assert.ok(md.includes("remove_missing_script"));
});

test("generateSkillMarkdown includes key project types when present", () => {
  const caps = buildFixtureCaps();
  const md = generateSkillMarkdown(
    {
      projectName: "Test",
      unityVersion: "6000.0.1f1",
      packages: [],
      bridgeVersion: null,
      verifyVersion: null,
      monoBehaviours: [
        { name: "PlayerController", namespace: "MyGame", filePath: "Assets/Scripts/PlayerController.cs" },
      ],
      scriptableObjects: [
        { name: "GameConfig", namespace: "MyGame.Data", filePath: "Assets/Scripts/GameConfig.cs" },
      ],
    },
    caps,
  );
  assert.ok(md.includes("PlayerController"));
  assert.ok(md.includes("MyGame.PlayerController"));
  assert.ok(md.includes("GameConfig"));
  assert.ok(md.includes("MyGame.Data.GameConfig"));
});

test("generateSkillMarkdown omits key types section when empty", () => {
  const caps = buildFixtureCaps();
  const md = generateSkillMarkdown(
    {
      projectName: "Test",
      unityVersion: "6000.0.1f1",
      packages: [],
      bridgeVersion: null,
      verifyVersion: null,
      monoBehaviours: [],
      scriptableObjects: [],
    },
    caps,
  );
  assert.ok(!md.includes("Key project types"));
});

test("generateSkillMarkdown includes mutate→gate→fix workflow and gate modes", () => {
  const caps = buildFixtureCaps();
  const md = generateSkillMarkdown(
    {
      projectName: "Test",
      unityVersion: "6000.0.1f1",
      packages: [],
      bridgeVersion: null,
      verifyVersion: null,
      monoBehaviours: [],
      scriptableObjects: [],
    },
    caps,
  );
  assert.ok(md.includes("mutate → gate → fix"));
  assert.ok(md.includes("enforce"));
  assert.ok(md.includes("warn"));
  assert.ok(md.includes("paths_hint"));
});

test("generateSkillMarkdown never references internal specs or milestone IDs", () => {
  const caps = buildFixtureCaps();
  const md = generateSkillMarkdown(
    {
      projectName: "Test",
      unityVersion: "6000.0.1f1",
      packages: [],
      bridgeVersion: null,
      verifyVersion: null,
      monoBehaviours: [],
      scriptableObjects: [],
    },
    caps,
  );
  assert.ok(!md.includes("specs/"));
  assert.ok(!/\bM\d+\b/.test(md));
  assert.ok(!md.includes("T7."));
});

// ---------------------------------------------------------------------------
// writeSkillToClients
// ---------------------------------------------------------------------------

test("writeSkillToClients writes to .claude/skills by default", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uomcp-skill-"));
  try {
    const targets = await writeSkillToClients(dir, "# Test\n", ["claude"]);
    assert.equal(targets.length, 1);
    assert.equal(targets[0].client, "claude");
    assert.ok(targets[0].absolutePath.includes(".claude/skills/unity-open-mcp/SKILL.md"));
    assert.equal(targets[0].written, true);

    const written = await readFile(targets[0].absolutePath, "utf-8");
    assert.equal(written, "# Test\n");
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("writeSkillToClients writes to multiple client dirs", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uomcp-skill-"));
  try {
    const targets = await writeSkillToClients(dir, "# Test\n", ["claude", "cursor", "opencode"]);
    assert.equal(targets.length, 3);
    assert.ok(targets.some((t) => t.client === "cursor"));
    assert.ok(targets.some((t) => t.client === "opencode"));
    for (const t of targets) {
      const content = await readFile(t.absolutePath, "utf-8");
      assert.equal(content, "# Test\n");
    }
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("writeSkillToClients reports existed flag for pre-existing files", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uomcp-skill-"));
  try {
    // Pre-create the claude target.
    const targetPath = join(dir, ".claude", "skills", "unity-open-mcp", "SKILL.md");
    await mkdir(join(dir, ".claude", "skills", "unity-open-mcp"), { recursive: true });
    await writeFile(targetPath, "# old\n");

    const targets = await writeSkillToClients(dir, "# new\n", ["claude"]);
    assert.equal(targets[0].existed, true);
    assert.equal(targets[0].written, true);
    const content = await readFile(targetPath, "utf-8");
    assert.equal(content, "# new\n");
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// generateSkill (orchestrator)
// ---------------------------------------------------------------------------

test("generateSkill returns skill content without writing when write=false", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uomcp-skill-"));
  try {
    await makeFakeProject(dir);
    const caps = buildFixtureCaps();
    const result = await generateSkill(dir, caps, { write: false });
    assert.ok(result.skill.length > 0);
    assert.ok(result.skill.includes("MyGame"));
    assert.equal(result.written.length, 0);
    assert.equal(result.project.bridgeVersion, "0.3.0");
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("generateSkill writes to .claude/skills when write=true", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uomcp-skill-"));
  try {
    await makeFakeProject(dir);
    const caps = buildFixtureCaps();
    const result = await generateSkill(dir, caps, { write: true, clients: ["claude"] });
    assert.ok(result.skill.length > 0);
    assert.equal(result.written.length, 1);
    assert.equal(result.written[0].client, "claude");
    assert.ok(result.written[0].written);

    const written = await readFile(result.written[0].absolutePath, "utf-8");
    assert.equal(written, result.skill);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("generateSkill defaults to claude client when none specified", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uomcp-skill-"));
  try {
    await makeFakeProject(dir);
    const caps = buildFixtureCaps();
    const result = await generateSkill(dir, caps, { write: true });
    assert.equal(result.written.length, 1);
    assert.equal(result.written[0].client, "claude");
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("generateSkill is regenerable — produces identical project state across calls", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uomcp-skill-"));
  try {
    await makeFakeProject(dir);
    const caps = buildFixtureCaps();
    const r1 = await generateSkill(dir, caps, { write: false });
    const r2 = await generateSkill(dir, caps, { write: false });
    assert.equal(r1.project.unityVersion, r2.project.unityVersion);
    assert.equal(r1.project.bridgeVersion, r2.project.bridgeVersion);
    assert.equal(
      r1.project.monoBehaviours.map((m) => m.name).join(","),
      r2.project.monoBehaviours.map((m) => m.name).join(","),
    );
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});
