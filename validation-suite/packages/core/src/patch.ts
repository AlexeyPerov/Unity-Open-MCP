/**
 * Deterministic text-patch transform (phase-2 task 3).
 *
 * Pure reference implementation of the pinned patch-op vocabulary
 * (`replace_line_contains`, `insert_after_line_contains`,
 * `insert_before_line_contains`, `trim_trailing_whitespace`). Mirrored
 * in Rust (`fs_ops::apply_patches`) so the executor and the unit tests
 * agree on exact line semantics.
 *
 * Semantics (locked):
 *  - Input is treated as UTF-8 text split on `\n`. A trailing newline is
 *    preserved; lines retain their terminators during matching.
 *  - `match` is a substring test against a line's content (excluding the
 *    trailing newline). The *first* matching line wins; a missing match
 *    throws (deterministic failure rather than a silent no-op).
 *  - `replace_line_contains` replaces the entire matched line (with its
 *    trailing newline kept) with `replace`.
 *  - `insert_after`/`insert_before` splice the `insert` string (which may
 *    itself be multi-line) on the matching side, preserving the matched
 *    line and its newline.
 *  - `trim_trailing_whitespace` strips trailing ASCII whitespace from
 *    every line (spaces + tabs) but keeps line terminators.
 */

import type { FsPatchEntry } from "./types.ts";

/** Split text into lines keeping the terminating newline on each line. */
export function splitLines(text: string): string[] {
  // Keep newlines attached so insert/replace preserve them exactly.
  const out: string[] = [];
  let start = 0;
  for (let i = 0; i < text.length; i++) {
    if (text[i] === "\n") {
      out.push(text.slice(start, i + 1));
      start = i + 1;
    }
  }
  if (start < text.length) out.push(text.slice(start));
  return out;
}

/** Content of a line without its trailing newline (for substring matching). */
function lineContent(line: string): string {
  return line.endsWith("\n") ? line.slice(0, -1) : line;
}

/** Index of the first line whose content contains `match`, or -1. */
function findLine(lines: string[], match: string): number {
  for (let i = 0; i < lines.length; i++) {
    if (lineContent(lines[i]).includes(match)) return i;
  }
  return -1;
}

/**
 * Apply an ordered list of patches to `text`. Throws on a missing match
 * (deterministic failure) so the executor can record a clean error in
 * the action log. Returns the patched text.
 */
export function applyPatches(text: string, patches: FsPatchEntry[]): string {
  let lines = splitLines(text);
  for (const patch of patches) {
    switch (patch.op) {
      case "replace_line_contains": {
        const match = patch.match ?? "";
        const replace = patch.replace ?? "";
        const idx = findLine(lines, match);
        if (idx === -1) {
          throw new Error(
            `replace_line_contains: no line matched "${match}".`,
          );
        }
        // Preserve the original trailing newline.
        const hadNewline = lines[idx].endsWith("\n");
        lines[idx] = hadNewline ? `${replace}\n` : replace;
        break;
      }
      case "insert_after_line_contains": {
        const match = patch.match ?? "";
        const insert = patch.insert ?? "";
        const idx = findLine(lines, match);
        if (idx === -1) {
          throw new Error(
            `insert_after_line_contains: no line matched "${match}".`,
          );
        }
        // `insert` may be multi-line; ensure each inserted line ends with
        // a newline so the spliced block reads back identically.
        const insertLines = splitLines(ensureTrailingNewline(insert));
        lines.splice(idx + 1, 0, ...insertLines);
        break;
      }
      case "insert_before_line_contains": {
        const match = patch.match ?? "";
        const insert = patch.insert ?? "";
        const idx = findLine(lines, match);
        if (idx === -1) {
          throw new Error(
            `insert_before_line_contains: no line matched "${match}".`,
          );
        }
        const insertLines = splitLines(ensureTrailingNewline(insert));
        lines.splice(idx, 0, ...insertLines);
        break;
      }
      case "trim_trailing_whitespace": {
        lines = lines.map((line) => {
          const nl = line.endsWith("\n") ? "\n" : "";
          return lineContent(line).replace(/[ \t]+$/g, "") + nl;
        });
        break;
      }
      default: {
        throw new Error(`Unknown patch op: ${String((patch as { op?: string }).op)}`);
      }
    }
  }
  return lines.join("");
}

/** Ensure `s` ends with a newline (for insert blocks that should be line-safe). */
function ensureTrailingNewline(s: string): string {
  return s.endsWith("\n") ? s : `${s}\n`;
}
