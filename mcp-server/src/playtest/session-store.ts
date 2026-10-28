// Playtest session recorder — on-disk layout.
//
// A session is a deliberate on-disk record (not a cache): the review artifact
// a human reads after an agent's playtest. It lives in the same platform
// app-data root as the player-state snapshots, outside the project:
//
//   <app data>/unity-open-mcp/playtest-sessions/<company>/<product>/<session>/
//     session.json      metadata (owner, device, snapshot, state, report path)
//     log.jsonl         one JSON line per event, appended as it happens
//     report/
//       frames/         full-size captures (linked or copied when the call ends)
//       thumbs/         downscaled previews (written by `end`)
//       report.md       written by `end`
//
// `<app data>` mirrors the bridge's player-state root: ~/Library/Application
// Support on macOS, %LOCALAPPDATA% on Windows, $XDG_DATA_HOME or ~/.local/share
// elsewhere. Company / product are the project's Player Settings, made into one
// path segment each with the same rule as the bridge.
//
// Every write is synchronous and small: a log line is appended with one
// appendFileSync (open-write-close) the moment the event happens, so the log
// survives a domain reload, an Editor crash and an MCP server restart; the
// metadata file is replaced through a temp file + rename.
import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

export const SESSION_FORMAT = 1;
export const SESSION_FILE = "session.json";
export const LOG_FILE = "log.jsonl";
export const REPORT_DIR = "report";
export const FRAMES_DIR = "frames";
export const THUMBS_DIR = "thumbs";
export const REPORT_FILE = "report.md";

export interface SessionOwner { project: string; agent: string; port?: number }

export interface SessionMeta {
  format: number;
  id: string;
  name: string;
  project: string;
  company: string;
  product: string;
  owner: SessionOwner;
  /** MCP server process that owns the session; a dead pid makes it adoptable. */
  server_pid: number;
  started_at: string;
  state: "open" | "ended";
  ended_at?: string;
  device: Record<string, unknown> | null;
  snapshot: { name: string; taken: boolean } | null;
  report?: string;
  adoptions?: Array<{ at: string; from: SessionOwner; from_pid: number }>;
  limits: Record<string, number>;
}

/** The platform app-data folder (same rule as the bridge's player-state store). */
export function appDataRoot(platform: NodeJS.Platform = process.platform, env: NodeJS.ProcessEnv = process.env, home: string = env.HOME ?? homedir()): string {
  let appData: string;
  if (platform === "darwin") appData = join(home, "Library", "Application Support");
  else if (platform === "win32") appData = env.LOCALAPPDATA && env.LOCALAPPDATA.length ? env.LOCALAPPDATA : join(home, "AppData", "Local");
  else {
    const xdg = env.XDG_DATA_HOME;
    appData = xdg && xdg.startsWith("/") ? xdg : join(home, ".local", "share");
  }
  return join(appData, "unity-open-mcp");
}

export function sessionsRoot(platform?: NodeJS.Platform, env?: NodeJS.ProcessEnv, home?: string): string {
  return join(appDataRoot(platform, env, home), "playtest-sessions");
}

/** A company / product name as one path segment (the bridge's SafeSegment rule). */
export function safeSegment(value: string | null | undefined): string {
  if (!value) return "_";
  let out = "";
  for (const c of value) out += /[\\/:*?"<>|]/.test(c) || c.charCodeAt(0) < 32 ? "_" : c;
  out = out.trim().replace(/\.+$/, "");
  return out.length === 0 || out === "." || out === ".." ? "_" : out;
}

/** A session id: UTC stamp + a slug of the name + 4 hex, safe as a folder name. */
export function sessionId(name: string, now: Date, random: string): string {
  const stamp = now.toISOString().replace(/[-:]/g, "").replace(/\..*$/, "").replace("T", "-");
  const slug = name.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 32) || "playtest";
  return `${stamp}-${slug}-${random}`;
}

