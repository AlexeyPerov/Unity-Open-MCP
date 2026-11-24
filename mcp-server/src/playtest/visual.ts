// Playtest visual baselines — compare a composed game frame with a stored one.
//
// The replay (replay.ts) checks what a screen IS (its controls and texts); this
// checks how it LOOKS: a moved or resized control, a missing icon, a wrong
// colour, a popup drawn under the HUD. Frames are composed captures (every
// camera plus overlay canvases, as the player sees them), compared here in the
// server with the PNG reader in png.ts — no bridge contract, no image
// dependency, and the baselines can live wherever the scenario lives.
//
// A pixel differs when any channel moves more than `pixelTolerance` (rendering
// noise, anti-aliasing); masked boxes are not compared (counters, timers,
// animated backgrounds). Differing pixels are counted per 16-px block; a block
// with fewer than 4 of them is noise (a stray anti-aliased edge). The verdict is
// the share of compared pixels in the remaining blocks against `threshold`; those
// blocks are joined into regions when they touch, so a result can say WHERE the
// screen changed — and, with the snapshot's rects, around which control.
//
// Pure: images in, numbers / boxes / a diff image out.
import type { RgbaImage } from "./png.js";
import { maskPath, type SnapshotNode } from "./detectors.js";

export const VISUAL_DEFAULTS = { threshold: 0.0002, pixel_tolerance: 24, max_edge: 640, mask_numbers: true } as const;
export const VISUAL_LIMITS = { max_regions: 10, block: 16, block_noise: 4 } as const;

/** [x, y, w, h] in image pixels, top-left origin. */
export type Box = [number, number, number, number];

export interface VisualCompareOptions {
  pixelTolerance: number;
  threshold: number;
  masks: Box[];
}

export interface VisualResult {
  width: number;
  height: number;
  /** Pixels compared (not masked). */
  compared: number;
  /** Every differing pixel, noise included. */
  mismatched: number;
  /** Differing pixels in blocks over the noise floor. */
  changed: number;
  /** changed / compared (0 when nothing is compared). */
  ratio: number;
  pass: boolean;
  /** Regions of differing pixels, largest first. */
  regions: Box[];
  /** Per-pixel mismatch map (1 = differs, 2 = masked), for the diff image. */
  map: Uint8Array;
}

export function compareImages(baseline: RgbaImage, current: RgbaImage, options: VisualCompareOptions): VisualResult | { sizeMismatch: { baseline: [number, number]; current: [number, number] } } {
  if (baseline.width !== current.width || baseline.height !== current.height)
    return { sizeMismatch: { baseline: [baseline.width, baseline.height], current: [current.width, current.height] } };
  const { width, height } = current;
  const map = new Uint8Array(width * height);
  for (const [bx, by, bw, bh] of options.masks) {
    const x0 = Math.max(0, Math.floor(bx)), y0 = Math.max(0, Math.floor(by));
    const x1 = Math.min(width, Math.ceil(bx + bw)), y1 = Math.min(height, Math.ceil(by + bh));
    for (let y = y0; y < y1; y++) map.fill(2, y * width + x0, y * width + Math.max(x0, x1));
  }
  let compared = 0, mismatched = 0;
  const tolerance = options.pixelTolerance;
  for (let p = 0, i = 0; p < map.length; p++, i += 4) {
    if (map[p] === 2) continue;
    compared++;
    const a = baseline.data, b = current.data;
    if (Math.abs(a[i] - b[i]) > tolerance || Math.abs(a[i + 1] - b[i + 1]) > tolerance
        || Math.abs(a[i + 2] - b[i + 2]) > tolerance || Math.abs(a[i + 3] - b[i + 3]) > tolerance) {
      map[p] = 1;
      mismatched++;
    }
  }
  const size = VISUAL_LIMITS.block;
  const cols = Math.ceil(width / size), rows = Math.ceil(height / size);
  const counts = new Uint32Array(cols * rows);
  if (mismatched)
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++)
      if (map[y * width + x] === 1) counts[Math.floor(y / size) * cols + Math.floor(x / size)]++;
  let changedPixels = 0;
  const changed = new Uint8Array(cols * rows);
  for (let b = 0; b < counts.length; b++)
    if (counts[b] >= VISUAL_LIMITS.block_noise) { changed[b] = 1; changedPixels += counts[b]; }
  const ratio = compared ? changedPixels / compared : 0;
  return {
    width, height, compared, mismatched, changed: changedPixels, ratio, pass: ratio <= options.threshold,
    regions: changedPixels ? regionsOf(changed, cols, rows, width, height) : [], map,
  };
}

