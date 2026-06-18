// Auto-generated agent skill builder.
//
// Reads project state from disk (Unity version, installed packages, key
// MonoBehaviour / ScriptableObject types), combines it with the capability
// surface from build-capabilities, and emits a project-specific SKILL.md
// that gives the LLM up-to-date context for *this* project — installed
// tool versions, available verify rules, and the types the agent will
// encounter.
//
// The module has three layers:
//  1. readProjectState() — disk I/O (ProjectVersion.txt, manifest.json, .cs scan)
//  2. generateSkillMarkdown() — pure string builder (no I/O, easily tested)
//  3. writeSkillToClients() — writes the generated file to .claude/skills/ etc.

import { readFile, writeFile, readdir, stat } from "node:fs/promises";
import { join, basename, relative, sep } from "node:path";
import type { CapabilitiesResult } from "../capabilities/build-capabilities.js";
import { clientSkillRelativePath } from "./client-paths.js";

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export interface PackageEntry {
  id: string;
  version: string;
}

export interface TypeEntry {
  name: string;
  namespace: string;
  filePath: string;
}

export interface ProjectState {
  projectName: string;
  unityVersion: string;
  packages: PackageEntry[];
  bridgeVersion: string | null;
  verifyVersion: string | null;
  monoBehaviours: TypeEntry[];
  scriptableObjects: TypeEntry[];
}

export interface SkillWriteTarget {
  client: string;
  relativePath: string;
  absolutePath: string;
  written: boolean;
  existed: boolean;
}

