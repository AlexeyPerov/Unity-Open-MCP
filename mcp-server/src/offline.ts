// Offline asset reader — parses text-serialized Unity assets (.prefab/.unity/
// .asset) from disk without a running Editor.
//
// This file is a thin re-export barrel over the focused modules under
// `offline/` (M28-refactoring Plan 3, T3.1). The former 2708-LOC monolith was
// split along its natural seams:
//   - offline/types.ts       — shared internal types + lookup tables
//   - offline/primitives.ts  — low-level GUID/fileID/scalar extraction
//   - offline/names.ts       — field-name display helpers
//   - offline/parse.ts       — YAML document + JSON stream parsing
//   - offline/hierarchy.ts   — GameObject tree + component/field resolution
//   - offline/overrides.ts   — prefab-override parsing + label resolution
//   - offline/references.ts  — reference resolution (GUID/fileID → labels)
//   - offline/index-builders.ts — GUID/script index + directory walkers
//   - offline/model.ts       — AssetModel builders + integrity checks
//   - offline/api.ts         — the public entry points (this barrel's exports)
//
// Public API surface (unchanged): three entry points the compressible-tool
// router and tool-router call — readAssetOffline, searchAssetsOffline,
// listAssetsOffline — plus findReferencesOffline, dependenciesOffline,
// scanIntegrityOffline, isOfflineAsset, countHierarchy, and their result
// interfaces.
//
// The original header noted the file was monolithic "so it loads cleanly under
// node --experimental-strip-types in tests: no cross-file runtime imports".
// Decomposition is now safe: tests compile to dist-test/ via tsc (the
// strip-types path is not the test execution path), and the only strip-types
// entry — scripts/generate-token-estimates.mjs — imports tools/index.ts, which
// does not transitively reach this module. All submodules use static
// relative imports with `.js` specifiers (Node16 module resolution), so the
// import graph stays statically resolvable.

export {
  readAssetOffline,
  searchAssetsOffline,
  listAssetsOffline,
  findReferencesOffline,
  dependenciesOffline,
  scanIntegrityOffline,
  isOfflineAsset,
  countHierarchy,
} from "./offline/api.js";

export type {
  OfflineReadResult,
  FolderListing,
  ListAssetsResult,
  ReferencedByEntry,
  CollapsedGroup,
  FindReferencesOfflineResult,
  ForwardEdgeKind,
  ForwardEdge,
  ReverseEdge,
  ImpactEntry,
  DependenciesOfflineResult,
  IntegrityScanEntry,
  IntegrityScanResult,
} from "./offline/api.js";
