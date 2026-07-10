// Path-normalization helpers for the offline reader.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). Converts absolute filesystem paths to project-relative asset paths
// (forward-slash normalized) and to relative directory segments. A leaf
// module — depends only on node:path.

import { relative, sep } from "node:path";
import { shouldSkipDir } from "./types.js";

export function toAssetPath(projectRoot: string, absPath: string): string {
  let rel = relative(projectRoot, absPath);
  if (sep === "\\") rel = rel.replace(/\\/g, "/");
  return rel;
}

export function relativeDir(projectRoot: string, absPath: string): string {
  let rel = absPath.slice(projectRoot.length).replace(/\\/g, "/");
  if (rel.startsWith("/")) rel = rel.slice(1);
  const lastSlash = rel.lastIndexOf("/");
  return lastSlash >= 0 ? rel.slice(0, lastSlash) : rel;
}

export { shouldSkipDir };