/** Join touching changed blocks into boxes (largest first). */
function regionsOf(changed: Uint8Array, cols: number, rows: number, width: number, height: number): Box[] {
  const size = VISUAL_LIMITS.block;
  const seen = new Uint8Array(cols * rows);
  const boxes: Array<{ box: Box; blocks: number }> = [];
  for (let start = 0; start < changed.length; start++) {
    if (!changed[start] || seen[start]) continue;
    let minC = cols, minR = rows, maxC = -1, maxR = -1, blocks = 0;
    const stack = [start];
    seen[start] = 1;
    while (stack.length) {
      const cell = stack.pop()!;
      const c = cell % cols, r = Math.floor(cell / cols);
      blocks++;
      minC = Math.min(minC, c); maxC = Math.max(maxC, c); minR = Math.min(minR, r); maxR = Math.max(maxR, r);
      for (let dr = -1; dr <= 1; dr++) for (let dc = -1; dc <= 1; dc++) {
        const nc = c + dc, nr = r + dr;
        if (nc < 0 || nr < 0 || nc >= cols || nr >= rows) continue;
        const next = nr * cols + nc;
        if (changed[next] && !seen[next]) { seen[next] = 1; stack.push(next); }
      }
    }
    const x = minC * size, y = minR * size;
    boxes.push({ box: [x, y, Math.min(width, (maxC + 1) * size) - x, Math.min(height, (maxR + 1) * size) - y], blocks });
  }
  return boxes.sort((a, b) => b.blocks - a.blocks).slice(0, VISUAL_LIMITS.max_regions).map(b => b.box);
}

/** The current frame dimmed to grey, differing pixels red, masked areas blue-tinted, regions outlined. */
export function diffImage(current: RgbaImage, result: VisualResult): RgbaImage {
  const { width, height, map } = result;
  const out = new Uint8Array(width * height * 4);
  for (let p = 0, i = 0; p < map.length; p++, i += 4) {
    const grey = Math.round((current.data[i] * 0.3 + current.data[i + 1] * 0.59 + current.data[i + 2] * 0.11) * 0.45 + 60);
    if (map[p] === 1) { out[i] = 255; out[i + 1] = 32; out[i + 2] = 32; }
    else if (map[p] === 2) { out[i] = Math.round(grey * 0.6); out[i + 1] = Math.round(grey * 0.7); out[i + 2] = Math.min(255, grey + 70); }
    else { out[i] = grey; out[i + 1] = grey; out[i + 2] = grey; }
    out[i + 3] = 255;
  }
  for (const [x, y, w, h] of result.regions) {
    const line = (px: number, py: number) => {
      if (px < 0 || py < 0 || px >= width || py >= height) return;
      const i = (py * width + px) * 4;
      out[i] = 255; out[i + 1] = 210; out[i + 2] = 0;
    };
    for (let px = x; px < x + w; px++) { line(px, y); line(px, y + h - 1); }
    for (let py = y; py < y + h; py++) { line(x, py); line(x + w - 1, py); }
  }
  return { width, height, data: out };
}

// ---------------------------------------------------------------------------
// Snapshot rects ↔ image boxes
// ---------------------------------------------------------------------------

/** A snapshot rect ([x, y, w, h], screen pixels, bottom-left origin) as an image box. */
export function screenRectToBox(rect: number[], screen: { w: number; h: number }, image: { width: number; height: number }): Box | null {
  if (!Array.isArray(rect) || rect.length < 4 || !(screen.w > 0) || !(screen.h > 0)) return null;
  const sx = image.width / screen.w, sy = image.height / screen.h;
  const [x, y, w, h] = rect.map(Number);
  return [x * sx, (screen.h - (y + h)) * sy, w * sx, h * sy];
}

/**
 * Boxes not to compare: the scenario's masks — a hierarchy path or path suffix
 * (every visible node at or under it), or a normalized [x, y, w, h] (0–1, top-left
 * origin) — plus, with maskNumbers, every visible text showing a digit (counters,
 * timers, scores: the screen signature masks them too).
 */