export interface GenerateSkillResult {
  skill: string;
  project: ProjectState;
  written: SkillWriteTarget[];
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const BRIDGE_PACKAGE_ID = "com.alexeyperov.unity-open-mcp-bridge";
const VERIFY_PACKAGE_ID = "com.alexeyperov.unity-open-mcp-verify";

// Project-relative skill paths come from the single-source manifest
// at `skills/client-paths.json` (see client-paths.ts). Do not add a
// per-client constant here — edit the manifest instead.

const SKIP_DIRS = new Set([
  ".git", ".vs", "Library", "Logs", "obj", "Obj",
  "Temp", "Build", "Builds", "UserSettings", "node_modules",
  ".claude", ".cursor", ".opencode", ".agents",
]);

const MAX_CS_SCAN = 500;
const MAX_TYPES_IN_SKILL = 40;

// ---------------------------------------------------------------------------
// 1. Project state reader (disk I/O)
// ---------------------------------------------------------------------------

export async function readProjectState(projectRoot: string): Promise<ProjectState> {
  const projectName = basename(projectRoot);
  const unityVersion = await readUnityVersion(projectRoot);
  const { packages, bridgeVersion, verifyVersion } = await readManifest(projectRoot);
  const { monoBehaviours, scriptableObjects } = await scanProjectTypes(projectRoot);

  return {
    projectName,
    unityVersion,
    packages,
    bridgeVersion,
    verifyVersion,
    monoBehaviours,
    scriptableObjects,
  };
}

async function readUnityVersion(projectRoot: string): Promise<string> {
  try {
    const content = await readFile(
      join(projectRoot, "ProjectSettings", "ProjectVersion.txt"),
      "utf-8",
    );
    const match = content.match(/m_EditorVersion:\s*(.+)/);
    return match ? match[1].trim() : "unknown";
  } catch {
    return "unknown";
  }
}

async function readManifest(
  projectRoot: string,
): Promise<{
  packages: PackageEntry[];
  bridgeVersion: string | null;
  verifyVersion: string | null;
}> {
  let deps: Record<string, string>;
  try {
    const raw = await readFile(
      join(projectRoot, "Packages", "manifest.json"),
      "utf-8",
    );
    const parsed = JSON.parse(raw) as { dependencies?: Record<string, string> };
    deps = parsed.dependencies ?? {};
  } catch {
    return { packages: [], bridgeVersion: null, verifyVersion: null };
  }

  const packages: PackageEntry[] = Object.entries(deps)
    .map(([id, version]) => ({ id, version }))
    .sort((a, b) => a.id.localeCompare(b.id));

  const bridgeEntry = deps[BRIDGE_PACKAGE_ID];
  const verifyEntry = deps[VERIFY_PACKAGE_ID];

  return {
    packages,
    bridgeVersion: bridgeEntry ?? null,
    verifyVersion: verifyEntry ?? null,
  };
}

async function scanProjectTypes(
  projectRoot: string,
): Promise<{ monoBehaviours: TypeEntry[]; scriptableObjects: TypeEntry[] }> {
  const assetsDir = join(projectRoot, "Assets");
  const csFiles: string[] = [];
  await collectCsFiles(assetsDir, csFiles, 0);

  const monoBehaviours: TypeEntry[] = [];
  const scriptableObjects: TypeEntry[] = [];

  for (const filePath of csFiles) {
    if (monoBehaviours.length + scriptableObjects.length >= MAX_CS_SCAN) break;
    try {
      const content = await readFile(filePath, "utf-8");
      extractTypes(content, filePath, projectRoot, monoBehaviours, scriptableObjects);
    } catch {
      // Skip unreadable files.
    }
  }

  monoBehaviours.sort((a, b) => a.name.localeCompare(b.name));
  scriptableObjects.sort((a, b) => a.name.localeCompare(b.name));

  return {
    monoBehaviours: monoBehaviours.slice(0, MAX_TYPES_IN_SKILL),
    scriptableObjects: scriptableObjects.slice(0, MAX_TYPES_IN_SKILL),
  };
}

async function collectCsFiles(
  dir: string,
  results: string[],
  depth: number,
): Promise<void> {
  if (depth > 8 || results.length > 2000) return;
  let entries: import("node:fs").Dirent[];
  try {
    entries = await readdir(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    if (entry.name.startsWith(".")) continue;
    const fullPath = join(dir, entry.name);
    if (entry.isDirectory()) {
      if (SKIP_DIRS.has(entry.name)) continue;
      await collectCsFiles(fullPath, results, depth + 1);
    } else if (entry.name.endsWith(".cs")) {
      results.push(fullPath);
    }
  }
}

const CLASS_RE =
  /(?:public|internal|abstract|sealed|\s)+class\s+(\w+)(?:\s*:\s*(?:MonoBehaviour|ScriptableObject))/g;

const NAMESPACE_RE = /namespace\s+([\w.]+)/;

function extractTypes(
  content: string,
  filePath: string,
  projectRoot: string,
  monoBehaviours: TypeEntry[],
  scriptableObjects: TypeEntry[],
): void {
  const nsMatch = content.match(NAMESPACE_RE);
  const namespace = nsMatch ? nsMatch[1] : "";

  for (const match of content.matchAll(CLASS_RE)) {
    const className = match[1];
    const afterMatch = content.slice(match.index! + match[0].length, match.index! + match[0].length + 40);
    const isScriptableObject = /ScriptableObject/.test(match[0]) || /ScriptableObject/.test(afterMatch);
    const isMonoBehaviour = /MonoBehaviour/.test(match[0]) || /MonoBehaviour/.test(afterMatch);

    const entry: TypeEntry = {
      name: className,
      namespace,
      filePath: relative(projectRoot, filePath).split(sep).join("/"),
    };

    if (isMonoBehaviour) monoBehaviours.push(entry);
    else if (isScriptableObject) scriptableObjects.push(entry);
  }
}

// ---------------------------------------------------------------------------
// 2. Skill markdown generator (pure, no I/O)
// ---------------------------------------------------------------------------

export function generateSkillMarkdown(
  state: ProjectState,
  caps: CapabilitiesResult,
): string {
  const lines: string[] = [];
  const now = new Date().toISOString().slice(0, 10);

  lines.push(`# Unity Agent Skill — ${state.projectName}`);
  lines.push("");
  lines.push(`> Auto-generated by \`unity_open_mcp_generate_skill\` on ${now}.`);
  lines.push(`> Regenerate after package or script changes to keep this file current.`);
  lines.push("");

  // --- Project environment ---
  lines.push("## Project environment");
  lines.push("");
  lines.push(`- **Unity version:** ${state.unityVersion}`);
  if (state.bridgeVersion) {
    lines.push(`- **Bridge package:** ${BRIDGE_PACKAGE_ID} @ ${state.bridgeVersion}`);
  } else {
    lines.push(`- **Bridge package:** not installed`);
  }
  if (state.verifyVersion) {
    lines.push(`- **Verify package:** ${VERIFY_PACKAGE_ID} @ ${state.verifyVersion}`);
  } else {
    lines.push(`- **Verify package:** not installed`);
  }
  lines.push("");

  // --- Installed packages ---
  if (state.packages.length > 0) {
    lines.push("### Installed packages");
    lines.push("");
    lines.push("| Package | Version |");
    lines.push("|---|---|");
    for (const pkg of state.packages) {
      lines.push(`| ${pkg.id} | ${pkg.version} |`);
    }
    lines.push("");
  }

  // --- Available tools ---
  const implementedTools = caps.tools.filter((t) => t.implemented);
  if (implementedTools.length > 0) {
    lines.push("## Available tools");
    lines.push("");
    lines.push("All tools are prefixed `unity_open_mcp_*` or `unity_senses_*`.");
    lines.push("");

    const byCategory = groupByCategory(implementedTools);
    for (const [category, tools] of byCategory) {
      lines.push(`### ${capitalize(category)}`);
      lines.push("");
      for (const tool of tools) {
        lines.push(`- \`${tool.name}\` — ${firstSentence(tool.description)}`);
      }
      lines.push("");
    }
  }

  // --- Verify rules ---
  const implementedRules = caps.rules.filter((r) => r.implemented);
  if (implementedRules.length > 0) {
    lines.push("## Verify rules (gate)");
    lines.push("");
    for (const rule of implementedRules) {
      lines.push(`### ${rule.title}`);
      lines.push("");
      lines.push(rule.description);
      lines.push("");
      if (rule.issues.length > 0) {
        lines.push("| Issue code | Severity | Auto-fix |");
        lines.push("|---|---|---|");
        for (const issue of rule.issues) {
          const fixLabel = issue.fixIds.length > 0 ? issue.fixIds.join(", ") : "—";
          lines.push(`| ${issue.code} | ${issue.severity} | ${fixLabel} |`);
        }
        lines.push("");
      }
    }
  }

  // --- Available fixes ---
  const implementedFixes = caps.fixes.filter((f) => f.implemented);
  if (implementedFixes.length > 0) {
    lines.push("## Available fixes");
    lines.push("");
    for (const fix of implementedFixes) {
      lines.push(`- \`${fix.id}\` — safe: ${fix.safe}, resolves: ${fix.issueCodes.join(", ")}`);
    }
    lines.push("");
  }

  // --- Key project types ---
  if (state.monoBehaviours.length > 0 || state.scriptableObjects.length > 0) {
    lines.push("## Key project types");
    lines.push("");
    lines.push("Use `unity_open_mcp_find_members` to inspect any of these before editing.");
    lines.push("");

    if (state.monoBehaviours.length > 0) {
      lines.push("### MonoBehaviours");
      lines.push("");
      for (const mb of state.monoBehaviours) {
        const fullName = mb.namespace ? `${mb.namespace}.${mb.name}` : mb.name;
        lines.push(`- **${fullName}** — \`${mb.filePath}\``);
      }
      lines.push("");
    }

    if (state.scriptableObjects.length > 0) {
      lines.push("### ScriptableObjects");
      lines.push("");
      for (const so of state.scriptableObjects) {
        const fullName = so.namespace ? `${so.namespace}.${so.name}` : so.name;
        lines.push(`- **${fullName}** — \`${so.filePath}\``);
      }
      lines.push("");
    }
  }

  // --- Core workflow ---
  lines.push("## Core workflow: mutate → gate → fix");
  lines.push("");
  lines.push("1. **Discover** — call `unity_open_mcp_capabilities` to confirm which tools and rules are available.");
  lines.push("2. **Declare scope** — pass `paths_hint` with asset paths you intend to touch.");
  lines.push("3. **Mutate** — use `unity_open_mcp_execute_csharp`, `invoke_method`, or `execute_menu` with default `gate: enforce`.");
  lines.push("4. **Read gate** — on `isError: true`, inspect `gate.delta.newIssues` and `agentNextSteps`.");
  lines.push("5. **Fix** — address top error; use `unity_open_mcp_apply_fix` with `dry_run: true` first when a fix is available.");
  lines.push("6. **Retry** — re-run mutation; confirm `gate.delta.resolvedErrors > 0` or `newErrors == 0`.");
  lines.push("");
  lines.push("**Principle: mutation success ≠ project safe.** A successful C# compile can still break prefab references.");
  lines.push("");

  // --- Gate modes ---
  lines.push("### Gate modes");
  lines.push("");
  lines.push("| Mode | When to use |");
  lines.push("|---|---|");
  lines.push("| `enforce` (default) | Normal edits — fail fast on new errors |");
  lines.push("| `warn` | Exploratory changes — read `gate.delta` but continue |");
  lines.push("| `off` | Trusted scripts only — no checkpoint/validate |");
  lines.push("");

  // --- Quick start ---
  lines.push("## Quick start");
  lines.push("");
  lines.push("```json");
  lines.push("// 1. Check bridge health");
  lines.push('// Tool call: unity_open_mcp_ping');
  lines.push("{}");
  lines.push("");
  lines.push("// 2. Discover capabilities");
  lines.push("// Tool call: unity_open_mcp_capabilities");
  lines.push("{}");
  lines.push("");
  lines.push("// 3. Inspect a type before editing");
  lines.push("// Tool call: unity_open_mcp_find_members");
  lines.push('{ "type": "PlayerController" }');
  lines.push("");
  lines.push("// 4. Execute a mutation with gate enforcement");
  lines.push("// Tool call: unity_open_mcp_execute_csharp");
  lines.push(JSON.stringify({
    code: 'var go = new GameObject("NewObject");\nreturn go.name;',
    paths_hint: ["Assets/Scenes/"],
    gate: "enforce",
  }, null, 2));
  lines.push("```");
  lines.push("");

  lines.push("---");
  lines.push("");
  lines.push("This file was auto-generated. To refresh, call `unity_open_mcp_generate_skill` with `\"write\": true`.");

  return lines.join("\n") + "\n";
}

function groupByCategory(
  tools: CapabilitiesResult["tools"],
): Map<string, CapabilitiesResult["tools"]> {
  const map = new Map<string, CapabilitiesResult["tools"]>();
  for (const tool of tools) {
    const cat = tool.category || "other";
    if (!map.has(cat)) map.set(cat, []);
    map.get(cat)!.push(tool);
  }
  return map;
}

function capitalize(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

function firstSentence(text: string): string {
  const period = text.indexOf(". ");
  if (period > 0 && period < 120) return text.slice(0, period + 1);
  return text;
}

// ---------------------------------------------------------------------------
// 3. File writer (disk I/O)
// ---------------------------------------------------------------------------

export async function writeSkillToClients(
  projectRoot: string,
  content: string,
  clients: string[],
): Promise<SkillWriteTarget[]> {
  const results: SkillWriteTarget[] = [];
  for (const client of clients) {
    let rel: string;
    try {
      rel = clientSkillRelativePath(client);
    } catch {
      // Unknown client key — skip rather than abort the whole write.
      continue;
    }
    const abs = join(projectRoot, rel.split("/").join(sep));
    let existed = false;
    try {
      await stat(abs);
      existed = true;
    } catch {
      // File does not exist yet.
    }
    await ensureParentDir(abs);
    await writeFile(abs, content, "utf-8");
    results.push({
      client,
      relativePath: rel,
      absolutePath: abs,
      written: true,
      existed,
    });
  }
  return results;
}

async function ensureParentDir(filePath: string): Promise<void> {
  const { mkdir } = await import("node:fs/promises");
  const lastSep = filePath.lastIndexOf(sep);
  if (lastSep > 0) {
    await mkdir(filePath.slice(0, lastSep), { recursive: true });
  }
}

// ---------------------------------------------------------------------------
// Orchestrator
// ---------------------------------------------------------------------------

export async function generateSkill(
  projectRoot: string,
  caps: CapabilitiesResult,
  options: { write?: boolean; clients?: string[] } = {},
): Promise<GenerateSkillResult> {
  const state = await readProjectState(projectRoot);
  const skill = generateSkillMarkdown(state, caps);

  let written: SkillWriteTarget[] = [];
  if (options.write) {
    const clients = options.clients && options.clients.length > 0
      ? options.clients
      : ["claude"];
    written = await writeSkillToClients(projectRoot, skill, clients);
  }

  return { skill, project: state, written };
}
