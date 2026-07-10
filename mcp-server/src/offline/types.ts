// Shared internal types and constants for the offline asset reader.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). These types and lookup tables are imported by every offline submodule
// — parse, hierarchy, overrides, references, primitives. Keeping them in one
// leaf module with no runtime imports of its own preserves the static,
// type-stripping-safe import graph.

import { extname } from "node:path";
import type {
  PrefabOverrideEntry,
  AssetIntegrityIssue,
} from "../compression/asset-model.js";

// ===========================================================================
// Internal parser types.
// ===========================================================================

export interface ParsedObject {
  id: string;
  classID: number;
  type: string;
  lines: string[];
  order: number;
  name: string;
  componentIDs: string[];
  gameObjectID: string;
  fatherTransformID: string;
  sourceObjectID: string;
  sourceGUID: string;
  scriptGUID: string;
}

export interface HierarchyResult {
  gameObject: ParsedObject;
  children: HierarchyResult[];
  path: string;
  depth: number;
}

export interface ParsedAsset {
  path: string;
  kind: string;
  guid: string;
  objects: ParsedObject[];
  byID: Map<string, ParsedObject>;
}

export type ScriptIndex = Map<string, string>;
export type GUIDIndex = Map<string, string>;

export interface PrefabOverride {
  kind: string;
  target: string;
  propertyPath: string;
  value: string;
  addedObject: string;
}

export interface HeaderInfo {
  classID: number;
  id: string;
}

export interface JsonParsedObject {
  /** Display label (m_Name / m_ObjectId / name key, or a generated label). */
  name: string;
  /** Type label (top-level kind, or the m_Type discriminator for shadergraph). */
  type: string;
  /** The parsed JSON value. */
  value: unknown;
}

// Re-export the asset-model integrity/override types the submodules consume,
// so each submodule imports model types from one place. These are type-only.
export type { PrefabOverrideEntry, AssetIntegrityIssue };

// ===========================================================================
// Native Unity class IDs → names.
// ===========================================================================

export const nativeClassNames: Record<number, string> = {
  1: "GameObject",
  4: "Transform",
  20: "Camera",
  23: "MeshRenderer",
  33: "MeshFilter",
  64: "MeshCollider",
  65: "BoxCollider",
  81: "AudioListener",
  82: "AudioSource",
  95: "Animator",
  108: "Light",
  114: "MonoBehaviour",
  115: "MonoScript",
  120: "LineRenderer",
  137: "SkinnedMeshRenderer",
  156: "Terrain",
  198: "ParticleSystem",
  212: "SpriteRenderer",
  222: "CanvasRenderer",
  223: "Canvas",
  224: "RectTransform",
  225: "CanvasGroup",
  329: "VideoPlayer",
  73398921: "VFXRenderer",
};

export function nativeClassName(classID: number): string {
  return nativeClassNames[classID] ?? "";
}

// ===========================================================================
// Kind / extension utilities.
// ===========================================================================

export const extToKind: Record<string, string> = {
  ".anim": "anim",
  ".asmdef": "asmdef",
  ".asset": "asset",
  ".controller": "controller",
  ".cs": "cs",
  ".mat": "mat",
  ".overrideController": "controller",
  ".physicsMaterial2D": "physics2d",
  ".physicMaterial": "physics",
  ".playable": "playable",
  ".prefab": "prefab",
  ".preset": "preset",
  ".shader": "shader",
  ".shadergraph": "shadergraph",
  ".shadersubgraph": "shadergraph",
  ".spriteatlas": "atlas",
  ".terrainlayer": "terrainlayer",
  ".unity": "scene",
  ".uss": "uss",
  ".uxml": "uxml",
  ".vfx": "vfx",
  ".meta": "meta",
};

// ===========================================================================
// M24 — offline-parseable extension sets.
// YAML_PARSEABLE covers text-serialized Unity YAML; JSON_PARSEABLE covers the
// JSON asset kinds unity-scanner does not handle (our differentiator). Both are
// merged into OFFLINE_PARSEABLE for the read_asset / search / find_references
// gates. LISTABLE stays broader (it never parses, only enumerates files).
// ===========================================================================

export const YAML_PARSEABLE_EXTENSIONS = new Set([
  ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim", ".playable",
  ".preset", ".spriteatlas", ".terrainlayer", ".vfx",
]);

export const JSON_PARSEABLE_EXTENSIONS = new Set([
  ".asmdef", ".shadergraph", ".shadersubgraph",
]);

export const OFFLINE_PARSEABLE_EXTENSIONS = new Set<string>([
  ...YAML_PARSEABLE_EXTENSIONS,
  ...JSON_PARSEABLE_EXTENSIONS,
]);

export function offlineParseKind(assetPath: string): "yaml" | "json" | null {
  const dot = assetPath.lastIndexOf(".");
  if (dot < 0) return null;
  const ext = assetPath.slice(dot).toLowerCase();
  if (YAML_PARSEABLE_EXTENSIONS.has(ext)) return "yaml";
  if (JSON_PARSEABLE_EXTENSIONS.has(ext)) return "json";
  return null;
}

export const kindAliases: Record<string, string> = {
  prefabs: "prefab",
  scenes: "scene",
  unity: "scene",
  scripts: "cs",
  script: "cs",
  material: "mat",
  materials: "mat",
};

export function kindForPath(path: string): string {
  const ext = extname(path);
  if (extToKind[ext]) return extToKind[ext];
  if (ext === "") return "none";
  return ext.slice(1).toLowerCase();
}

export function normalizeKind(kind: string): string {
  const trimmed = kind.trim().toLowerCase();
  if (kindAliases[trimmed]) return kindAliases[trimmed];
  return trimmed;
}

export function parseKindSet(raw: string): Set<string> {
  const out = new Set<string>();
  for (const part of raw.split(",")) {
    const kind = normalizeKind(part);
    if (kind !== "") out.add(kind);
  }
  return out;
}

export const skipDirs = new Set([
  ".git", ".vs", "Library", "Logs", "obj", "Obj",
  "Temp", "Build", "Builds", "UserSettings", "node_modules",
]);

export function shouldSkipDir(name: string): boolean {
  return skipDirs.has(name);
}

// ===========================================================================
// Field extraction allow-list (shared by YAML + prefab-override renderers).
// ===========================================================================

export const skipFieldNames = new Set([
  "m_Name", "m_ObjectHideFlags", "m_CorrespondingSourceObject",
  "m_PrefabInstance", "m_PrefabAsset", "serializedVersion",
  "m_GameObject", "m_Script", "m_Enabled",
  "m_EditorHideFlags", "m_EditorClassIdentifier", "m_Modification",
]);

// A ParsedAsset stand-in used only by resolveReferences' local-reference path.
// JSON assets have no in-file fileIDs, so local ref resolution is a no-op.
export const EMPTY_PARSED: ParsedAsset = {
  path: "",
  kind: "",
  guid: "",
  objects: [],
  byID: new Map(),
};
