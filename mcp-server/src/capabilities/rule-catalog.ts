// Capability-discovery rule catalog.
//
// Static metadata for verify rules and fix providers. This is the documented
// capability surface agents discover via `unity_open_mcp_capabilities`. The
// catalog is versioned with the package: `implemented` flags reflect what
// ships in the matching bridge/verify package release.
//
// Implemented-rule metadata mirrors the C# verify package
// (packages/verify/Editor/Rules/*) issue mappers and the fix registry
// (packages/verify/Editor/Fixes/FixProviderRegistry.cs). Planned rules mirror
// the extension-heuristic stubs in the bridge gate adapter
// (packages/bridge/Editor/Gate/VerifyGateAdapter.cs).

export type CapabilityStatus = "implemented" | "planned";

export interface RuleIssueDescriptor {
  /** Issue code emitted by the rule (e.g. `missing_script`). */
  code: string;
  /** Default severity (`Error` | `Warning`). */
  severity: "Error" | "Warning";
  /** Fix IDs that can resolve this issue code, if any. */
  fixIds: string[];
  /** True when the issue is only emitted during a full project scan. */
  fullScanOnly?: boolean;
}

export interface RuleCapability {
  id: string;
  title: string;
  description: string;
  /** Asset kinds the rule analyzes (e.g. `prefab`, `scene`). */
  applicableAssetKinds: string[];
  /** File extensions the rule applies to, when known. */
  applicableExtensions?: string[];
  implemented: boolean;
  status: CapabilityStatus;
  /** Issue codes this rule can emit. Empty for planned rules. */
  issues: RuleIssueDescriptor[];
  /** Guidance shown to the agent when the rule is not implemented. */
  guidance?: string;
}

export interface FixCapability {
  id: string;
  implemented: boolean;
  status: CapabilityStatus;
  /** Rule IDs this fix can resolve issues for. */
  rules: string[];
  /** Issue codes this fix addresses. */
  issueCodes: string[];
  /** True when the fix is safe to auto-apply (no destructive side effects). */
  safe: boolean;
  /** Guidance shown to the agent when the fix is not implemented. */
  guidance?: string;
}

// ---------------------------------------------------------------------------
// Implemented rules — mirror packages/verify/Editor/Rules/*
// ---------------------------------------------------------------------------

const MISSING_REFERENCES_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "missing_guid",
    severity: "Error",
    fixIds: ["relink_broken_guid"],
  },
  {
    code: "missing_fileid",
    severity: "Error",
    fixIds: [],
  },
  {
    code: "missing_local_fileid",
    severity: "Warning",
    fixIds: [],
  },
  {
    code: "empty_local_ref",
    severity: "Warning",
    fixIds: [],
  },
  {
    code: "missing_method",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
  },
  {
    code: "type_mismatch",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
  },
  {
    code: "missing_script",
    severity: "Error",
    fixIds: ["remove_missing_script"],
  },
  {
    code: "duplicate_component",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
  },
  {
    code: "invalid_layer",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
  },
];

const SCENE_PREFAB_HEALTH_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "broken_reference",
    severity: "Error",
    fixIds: [],
  },
  {
    code: "high_risk_bootstrap",
    severity: "Warning",
    fixIds: [],
  },
  {
    code: "scene_object_count",
    severity: "Warning",
    fixIds: [],
  },
  {
    code: "component_hotspot",
    severity: "Warning",
    fixIds: [],
  },
  {
    code: "inactive_expensive",
    severity: "Warning",
    fixIds: [],
  },
  {
    code: "inactive_heavy",
    severity: "Warning",
    fixIds: [],
  },
  {
    code: "deep_nesting",
    severity: "Warning",
    fixIds: [],
  },
  {
    code: "override_explosion",
    severity: "Warning",
    fixIds: [],
  },
];

const DEPENDENCIES_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "broken_dependency",
    severity: "Error",
    fixIds: ["relink_broken_guid"],
  },
  {
    code: "dependency_cycle",
    severity: "Warning",
    fixIds: [],
  },
];

