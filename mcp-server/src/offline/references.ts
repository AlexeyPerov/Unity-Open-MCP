// Reference resolution for the offline reader.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). resolveReferences rewrites a scalar field value to inline the
// human-readable names of the assets it points at — via the GUID→path index for
// external refs, and via the local FileID→object map for in-file refs. This is
// the resolution primitive the hierarchy builder and JSON scalar renderer both
// consume (injected, to keep the dependency one-directional).

import { basename, extname } from "node:path";
import type { GUIDIndex, ParsedAsset } from "./types.js";
import { findFileIDs, findGUIDs } from "./primitives.js";
import {
  componentName,
  localReferenceLabel,
  objectPath,
} from "./hierarchy.js";

export function resolveReferences(
  value: string,
  guidIndex: GUIDIndex,
  asset: ParsedAsset,
): string {
  const guids = findGUIDs(value);
  if (guids.length > 0) {
    const refs: string[] = [];
    const seen = new Set<string>();
    for (const guid of guids) {
      const path = guidIndex.get(guid);
      if (!path || seen.has(path)) continue;
      seen.add(path);
      const name = basename(path);
      const ext = extname(name);
      refs.push(ext ? name.slice(0, -ext.length) : name);
    }
    if (refs.length > 0) return `${value} -> ${refs.join(", ")}`;
  }

  if (guids.length === 0) {
    const refs: string[] = [];
    const seen = new Set<string>();
    for (const fileID of findFileIDs(value)) {
      const label = localReferenceLabel(asset, fileID);
      if (label === "" || seen.has(label)) continue;
      seen.add(label);
      refs.push(label);
    }
    if (refs.length > 0) return `${value} -> ${refs.join(", ")}`;
  }

  return value;
}

// Wire the overrides label-registry with this module's hierarchy-backed
// implementations. overrides.ts needs objectPath / localReferenceLabel /
// componentName / resolveReferences for its label resolver, but cannot import
// references.ts (references imports overrides' collectOverrides). The
// late-bound registry breaks the cycle; this is the only wiring site.
export const OVERRIDE_LABEL_HELPERS = {
  objectPath,
  localReferenceLabel,
  componentName,
  resolveReferences,
};
