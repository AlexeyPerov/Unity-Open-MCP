// Skill client-path manifest — single source of truth.
//
// The checked-in manifest lives at the toolkit root:
//   `skills/client-paths.json`
// This module is the ONLY consumer on the mcp-server side. The Hub
// wizard (`hub/src-tauri/src/config/mcp_config.rs`) reads the same
// file directly from the toolkit root. To add or rename a client,
// edit `skills/client-paths.json` — do not hand-maintain duplicate
// constants here or in Rust.
//
// Resolution strategy:
//   1. `UNITY_OPEN_MCP_TOOLKIT_ROOT` env var (when the Hub or a
//      launcher pins the toolkit root explicitly).
//   2. Walk up from this module's location looking for
//      `skills/client-paths.json` (covers `mcp-server/src/` in dev
//      and `mcp-server/dist/` in a built checkout).
//   3. Bundled fallback constant (kept in sync with the manifest by
//      a unit test) so the skill generator still works in a
//      standalone `mcp-server/` install that lacks the toolkit tree.

import { readFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

export interface ClientPathEntry {
  relativePath: string;
}

export interface ClientPathsManifest {
  skillId: string;
  templateRelativePath: string;
  clients: Record<string, ClientPathEntry>;
  mcpClientMapping: Record<string, string[]>;
}

const MANIFEST_REL = "skills/client-paths.json";

/**
 * Bundled fallback mirroring `skills/client-paths.json`. A unit test
 * asserts this stays in sync with the on-disk manifest so the two
 * never drift silently.
 */
export const BUNDLED_MANIFEST: ClientPathsManifest = {
  skillId: "unity-open-mcp",
  templateRelativePath: "skills/unity-open-mcp/SKILL.md",
  clients: {
    cursor: { relativePath: ".cursor/skills/unity-open-mcp/SKILL.md" },
    claude: { relativePath: ".claude/skills/unity-open-mcp/SKILL.md" },
    opencode: { relativePath: ".opencode/skills/unity-open-mcp/SKILL.md" },
    agents: { relativePath: ".agents/skills/unity-open-mcp/SKILL.md" },
  },
  mcpClientMapping: {
    cursor: ["cursor"],
    "claude-desktop": ["claude"],
    "claude-code": ["claude"],
    "opencode-global": ["opencode"],
    "opencode-project": ["opencode"],
    "zcode-global": ["agents"],
    "zcode-project": ["agents"],
    manual: ["cursor", "claude", "opencode", "agents"],
  },
};

function hereDir(): string {
  // Works under both `node --experimental-strip-types` (src) and the
  // compiled `dist/` output.
  if (typeof __dirname !== "undefined") return __dirname;
  return dirname(fileURLToPath(import.meta.url));
}

function tryReadManifest(path: string): ClientPathsManifest | null {
  try {
    const raw = readFileSync(path, "utf-8");
    const parsed = JSON.parse(raw) as Partial<ClientPathsManifest>;
    if (
      typeof parsed.skillId === "string" &&
      typeof parsed.templateRelativePath === "string" &&
      parsed.clients &&
      typeof parsed.clients === "object" &&
      parsed.mcpClientMapping &&
      typeof parsed.mcpClientMapping === "object"
    ) {
      return parsed as ClientPathsManifest;
    }
    return null;
  } catch {
    return null;
  }
}

interface ResolvedManifest {
  manifest: ClientPathsManifest;
  /**
   * Absolute toolkit-root directory the manifest was loaded from, when
   * discovered from disk. `null` for the bundled fallback (no on-disk
   * toolkit tree to resolve template/asset paths against).
   */
  toolkitRoot: string | null;
}

function resolveManifestWithRoot(): ResolvedManifest {
  // 1. Explicit env override.
  const envRoot = process.env.UNITY_OPEN_MCP_TOOLKIT_ROOT?.trim();
  if (envRoot) {
    const fromEnv = tryReadManifest(join(envRoot, MANIFEST_REL));
    if (fromEnv) return { manifest: fromEnv, toolkitRoot: envRoot };
  }
  // 2. Walk up from this module's directory looking for the toolkit
  //    root (i.e. a parent dir containing `skills/client-paths.json`).
  let dir = hereDir();
  for (let i = 0; i < 8; i++) {
    const candidate = join(dir, MANIFEST_REL);
    const m = tryReadManifest(candidate);
    if (m) return { manifest: m, toolkitRoot: dir };
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  // 3. Bundled fallback (validated against the manifest by tests).
  return { manifest: BUNDLED_MANIFEST, toolkitRoot: null };
}

function resolveManifest(): ClientPathsManifest {
  return resolveManifestWithRoot().manifest;
}

let cached: ResolvedManifest | null = null;

/**
 * Load the client-paths manifest. Resolution is cached after the
 * first call (the manifest is immutable for the lifetime of the
 * process).
 */
export function loadClientPathsManifest(): ClientPathsManifest {
  return getResolvedManifest().manifest;
}

/**
 * Internal: the resolved manifest + the toolkit root it was loaded
 * from. Cached after the first call.
 */
function getResolvedManifest(): ResolvedManifest {
  if (cached) return cached;
  cached = resolveManifestWithRoot();
  return cached;
}

/**
 * Absolute path to the template skill file
 * (`<toolkitRoot>/<manifest.templateRelativePath>`), resolved from the
 * same root discovery that loads `client-paths.json`. Returns `null`
 * when the toolkit root cannot be found (bundled-fallback / standalone
 * `mcp-server/` install) so callers can degrade gracefully instead of
 * guessing a path. Never throws.
 *
 * Used by the skill generator to merge the template workflow prose
 * with the project-specific inventory (the template is the source of
 * truth for the workflow playbook).
 */
export function resolveTemplateSkillPath(): string | null {
  const { manifest, toolkitRoot } = getResolvedManifest();
  if (!toolkitRoot) return null;
  return join(toolkitRoot, manifest.templateRelativePath);
}

/**
 * @internal Test-only cache reset. The manifest resolution is cached
 * for process lifetime (it is immutable in production, where the env
 * override is set before the process starts). Tests that mutate
 * `UNITY_OPEN_MCP_TOOLKIT_ROOT` after a prior test already warmed the
 * cache need this to observe the override. Do not call from runtime
 * code.
 */
export function _clearClientPathsCacheForTests(): void {
  cached = null;
}

/**
 * Project-relative skill path for a client key
 * (`cursor` / `claude` / `opencode` / `agents`). Throws for unknown
 * keys so callers fail loudly instead of writing to a wrong path.
 */
export function clientSkillRelativePath(clientKey: string): string {
  const manifest = loadClientPathsManifest();
  const entry = manifest.clients[clientKey];
  if (!entry) {
    throw new Error(
      `Unknown skill client key "${clientKey}". Known keys: ${Object.keys(manifest.clients).join(", ")}.`,
    );
  }
  return entry.relativePath;
}

/**
 * All known client keys from the manifest — used to derive the
 * `unity_open_mcp_generate_skill` `clients` enum and the allowlist in
 * the tool router.
 */
export function knownClientKeys(): string[] {
  return Object.keys(loadClientPathsManifest().clients);
}