// M24 Plan 2 / T24.2 — offline project-wide integrity codes. Emitted by the
// offline scanIntegrityOffline() walker (mcp-server/src/offline.ts), the
// offline seed for the M25 verify engine. These are the first codes to fill
// the remove_orphan_meta / fix_duplicate_guid fix placeholders (flipped to
// implemented below). The per-read offline integrity codes
// (missing_reference / missing_script_reference / orphaned_prefab_instance /
// malformed_json / asmdef_missing_name / shadergraph_root_missing) surface on
// every read_asset call; this rule family is the project-wide aggregator.
const OFFLINE_INTEGRITY_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "orphan_meta",
    severity: "Warning",
    fixIds: ["remove_orphan_meta"],
    fullScanOnly: true,
  },
  {
    code: "duplicate_guid",
    severity: "Error",
    fixIds: ["fix_duplicate_guid"],
    fullScanOnly: true,
  },
  {
    code: "missing_reference",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
  },
  {
    code: "missing_script_reference",
    severity: "Error",
    fixIds: ["remove_missing_script"],
    fullScanOnly: true,
  },
];

// M25 Plan 1 — wave-1 rule families ported from backlog-verify-rules.
// Each entry mirrors the C# verify package issue mappers
// (packages/verify/Editor/Rules/*). The issue codes are the link keys the
// fix registry matches on.

const ASMDEF_AUDIT_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "broken_asmdef_reference",
    severity: "Error",
    fixIds: [],
  },
  {
    code: "asmdef_missing_name",
    severity: "Error",
    fixIds: [],
  },
  {
    code: "malformed_asmdef",
    severity: "Error",
    fixIds: [],
  },
];

const PROJECT_HEALTH_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "orphan_meta",
    severity: "Warning",
    fixIds: ["remove_orphan_meta"],
    fullScanOnly: true,
  },
  {
    code: "duplicate_guid",
    severity: "Error",
    fixIds: ["fix_duplicate_guid"],
    fullScanOnly: true,
  },
  {
    code: "missing_project_setting",
    severity: "Error",
    fixIds: [],
    fullScanOnly: true,
  },
];

const MATERIALS_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "missing_shader",
    severity: "Error",
    fixIds: ["reassign_missing_shader"],
  },
  {
    code: "missing_texture",
    severity: "Error",
    fixIds: ["reassign_missing_texture"],
  },
];

const ANIMATION_ANALYSIS_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "missing_clip",
    severity: "Error",
    fixIds: [],
  },
  {
    code: "empty_clip",
    severity: "Warning",
    fixIds: [],
  },
];

const SHADER_ANALYSIS_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "shader_compile_error",
    severity: "Error",
    fixIds: [],
  },
  {
    code: "missing_shader_asset",
    severity: "Error",
    fixIds: [],
  },
];

// ---------------------------------------------------------------------------
// Planned rules — mirror stubs in VerifyGateAdapter.SelectRuleIds
// ---------------------------------------------------------------------------

const PLANNED_RULES: RuleCapability[] = [
  {
    id: "textures",
    title: "Texture analysis",
    description:
      "Flags oversized textures, missing import settings, and uncompressed formats on mobile.",
    applicableAssetKinds: ["texture"],
    applicableExtensions: [".png", ".jpg", ".jpeg", ".tga"],
    implemented: false,
    status: "planned",
    issues: [],
    guidance:
      "Not yet ported. Use read_asset on .meta files or inspect TextureImporter settings via execute_csharp.",
  },
  {
    id: "sprite_2d_analysis",
    title: "Sprite 2D analysis",
    description:
      "Validates sprite atlas packing, multiple-sprite geometry, and 2D animation bone references.",
    applicableAssetKinds: ["sprite"],
    applicableExtensions: [".png", ".jpg", ".jpeg", ".tga"],
    implemented: false,
    status: "planned",
    issues: [],
    guidance: "Not yet ported. Inspect sprite importers via read_asset or execute_csharp.",
  },
  {
    id: "audio_analysis",
    title: "Audio analysis",
    description:
      "Flags uncompressed audio, missing platform overrides, and load-type mismatches.",
    applicableAssetKinds: ["audio"],
    applicableExtensions: [".wav", ".mp3", ".ogg"],
    implemented: false,
    status: "planned",
    issues: [],
    guidance:
      "Not yet ported. Inspect AudioImporter settings via read_asset or execute_csharp.",
  },
];

// ---------------------------------------------------------------------------
// Full catalog
// ---------------------------------------------------------------------------