export const SESSION_ID_PATTERN = /^[0-9]{8}-[0-9]{6}-[a-z0-9-]{1,40}$/;

/**
 * Company / product from ProjectSettings/ProjectSettings.asset — the fallback
 * when the Editor cannot be asked (a restart with the Editor closed). Plain
 * and quoted YAML scalars only; anything else falls back to Unity's defaults.
 */
export function readProjectIdentity(projectPath: string): { company: string; product: string } | null {
  let text: string;
  try { text = readFileSync(join(projectPath, "ProjectSettings", "ProjectSettings.asset"), "utf8"); } catch { return null; }
  const scalar = (key: string): string | null => {
    const match = new RegExp(`^\\s*${key}:[ \\t]*(.*)$`, "m").exec(text);
    if (!match) return null;
    const raw = match[1].trim();
    if (raw.startsWith("'") && raw.endsWith("'") && raw.length >= 2) return raw.slice(1, -1).replace(/''/g, "'");
    if (raw.startsWith("\"") && raw.endsWith("\"") && raw.length >= 2) {
      try { return JSON.parse(raw) as string; } catch { return raw.slice(1, -1); }
    }
    return raw;
  };
  const company = scalar("companyName");
  const product = scalar("productName");
  if (company === null || product === null) return null;
  return { company, product };
}

export class SessionStore {
  constructor(readonly root: string) {}

  productDir(company: string, product: string): string {
    return join(this.root, safeSegment(company), safeSegment(product));
  }

  dir(meta: Pick<SessionMeta, "company" | "product" | "id">): string {
    return join(this.productDir(meta.company, meta.product), meta.id);
  }

  create(meta: SessionMeta): string {
    const dir = this.dir(meta);
    mkdirSync(join(dir, REPORT_DIR, FRAMES_DIR), { recursive: true });
    this.writeMeta(meta);
    writeFileSync(join(dir, LOG_FILE), "", { flag: "a" });
    return dir;
  }

  writeMeta(meta: SessionMeta): void {
    const dir = this.dir(meta);
    const target = join(dir, SESSION_FILE);
    const temp = `${target}.${process.pid}.tmp`;
    writeFileSync(temp, JSON.stringify(meta, null, 2));
    renameSync(temp, target);
  }

  readMeta(dir: string): SessionMeta | null {
    try {
      const meta = JSON.parse(readFileSync(join(dir, SESSION_FILE), "utf8")) as SessionMeta;
      return meta && meta.format === SESSION_FORMAT && typeof meta.id === "string" ? meta : null;
    } catch { return null; }
  }

  append(meta: Pick<SessionMeta, "company" | "product" | "id">, entry: Record<string, unknown>): void {
    appendFileSync(join(this.dir(meta), LOG_FILE), JSON.stringify(entry) + "\n");
  }

  /** Every parseable line of a session log; a torn last line (a crash mid-write) is skipped. */
  readLog(dir: string): Array<Record<string, any>> {
    let text: string;
    try { text = readFileSync(join(dir, LOG_FILE), "utf8"); } catch { return []; }
    const out: Array<Record<string, any>> = [];
    for (const line of text.split("\n")) {
      if (!line.trim()) continue;
      try { out.push(JSON.parse(line)); } catch { /* torn line */ }
    }
    return out;
  }

  /** Sessions of one product, newest first. */
  list(company: string, product: string): Array<{ dir: string; meta: SessionMeta }> {
    const base = this.productDir(company, product);
    if (!existsSync(base)) return [];
    const out: Array<{ dir: string; meta: SessionMeta }> = [];
    for (const name of readdirSync(base)) {
      if (!SESSION_ID_PATTERN.test(name)) continue;
      const dir = join(base, name);
      const meta = this.readMeta(dir);
      if (meta) out.push({ dir, meta });
    }
    return out.sort((a, b) => b.meta.started_at.localeCompare(a.meta.started_at));
  }
}
