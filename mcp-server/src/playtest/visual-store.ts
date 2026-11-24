// Playtest visual baselines on disk — next to the scenario they belong to:
//
//   <project>/Playtests/Scenarios/<name>.json
//   <project>/Playtests/Scenarios/<name>.visual/<key>/index.json   step labels + capture info
//   <project>/Playtests/Scenarios/<name>.visual/<key>/00.png       the start screen
//   <project>/Playtests/Scenarios/<name>.visual/<key>/03.png       after step 3
//
// `key` names what the frames were taken on (visual.ts visualKey: platform +
// Simulator device / orientation or Game view size): a frame is compared only
// with one taken on the same. A record run replaces a key's folder as a whole.
// A frame whose index entry names another step label (the scenario was edited
// since) is not used: the step reports no baseline.
import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync, renameSync, rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { decodePng, type RgbaImage } from "./png.js";

export const VISUAL_INDEX_FORMAT = "unity-open-mcp.playtest-visual/1";

export interface VisualIndex {
  format: string;
  key: string;
  recorded_at: string;
  max_edge: number;
  captured_at?: Record<string, unknown>;
  steps: Array<{ n: number; label: string; file: string }>;
}

export interface VisualFrame { n: number; label: string; source: string }

/** The baselines folder of a scenario file: <name>.visual next to <name>.json. */
export function visualDirFor(scenarioFile: string): string {
  return scenarioFile.replace(/\.json$/i, "") + ".visual";
}

const frameName = (n: number) => `${String(n).padStart(2, "0")}.png`;

export function readVisualIndex(dir: string, key: string): VisualIndex | null {
  try {
    const index = JSON.parse(readFileSync(join(dir, key, "index.json"), "utf8")) as VisualIndex;
    return index && index.format === VISUAL_INDEX_FORMAT && Array.isArray(index.steps) ? index : null;
  } catch { return null; }
}

/** Keys with a readable index. */
export function visualKeys(dir: string): string[] {
  if (!existsSync(dir)) return [];
  return readdirSync(dir).filter(name => {
    try { return statSync(join(dir, name)).isDirectory() && existsSync(join(dir, name, "index.json")); } catch { return false; }
  }).sort();
}

/** Baselines of one key, read once per run. */
export class VisualBaselines {
  private readonly index: VisualIndex | null;
  private readonly cache = new Map<number, RgbaImage | null>();
  constructor(readonly dir: string, readonly key: string) { this.index = readVisualIndex(dir, key); }

  get exists(): boolean { return this.index !== null; }

  /** The baseline frame of step n (0 = start), or why there is none. */
  frame(n: number, label: string): { file: string; image: RgbaImage } | { missing: "no_key" | "no_step" | "other_step" | "unreadable"; detail?: string } {
    if (!this.index) return { missing: "no_key" };
    const entry = this.index.steps.find(s => s.n === n);
    if (!entry) return { missing: "no_step" };
    if (entry.label !== label) return { missing: "other_step", detail: `recorded for '${entry.label}'` };
    const file = join(this.dir, this.key, entry.file);
    if (!this.cache.has(n)) {
      try { this.cache.set(n, decodePng(readFileSync(file))); } catch { this.cache.set(n, null); }
    }
    const image = this.cache.get(n);
    return image ? { file, image } : { missing: "unreadable", detail: file };
  }
}

/** Replace a key's folder with these frames (written beside it first, then swapped in). */
export function writeVisualBaseline(dir: string, key: string, frames: VisualFrame[], info: { max_edge: number; captured_at?: Record<string, unknown>; now?: () => number }): { folder: string; frames: number } {
  const folder = join(dir, key);
  const staging = `${folder}.tmp-${process.pid}`;
  rmSync(staging, { recursive: true, force: true });
  mkdirSync(staging, { recursive: true });
  const steps: VisualIndex["steps"] = [];
  for (const frame of [...frames].sort((a, b) => a.n - b.n)) {
    const file = frameName(frame.n);
    copyFileSync(frame.source, join(staging, file));
    steps.push({ n: frame.n, label: frame.label, file });
  }
  const index: VisualIndex = {
    format: VISUAL_INDEX_FORMAT, key, recorded_at: new Date((info.now ?? Date.now)()).toISOString(), max_edge: info.max_edge,
    ...(info.captured_at ? { captured_at: info.captured_at } : {}), steps,
  };
  writeFileSync(join(staging, "index.json"), `${JSON.stringify(index, null, 2)}\n`);
  mkdirSync(dirname(folder), { recursive: true });
  const old = `${folder}.old-${process.pid}`;
  if (existsSync(folder)) renameSync(folder, old);
  renameSync(staging, folder);
  rmSync(old, { recursive: true, force: true });
  return { folder, frames: steps.length };
}