export const RULE_CATALOG: RuleCapability[] = [
  {
    id: "missing_references",
    title: "Missing references",
    description:
      "Detects broken PPtr references (missing GUID/FileID), missing script GUIDs, " +
      "UnityEvent target gaps, duplicate components, and invalid layers across prefabs, " +
      "scenes, and ScriptableObject assets.",
    applicableAssetKinds: ["prefab", "scene", "scriptable_object"],
    applicableExtensions: [".prefab", ".unity", ".asset"],
    implemented: true,
    status: "implemented",
    issues: MISSING_REFERENCES_ISSUES,
  },
  {
    id: "scene_prefab_health",
    title: "Scene and prefab health",
    description:
      "Scene and prefab structural health: broken references, bootstrap density, object/component " +
      "counts, inactive-object waste, prefab nesting depth, and override explosion.",
    applicableAssetKinds: ["scene", "prefab"],
    applicableExtensions: [".unity", ".prefab"],
    implemented: true,
    status: "implemented",
    issues: SCENE_PREFAB_HEALTH_ISSUES,
  },
  {
    id: "dependencies",
    title: "Forward dependency graph",
    description:
      "Forward-graph view of what each scoped asset depends on. Complements the reverse " +
      "find_references lookup and the per-PPtr-field missing_references rule: catches " +
      "asset-graph edges (PPtr, AssetReference, addressable) that do not resolve, plus " +
      "forward dependency cycles scoped to paths_hint.",
    applicableAssetKinds: ["prefab", "scene", "scriptable_object", "material", "animation"],
    applicableExtensions: [".prefab", ".unity", ".asset", ".mat", ".controller", ".anim"],
    implemented: true,
    status: "implemented",
    issues: DEPENDENCIES_ISSUES,
  },
  {
    id: "offline_integrity",
    title: "Offline project-wide integrity",
    description:
      "Project-wide integrity signals computed offline (no running Editor): " +
      "orphaned .meta files (companion asset deleted), duplicate GUIDs (two+ " +
      "assets sharing one GUID), and the aggregated missing-reference / " +
      "missing-script set across Assets/. Emitted by the offline integrity " +
      "scanner — the offline seed for the structured verify rules. Per-asset " +
      "variants of these codes surface on every read_asset call; this rule " +
      "is the full-scan aggregator.",
    applicableAssetKinds: ["prefab", "scene", "scriptable_object", "material", "animation", "meta"],
    implemented: true,
    status: "implemented",
    issues: OFFLINE_INTEGRITY_ISSUES,
  },
  {
    id: "asmdef_audit",
    title: "Assembly definition audit",
    description:
      "Validates assembly definition references, naming, and dependency " +
      "graph health: broken references (GUID or bare-name that does not " +
      "resolve to a compiled assembly), a missing required `name` field, " +
      "and malformed JSON.",
    applicableAssetKinds: ["asmdef"],
    applicableExtensions: [".asmdef", ".cs"],
    implemented: true,
    status: "implemented",
    issues: ASMDEF_AUDIT_ISSUES,
  },
  {
    id: "project_health",
    title: "Project health",
    description:
      "In-Editor project-wide health: orphaned .meta files (companion asset " +
      "deleted), duplicate GUIDs (two+ assets sharing one GUID), and " +
      "ProjectSettings integrity (missing required settings files, no editor " +
      "version). Full-scan only — does not fire on a scoped validate_edit. " +
      "The live counterpart of the offline_integrity aggregator.",
    applicableAssetKinds: ["meta", "project_settings"],
    implemented: true,
    status: "implemented",
    issues: PROJECT_HEALTH_ISSUES,
  },
  {
    id: "materials",
    title: "Material health",
    description:
      "Detects broken material references: a shader GUID that does not " +
      "resolve (missing_shader) or a texture GUID that does not resolve " +
      "(missing_texture). Built-in shader GUIDs (all-zero) are treated as " +
      "valid Unity built-ins.",
    applicableAssetKinds: ["material"],
    applicableExtensions: [".mat"],
    implemented: true,
    status: "implemented",
    issues: MATERIALS_ISSUES,
  },
  {
    id: "animation_analysis",
    title: "Animation analysis",
    description:
      "Detects broken animator state motion references (missing_clip — a " +
      "controller state points at a GUID that does not resolve) and empty " +
      "animation clips (empty_clip — a .anim that declares no curves).",
    applicableAssetKinds: ["animation"],
    applicableExtensions: [".controller", ".anim"],
    implemented: true,
    status: "implemented",
    issues: ANIMATION_ANALYSIS_ISSUES,
  },
  {
    id: "shader_analysis",
    title: "Shader analysis",
    description:
      "Inspects shader compile state: a .shader that failed to load " +
      "(missing_shader_asset) or a .shader Unity reports as unsupported / " +
      "with compile messages (shader_compile_error). Compile messages are " +
      "read via ShaderUtil reflection so the rule stays Unity-version-portable.",
    applicableAssetKinds: ["shader"],
    // .mat is listed because the gate pairs shader_analysis with materials for
    // material edits (a broken material often points at a broken shader) and
    // list_rules filters .mat to both rules.
    applicableExtensions: [".shader", ".shadergraph", ".mat"],
    implemented: true,
    status: "implemented",
    issues: SHADER_ANALYSIS_ISSUES,
  },
  ...PLANNED_RULES,
];

