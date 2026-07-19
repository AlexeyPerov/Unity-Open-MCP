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
