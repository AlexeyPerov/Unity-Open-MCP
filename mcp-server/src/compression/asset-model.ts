// M9 Plan 2 — Shared asset model contract.
//
// Normalized representation of a text-serialized Unity asset produced by a data
// source (live bridge today; offline YAML parser in M9 Plan 3). The compression
// module (compress.ts / render.ts) consumes this shape and emits the compact
// drill-down response. Both the live path (bridge -> AssetModel JSON -> MCP
// server compresses) and the future offline path (Node parser -> AssetModel ->
// same compression) flow through the same types so the compression code is
// written once.
//
// Design rule: the model is STRUCTURED DATA, never pre-compressed. All folding,
// component-set declaration, omission counts, and match-reason tagging happen in
// the compression module so the algorithm lives in exactly one place.

export type AssetKind =
  | "prefab"
  | "scene"
  | "asset"
  | "material"
  | "animation"
  | "controller"
  | "other";

export interface ComponentField {
  /** Serialized field name (Unity's backing-field name, e.g. m_Speed). */
  name: string;
  /** Field value rendered as a short string (already truncated by the source). */
  value: string;
}

export interface AssetComponent {
  /** Component type name (Transform, MeshRenderer, MonoBehaviour script name). */
  name: string;
  /** Resolved .cs path for MonoBehaviour scripts (offline GUID index; live omits). */
  scriptPath?: string;
  /** Serialized fields. Present only when the source fetched them (field_limit > 0). */
  fields?: ComponentField[];
}

export interface HierarchyNode {
  /** GameObject display name. */
  name: string;
  /** Slash-joined path from the asset root (e.g. "Player/Model/Helmet"). */
  path: string;
  /** Depth from the root (root = 0). */
  depth: number;
  components: AssetComponent[];
  children: HierarchyNode[];
  /** Local YAML fileID. Offline-only (Plan 3); the live bridge omits this. */
  fileID?: string;
}

/** Object entry for non-hierarchical assets (ScriptableObject, Material, .asset). */
export interface FlatObject {
  name: string;
  type: string;
  fields?: ComponentField[];
}

/** A single prefab variant override (from PrefabInstance m_Modifications etc.). */
export interface PrefabOverrideEntry {
  kind:
    | "property"
    | "added-component"
    | "added-gameobjects"
    | "removed-components"
    | "removed-gameobjects";
  /** Property path (backing-field names unwrapped). Property overrides only. */
  propertyPath?: string;
  /** Override value. Property overrides only. */
  value?: string;
  /** Resolved target label (GameObject path or "Component on Path"). Omitted when unresolvable. */
  target?: string;
  /** Resolved added-object label. Added-component / added-gameobjects only. */
  addedObject?: string;
}

export interface AssetModel {
  kind: AssetKind | string;
  path: string;
  guid?: string;
  /** Total YAML objects in the file (GameObjects + components + others). */
  objectCount: number;
  /** Total components across the hierarchy. */
  componentCount: number;
  roots: HierarchyNode[];
  /** Present for non-hierarchical assets (no GameObject tree). */
  flatObjects?: FlatObject[];
  /** Prefab variant overrides. Populated for prefabs with PrefabInstance objects. */
  overrides?: PrefabOverrideEntry[];
  /** Optional source-side notice (e.g. "scene hierarchy requires offline parser"). */
  note?: string;
}

export interface SearchObjectMatch {
  path: string;
  components: string[];
}

export interface SearchMatch {
  path: string;
  guid?: string;
  kind: string;
  /** Why this file matched: "file-name" | "gameobject" | "component" | "guid". */
  reasons: string[];
  /** Matching GameObjects / components inside the asset (structured search). */
  objects?: SearchObjectMatch[];
}

export interface SearchModel {
  query: { name?: string; component?: string; guid?: string; type?: string };
  matchCount: number;
  matches: SearchMatch[];
  /** Number of matches hidden by the source cap. */
  truncated: number;
}