// ---------------------------------------------------------------------------
// Fix capability entries.
//
// remove_orphan_meta + fix_duplicate_guid were promoted from planned to
// implemented in M24 Plan 2 / T24.2 — the offline_integrity rule (the offline
// scanIntegrityOffline scanner) now emits orphan_meta / duplicate_guid, so the
// fixes have an emitting rule + issue-code linkage. M25 Plan 1 added the
// project_health rule as a second producer of those codes (the live Editor
// counterpart). reassign_missing_texture / reassign_missing_shader now have an
// emitting rule (the M25 Plan 1 `materials` rule emits missing_texture /
// missing_shader); the fix providers themselves ship in M25 Plan 2.
// ---------------------------------------------------------------------------

const PLANNED_FIXES: FixCapability[] = [
  {
    id: "remove_orphan_meta",
    implemented: true,
    status: "implemented",
    rules: ["offline_integrity", "project_health"],
    issueCodes: ["orphan_meta"],
    // Deletes an orphaned .meta file (no companion asset). No asset data is
    // lost — the .meta is already detached. Safe to auto-suggest.
    safe: true,
  },
  {
    id: "fix_duplicate_guid",
    implemented: true,
    status: "implemented",
    rules: ["offline_integrity", "project_health"],
    issueCodes: ["duplicate_guid"],
    // Regenerating a GUID silently rewires the asset graph; never auto-applied
    // under enforce. Apply via apply_fix with the target path picked
    // deliberately (the less-referenced asset is usually the right one to
    // re-GUID, but that judgement is the operator's).
    safe: false,
  },
  {
    id: "reassign_missing_texture",
    implemented: false,
    status: "planned",
    rules: ["materials"],
    issueCodes: ["missing_texture"],
    safe: false,
    guidance:
      "Not yet ported — the `materials` rule now emits `missing_texture`, " +
      "but the fix provider ships in M25 Plan 2. Use read_asset on the .mat " +
      "file and execute_csharp to reassign the _MainTex / texture properties " +
      "in the meantime.",
  },
  {
    id: "reassign_missing_shader",
    implemented: false,
    status: "planned",
    rules: ["materials"],
    issueCodes: ["missing_shader"],
    safe: false,
    guidance:
      "Not yet ported — the `materials` rule now emits `missing_shader`, " +
      "but the fix provider ships in M25 Plan 2. Use find_references to " +
      "locate the intended shader and execute_csharp to reassign m_Shader " +
      "on the material in the meantime.",
  },
];

export const FIX_CATALOG: FixCapability[] = [
  {
    id: "remove_missing_script",
    implemented: true,
    status: "implemented",
    rules: ["missing_references"],
    issueCodes: ["missing_script"],
    safe: true,
  },
  {
    id: "relink_broken_guid",
    implemented: true,
    status: "implemented",
    rules: ["missing_references", "dependencies"],
    issueCodes: ["missing_guid", "broken_dependency"],
    // Mutates references and a wrong choice silently rewires the asset graph;
    // never auto-applied under enforce. Apply via apply_fix with target_guid.
    safe: false,
  },
  ...PLANNED_FIXES,
];

export function implementedRules(): RuleCapability[] {
  return RULE_CATALOG.filter((r) => r.implemented);
}

export function plannedRules(): RuleCapability[] {
  return RULE_CATALOG.filter((r) => !r.implemented);
}

export function implementedFixes(): FixCapability[] {
  return FIX_CATALOG.filter((f) => f.implemented);
}
