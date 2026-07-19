// Path-normalization helpers for the offline reader.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). Converts absolute filesystem paths to project-relative asset paths
// (forward-slash normalized) and to relative directory segments. A leaf
// module — depends only on node:path.

import { relative, sep } from "node:path";
import { shouldSkipDir } from "./types.js";

// M31-optimizations Plan 3 / L8-offline — module-scope regex. The Windows
// backslash→slash normalization is conditional (sep === "\\"), so on POSIX
// the replace never runs; the literal is hoisted regardless so the single-
// compile guarantee is explicit.
const BACKSLASH_RE = /\\/g;

// M31-optimizations Plan 4 / T4.3 — splits a project-relative asset path into
// segments for `.`/`..` collapse. Runs after backslash→slash normalization so
// the only separator in play is `/`.
const PATH_SEGMENT_SPLIT_RE = /\//g;

// M31-optimizations Plan 3 / L8-offline — single source of truth for the
// "trailing extension" regex. Previously duplicated inline at
// index-builders.ts (inside walkMeta's per-.cs.meta callback) and
// overrides.ts:308 (scriptBaseName). Hoisting to one named constant and
// exposing it via extractExtension lets both call sites share the compile.
const TRAILING_EXT_RE = /\.[^.]+$/;

export function toAssetPath(projectRoot: string, absPath: string): string {
  let rel = relative(projectRoot, absPath);
  if (sep === "\\") rel = rel.replace(BACKSLASH_RE, "/");
  return rel;
}

export function relativeDir(projectRoot: string, absPath: string): string {
  let rel = absPath.slice(projectRoot.length).replace(BACKSLASH_RE, "/");
  if (rel.startsWith("/")) rel = rel.slice(1);
  const lastSlash = rel.lastIndexOf("/");
  return lastSlash >= 0 ? rel.slice(0, lastSlash) : rel;
}

/**
 * M31-optimizations Plan 4 / T4.3 (M6) — normalize a project-relative asset
 * path so different spellings of the same file collapse to one string. Handles
 * the four documented spellings that previously produced distinct cache keys:
 *
 *   - `Assets/Foo.prefab`
 *   - `./Assets/Foo.prefab`           (leading `./` on a single segment)
 *   - `Assets/../Assets/Foo.prefab`   (`..` collapse against a real segment)
 *   - `Assets\Foo.prefab`             (Windows backslash)
 *
 * The transform is purely lexical (no filesystem access): backslashes become
 * forward slashes, then a single segment-walk collapses `.` and resolves `..`
 * against the preceding segment. A leading `..` that would escape the project
 * root is preserved (NOT dropped) — the caller still passes the resulting
 * path to the offline parser, which validates it against the project tree.
 *
 * Exposed so the compressible AssetModelCache can build a cache key without
 * re-forking the normalization logic.
 */
export function normalizeAssetPath(assetPath: string): string {
  const forward = assetPath.replace(BACKSLASH_RE, "/");
  const segments = forward.split(PATH_SEGMENT_SPLIT_RE);
  const out: string[] = [];
  for (const seg of segments) {
    if (seg === "" || seg === ".") continue;
    if (seg === "..") {
      // Collapse against the preceding real segment when one exists AND it is
      // not itself `..` (so a leading `..` that escapes the root is preserved).
      const prev = out[out.length - 1];
      if (prev !== undefined && prev !== "..") {
        out.pop();
        continue;
      }
    }
    out.push(seg);
  }
  return out.join("/");
}

/**
 * M31-optimizations Plan 3 / L8-offline — extract the trailing extension
 * (including the dot) from a path, or return "" when the basename has no dot.
 *
 * Single source of truth for the `path.match(/\.[^.]+$/)` logic that was
 * duplicated inline in `index-builders.ts` (walkMeta's per-`.cs.meta`
 * callback, on the hot meta-walk path) and `overrides.ts`'s `scriptBaseName`.
 * Both call sites now delegate here. The regex matches the LAST dot-to-end
 * span so a path like `Assets/Foo.bar.cs` yields `.cs` (matches the previous
 * inline behavior byte-for-byte).
 */
export function extractExtension(path: string): string {
  const match = path.match(TRAILING_EXT_RE);
  return match ? match[0] : "";
}

export { shouldSkipDir };