export function masksFor(
  nodes: SnapshotNode[], screen: { w: number; h: number } | null, image: { width: number; height: number },
  masks: Array<string | number[]>, maskNumbers: boolean,
): Box[] {
  const boxes: Box[] = [];
  for (const mask of masks) {
    if (Array.isArray(mask) && mask.length === 4) {
      const [x, y, w, h] = mask.map(Number);
      boxes.push([x * image.width, y * image.height, w * image.width, h * image.height]);
    }
  }
  if (!screen) return boxes;
  const paths = masks.filter((m): m is string => typeof m === "string" && m.length > 0).map(maskPath);
  for (const node of nodes) {
    if (node.visible === false || !node.rect) continue;
    const path = maskPath(node.path);
    const byPath = paths.some(p => path === p || path.endsWith(`/${p}`) || path.startsWith(`${p}/`) || path.includes(`/${p}/`));
    const counter = maskNumbers && node.role === "text" && /\d/.test(String(node.text ?? ""));
    if (!byPath && !counter) continue;
    const box = screenRectToBox(node.rect, screen, image);
    if (box) boxes.push(box);
  }
  return boxes;
}

/** A region over most of the frame (a background, a theme, another screen): naming controls in it says nothing. */
export function isWholeScreen(region: Box, image: { width: number; height: number }): boolean {
  return region[2] * region[3] >= image.width * image.height * 0.6;
}

/** The controls / texts a region covers, smallest first: "button 'Shop' (Menu/ShopButton)". */
export function nodesInRegion(region: Box, nodes: SnapshotNode[], screen: { w: number; h: number } | null, image: { width: number; height: number }): string[] {
  if (!screen || isWholeScreen(region, image)) return [];
  const [rx, ry, rw, rh] = region;
  const hits: Array<{ area: number; cover: number; text: string }> = [];
  for (const node of nodes) {
    if (node.visible === false || !node.rect || node.role === "container") continue;
    const box = screenRectToBox(node.rect, screen, image);
    if (!box) continue;
    const [x, y, w, h] = box;
    const ix = Math.max(0, Math.min(rx + rw, x + w) - Math.max(rx, x));
    const iy = Math.max(0, Math.min(ry + rh, y + h) - Math.max(ry, y));
    if (ix * iy <= 0) continue;
    // A full-screen panel or background covers every region: not informative.
    if (w * h > image.width * image.height * 0.5) continue;
    const label = String(node.text ?? "").trim();
    // How much of the node the region covers: a node the change sits on beats a neighbour it grazes.
    const cover = (ix * iy) / Math.max(1, w * h);
    if (cover < 0.15) continue;
    hits.push({ area: w * h, cover, text: `${node.role}${label ? ` '${label.length > 30 ? `${label.slice(0, 29)}…` : label}'` : ""} (${node.path.split("/").slice(-2).join("/")})` });
  }
  return [...new Set(hits.sort((a, b) => b.cover - a.cover || a.area - b.area).map(h => h.text))].slice(0, 3);
}

/** A region as a person reads it: where on the screen, in percent. */
export function describeRegion(region: Box, image: { width: number; height: number }): string {
  if (isWholeScreen(region, image)) return "across the whole screen";
  const [x, y, w, h] = region;
  const pct = (v: number, of: number) => Math.round((v / of) * 100);
  return `x ${pct(x, image.width)}–${pct(x + w, image.width)}%, y ${pct(y, image.height)}–${pct(y + h, image.height)}%`;
}

/**
 * The baseline folder key of a capture: platform (rendering differs across
 * OS / GPU) + the Simulator device and orientation, or the Game view size.
 */
export function visualKey(capturedAt: Record<string, unknown> | undefined, platform: string = process.platform): string {
  const slug = (value: string) => value.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 60);
  const at = capturedAt ?? {};
  const screen = Array.isArray(at.screen) ? at.screen.map(Number) : [];
  const size = screen.length === 2 ? `${screen[0]}x${screen[1]}` : "unknown";
  const view = at.mode === "simulator" && typeof at.device === "string"
    ? `${slug(at.device)}-${slug(String(at.orientation ?? "portrait"))}`
    : `game-${size}`;
  return `${slug(platform)}-${view}`;
}
