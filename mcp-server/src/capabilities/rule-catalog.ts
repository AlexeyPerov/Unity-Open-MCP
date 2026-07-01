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
  severity: "Error" | `Warning`;
  /** Fix IDs that can resolve this issue code, if any. */
  fixIds: string[];
  /** True when the issue is only emitted during a full project scan. */
  fullScanOnly?: boolean;
  /**
   * Machine-readable root-cause code (e.g. `missing_guid_reference`,
   * `duplicate_guid`). Stable across releases — agents branch on it. Mirrors
   * the C# `IssueExplainability` taxonomy in
   * `packages/verify/Editor/Core/IssueExplainability.cs`.
   */
  rootCause?: string;
  /**
   * Short, user-visible remediation playbook for this issue class. Clean of
   * internal IDs (no milestone / spec / execution-plan references).
   */
  remediation?: string;
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
    rootCause: "missing_guid_reference",
    remediation:
      "The reference's GUID does not resolve to a loadable asset — the target was likely deleted, moved, or never committed. Relink to the correct target GUID via apply_fix (relink_broken_guid) with a deliberately chosen target_guid, or remove the reference.",
  },
  {
    code: "missing_fileid",
    severity: "Error",
    fixIds: [],
    rootCause: "missing_fileid_reference",
    remediation:
      "The GUID resolves but the fileID is not a top-level object in the target asset (the sub-object was removed or the asset was re-exported). Relink to the correct fileID, or repoint the reference.",
  },
  {
    code: "missing_local_fileid",
    severity: "Warning",
    fixIds: [],
    rootCause: "missing_fileid_reference",
    remediation:
      "A local (same-file) fileID is referenced but no longer declared in the file. Reconnect the reference to a valid local object, or remove it.",
  },
  {
    code: "empty_local_ref",
    severity: "Warning",
    fixIds: [],
    rootCause: "missing_fileid_reference",
    remediation:
      "An empty local fileID reference was serialized. Reconnect it to a valid local object, or remove it.",
  },
  {
    code: "missing_method",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "missing_script_class",
    remediation:
      "A serialized call (e.g. UnityEvent) targets a method that no longer exists on the receiver class. Re-add the method, update the call to the new name, or clear the target.",
  },
  {
    code: "type_mismatch",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "missing_script_class",
    remediation:
      "A serialized type name cannot be resolved to a loaded class (renamed, removed, or in an assembly that no longer compiles). Restore or rename the class so the type resolves.",
  },
  {
    code: "missing_script",
    severity: "Error",
    fixIds: ["remove_missing_script"],
    rootCause: "missing_script_class",
    remediation:
      "A MonoBehaviour's script GUID no longer resolves to a compiled script (the script was deleted, renamed, or its assembly no longer compiles). Remove the missing script component via apply_fix (remove_missing_script), or re-add the correct script.",
  },
  {
    code: "duplicate_component",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "configuration_mismatch",
    remediation:
      "The same component type appears more than once on one GameObject, which Unity does not allow on reload. Remove the duplicate component(s), keeping the one with the intended serialized values.",
  },
  {
    code: "invalid_layer",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "configuration_mismatch",
    remediation:
      "The serialized layer index is out of range (the layer was removed from TagManager). Reassign the GameObject to a valid layer.",
  },
];

const SCENE_PREFAB_HEALTH_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "broken_reference",
    severity: "Error",
    fixIds: [],
    rootCause: "missing_guid_reference",
    remediation:
      "The scene/prefab holds a broken object reference. Identify the referenced object via the issue description, then relink or remove it.",
  },
  {
    code: "high_risk_bootstrap",
    severity: "Warning",
    fixIds: [],
    rootCause: "structural_complexity",
    remediation:
      "A bootstrap scene carries more than half the object budget, making load slow and startup fragile. Split content into additive scenes that load after startup.",
  },
  {
    code: "scene_object_count",
    severity: "Warning",
    fixIds: [],
    rootCause: "structural_complexity",
    remediation:
      "The scene exceeds the configured object budget. Reduce object count, move static content into prefabs, or raise the budget deliberately.",
  },
  {
    code: "component_hotspot",
    severity: "Warning",
    fixIds: [],
    rootCause: "structural_complexity",
    remediation:
      "A single GameObject holds an unusually high component count, which hurts performance and editability. Split responsibilities across child objects.",
  },
  {
    code: "inactive_expensive",
    severity: "Warning",
    fixIds: [],
    rootCause: "structural_complexity",
    remediation:
      "Inactive objects still carry Renderers (and their meshes/materials), wasting memory. Disable or remove the Renderer components, or delete the inactive objects.",
  },
  {
    code: "inactive_heavy",
    severity: "Warning",
    fixIds: [],
    rootCause: "structural_complexity",
    remediation:
      "A large pool of inactive objects bloats the scene file and Hierarchy. Move them into prefabs instantiated on demand, or delete unused ones.",
  },
  {
    code: "deep_nesting",
    severity: "Warning",
    fixIds: [],
    rootCause: "structural_complexity",
    remediation:
      "Prefab nesting exceeds the configured depth; deeply nested prefabs are fragile and slow to edit. Flatten the hierarchy or extract the inner prefab.",
  },
  {
    code: "override_explosion",
    severity: "Warning",
    fixIds: [],
    rootCause: "structural_complexity",
    remediation:
      "The prefab instance has too many overrides, undermining the point of a prefab base. Apply intentional overrides into the base prefab, or unpack the instance.",
  },
];

const DEPENDENCIES_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "broken_dependency",
    severity: "Error",
    fixIds: ["relink_broken_guid"],
    rootCause: "missing_dependency",
    remediation:
      "A forward dependency edge targets a GUID that does not resolve. Relink the dependency to the correct target GUID via apply_fix (relink_broken_guid), or remove the dependency.",
  },
  {
    code: "dependency_cycle",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "The forward dependency graph contains a cycle. Break the cycle by removing or reversing one edge in the loop.",
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
    rootCause: "orphaned_meta",
    remediation:
      "A .meta file has no companion asset on disk. Remove the orphaned .meta via apply_fix (remove_orphan_meta), or restore the missing asset.",
  },
  {
    code: "duplicate_guid",
    severity: "Error",
    fixIds: ["fix_duplicate_guid"],
    fullScanOnly: true,
    rootCause: "duplicate_guid",
    remediation:
      "Two or more assets share one GUID. Regenerate the GUID on the less-referenced asset via apply_fix (fix_duplicate_guid) — choose the target path deliberately.",
  },
  {
    code: "missing_reference",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "missing_guid_reference",
    remediation:
      "A serialized reference resolves to a GUID no asset owns. Relink to the correct target, or remove the reference.",
  },
  {
    code: "missing_script_reference",
    severity: "Error",
    fixIds: ["remove_missing_script"],
    fullScanOnly: true,
    rootCause: "missing_script_class",
    remediation:
      "A MonoBehaviour script GUID does not resolve to a compiled script. Remove the missing script component via apply_fix (remove_missing_script), or re-add the correct script.",
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
    rootCause: "missing_dependency",
    remediation:
      "An asmdef references a GUID or assembly name that does not resolve to a compiled assembly or known asmdef. Correct the reference name/GUID, or create the referenced assembly.",
  },
  {
    code: "asmdef_missing_name",
    severity: "Error",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "The asmdef has no 'name' field, so Unity cannot compile it. Add a name matching the assembly (Unity sets it automatically when the file is renamed correctly).",
  },
  {
    code: "malformed_asmdef",
    severity: "Error",
    fixIds: [],
    rootCause: "build_blocker",
    remediation:
      "The asmdef JSON failed to parse. Fix the JSON syntax error shown in the issue description.",
  },
  {
    code: "asmdef_duplicate_name",
    severity: "Error",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "duplicate_guid",
    remediation:
      "Two or more asmdef files declare the same assembly name, which prevents compilation. Rename one of the assemblies so each name is unique.",
  },
  {
    code: "asmdef_circular_reference",
    severity: "Error",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "configuration_mismatch",
    remediation:
      "The assembly reference graph contains a cycle. Break the cycle by removing or reversing one reference in the loop.",
  },
  {
    code: "asmdef_editor_in_runtime",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "A runtime assembly references an editor-only assembly. Either mark this assembly editor-only (add the Editor platform), or remove the editor reference.",
  },
  {
    code: "asmdef_auto_referenced_orphan",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "configuration_mismatch",
    remediation:
      "The assembly has autoReferenced=false but no other assembly references it, so it will not compile into the project. Add a reference from the consuming assembly, or set autoReferenced=true.",
  },
  {
    code: "asmdef_platform_filter_broad",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "The assembly compiles for all platforms with no filters. Add include/exclude platform filters if a narrower target is intended.",
  },
  {
    code: "asmdef_platform_filter_contradict",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "The asmdef sets both includePlatforms and excludePlatforms, which is contradictory. Keep one and remove the other.",
  },
  {
    code: "asmdef_version_define_invalid",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "A version define references a package by id, which can silently fail to match. Verify the package id and version expression, or use a different expression.",
  },
];

const PROJECT_HEALTH_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "orphan_meta",
    severity: "Warning",
    fixIds: ["remove_orphan_meta"],
    fullScanOnly: true,
    rootCause: "orphaned_meta",
    remediation:
      "A .meta file has no companion asset on disk — the asset was deleted while its .meta remained. Remove the orphaned .meta via apply_fix (remove_orphan_meta), or restore the missing asset.",
  },
  {
    code: "duplicate_guid",
    severity: "Error",
    fixIds: ["fix_duplicate_guid"],
    fullScanOnly: true,
    rootCause: "duplicate_guid",
    remediation:
      "Two or more assets share one GUID, so Unity cannot reliably resolve references to it. Regenerate the GUID on the less-referenced asset via apply_fix (fix_duplicate_guid) — choose the target path deliberately.",
  },
  {
    code: "missing_project_setting",
    severity: "Error",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "configuration_mismatch",
    remediation:
      "A required ProjectSettings file or field is missing or empty. Restore the setting (e.g. re-add the file, or set the editor version) via the Project Settings window or settings_set_player.",
  },
  {
    code: "project_empty_folder",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "structural_complexity",
    remediation:
      "An empty folder adds clutter without content. Delete it (and its .meta), or add the intended content.",
  },
  {
    code: "project_meta_only_folder",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "orphaned_meta",
    remediation:
      "A folder .meta exists but the folder itself is gone. Delete the orphaned .meta, or recreate the folder.",
  },
  {
    code: "project_deep_nesting",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "structural_complexity",
    remediation:
      "Folder nesting exceeds the configured depth, making paths long and hard to navigate. Flatten the directory structure.",
  },
  {
    code: "project_large_folder",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "structural_complexity",
    remediation:
      "A folder holds more assets than the configured budget, slowing AssetDatabase and obscuring structure. Split it into sub-folders.",
  },
  {
    code: "project_broken_asset",
    severity: "Error",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "build_blocker",
    remediation:
      "The asset failed to load in the Editor (corrupted or unimportable). Reimport the asset, or restore it from version control.",
  },
  {
    code: "project_empty_scene",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "structural_complexity",
    remediation:
      "The scene has zero root objects and is effectively empty. Populate it, or delete it if unused.",
  },
];

const MATERIALS_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "missing_shader",
    severity: "Error",
    fixIds: ["reassign_missing_shader"],
    rootCause: "resource_missing",
    remediation:
      "The material's shader is null or the error shader — the original shader failed to compile or is missing. Reassign a valid shader via apply_fix (reassign_missing_shader) with a target_shader name or path.",
  },
  {
    code: "missing_texture",
    severity: "Warning",
    fixIds: ["reassign_missing_texture"],
    rootCause: "resource_missing",
    remediation:
      "A material texture slot is null or a builtin placeholder. Reassign a valid texture via apply_fix (reassign_missing_texture) with a target_texture asset path or GUID.",
  },
  {
    code: "builtin_shader",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "The material uses a built-in shader. If intentional, ignore; otherwise assign the project's render-pipeline shader.",
  },
  {
    code: "builtin_texture",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "A material texture slot holds a unity_builtin placeholder. Reassign the intended texture.",
  },
  {
    code: "render_queue_override",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "The material overrides the shader's render queue, which can break draw ordering. Remove the override unless a specific queue is intended.",
  },
  {
    code: "unable_to_load",
    severity: "Error",
    fixIds: [],
    rootCause: "build_blocker",
    remediation:
      "The material could not be loaded by the Editor (corrupted or unimportable). Reimport the material, or restore it from version control.",
  },
  {
    code: "duplicate_material",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "structural_complexity",
    remediation:
      "Two or more materials share an identical content fingerprint. Consolidate them into one shared material.",
  },
  {
    code: "unused_material",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "structural_complexity",
    remediation:
      "The material is not referenced by any renderer or dependency. Delete it if truly unused.",
  },
  {
    code: "variant_parent_invalid",
    severity: "Error",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "missing_dependency",
    remediation:
      "A material variant's parent is missing or invalid. Reparent the variant to a valid base material, or convert it to a standalone material.",
  },
  {
    code: "variant_deep_chain",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "structural_complexity",
    remediation:
      "The material-variant chain exceeds the configured depth. Flatten the chain by applying overrides into an earlier variant.",
  },
  {
    code: "variant_heavy_overrides",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "structural_complexity",
    remediation:
      "The material variant has too many overrides, undermining the point of a variant base. Apply the overrides into the parent, or reduce them.",
  },
  {
    code: "gpu_instancing_off",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "configuration_mismatch",
    remediation:
      "GPU instancing is disabled on a material that could benefit from it. Enable GPU instancing in the material if the shader supports it.",
  },
  {
    code: "srp_batcher_incompatible",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "configuration_mismatch",
    remediation:
      "The material is incompatible with the SRP Batcher (mixed shader or non-batchable properties). Make shared materials use the same shader to allow batching.",
  },
  {
    code: "null_material",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "resource_missing",
    remediation:
      "A renderer references a null material. Assign a material to the slot.",
  },
  {
    code: "null_material_slot",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "resource_missing",
    remediation:
      "A renderer has a material slot that is null. Assign a material, or remove the slot.",
  },
  {
    code: "builtin_material",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "configuration_mismatch",
    remediation:
      "A renderer uses a unity_builtin material. Assign a project material instead.",
  },
];

const ANIMATION_ANALYSIS_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "missing_clip",
    severity: "Error",
    fixIds: [],
    rootCause: "resource_missing",
    remediation:
      "An animator state has no motion/clip assigned, or its clip is missing. Assign a valid AnimationClip to the state.",
  },
  {
    code: "empty_clip",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "An AnimationClip declares no curves, so it animates nothing. Add curves to the clip, or remove it if unused.",
  },
  {
    code: "unreachable_state",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "An animator state is unreachable from any entry/default/any-state transition. Add a transition to it, or remove the state.",
  },
  {
    code: "complexity_over_threshold",
    severity: "Warning",
    fixIds: [],
    rootCause: "structural_complexity",
    remediation:
      "The animator's state count exceeds the configured threshold. Split the controller into sub-state machines or simplify it.",
  },
  {
    code: "anystate_overuse",
    severity: "Warning",
    fixIds: [],
    rootCause: "structural_complexity",
    remediation:
      "AnyState transition count exceeds the threshold; overusing AnyState hurts performance and obscures flow. Replace some with direct transitions.",
  },
  {
    code: "parameter_mismatch",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "A transition references a parameter that is not declared in the controller (renamed or removed). Re-add the parameter, or fix the transition condition.",
  },
  {
    code: "expensive_curves_density",
    severity: "Warning",
    fixIds: [],
    rootCause: "structural_complexity",
    remediation:
      "The clip's keyframe density exceeds the threshold (too many keys per second). Reduce keyframe density by baking or simplifying the curves.",
  },
  {
    code: "expensive_curves_count",
    severity: "Warning",
    fixIds: [],
    rootCause: "structural_complexity",
    remediation:
      "The clip's curve count exceeds the threshold. Reduce the number of animated curves.",
  },
  {
    code: "duplicate_clip",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "structural_complexity",
    remediation:
      "Two or more clips are byte-identical duplicates. Consolidate them into one shared clip.",
  },
];

const SHADER_ANALYSIS_ISSUES: RuleIssueDescriptor[] = [
  {
    code: "shader_compile_error",
    severity: "Error",
    fixIds: [],
    rootCause: "build_blocker",
    remediation:
      "The shader failed to compile and fell back to the error shader. Fix the compile error (check the Editor console for the shader error), or reassign materials using it.",
  },
  {
    code: "missing_shader_asset",
    severity: "Error",
    fixIds: [],
    rootCause: "resource_missing",
    remediation:
      "The shader asset failed to load — it may be corrupted or removed. Restore the shader asset, or reassign materials using it.",
  },
  {
    code: "variant_explosion",
    severity: "Warning",
    fixIds: [],
    rootCause: "structural_complexity",
    remediation:
      "The shader's estimated variant count (2^keywords × passes) exceeds the threshold. Reduce shader keywords or passes to shrink the variant space.",
  },
  {
    code: "pass_count_exceeded",
    severity: "Warning",
    fixIds: [],
    rootCause: "structural_complexity",
    remediation:
      "The shader's pass count exceeds the threshold. Reduce the number of passes.",
  },
  {
    code: "fallback_shader",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "The shader declares a fallback. Confirm the fallback is intended; remove it if not.",
  },
  {
    code: "expensive_feature_platform",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "The shader uses keywords flagged expensive for the active platform profile (mobile). Remove or gate the expensive keywords for that profile.",
  },
  {
    code: "platform_keyword_mismatch",
    severity: "Warning",
    fixIds: [],
    rootCause: "configuration_mismatch",
    remediation:
      "The shader's render pipeline does not match the active platform profile. Align the shader's pipeline with the target profile.",
  },
  {
    code: "duplicate_keyword_profiles",
    severity: "Warning",
    fixIds: [],
    fullScanOnly: true,
    rootCause: "structural_complexity",
    remediation:
      "Two or more materials share an identical keyword profile. Consolidate them into one material to reduce variant count.",
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
      "malformed JSON, duplicate assembly names, circular references (DFS " +
      "over the name-based reference graph), editor assemblies referenced " +
      "from runtime, orphaned autoReferenced=false assemblies, platform-" +
      "filter breadth/contradictions, and version-define package references.",
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
      "deleted), duplicate GUIDs (two+ assets sharing one GUID), " +
      "ProjectSettings integrity (missing required settings files, no editor " +
      "version), empty / meta-only folders, deep folder nesting, large " +
      "folders, broken assets (failed to load), and empty scenes " +
      "(rootCount == 0). Full-scan only — does not fire on a scoped " +
      "validate_edit. The live counterpart of the offline_integrity aggregator.",
    applicableAssetKinds: ["meta", "project_settings", "scene"],
    implemented: true,
    status: "implemented",
    issues: PROJECT_HEALTH_ISSUES,
  },
  {
    id: "materials",
    title: "Material health",
    description:
      "Load-time material reference and performance analysis: missing shader " +
      "(null or InternalErrorShader — the original shader failed to compile " +
      "or is missing), null / unity_builtin textures per shader property via " +
      "ShaderUtil, builtin shaders, render-queue overrides, and (full-scan " +
      "only) duplicate materials (SHA-256 fingerprint), unused materials, " +
      "material-variant chains (broken parent, deep chain, heavy overrides " +
      "via reflection), GPU instancing off, and SRP-batcher incompatibility.",
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
      "Animator controller and clip analysis via the live AnimatorController " +
      "object: missing motion/clip on a state (missing_clip), empty clips " +
      "(empty_clip — a .anim declaring no curves), unreachable states (BFS " +
      "over the transition graph seeded from entry/default/any-state), " +
      "state-machine complexity over threshold, AnyState transition overuse, " +
      "parameter mismatches (regex-scan of MonoScripts referencing params not " +
      "in the controller), expensive curve density/count (AnimationUtility), " +
      "and (full-scan only) duplicate clips by byte-size match.",
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
      "Shader compile-state and variant analysis: error shaders " +
      "(InternalErrorShader — shader_compile_error), .shader assets that " +
      "fail to load (missing_shader_asset), variant-explosion estimation " +
      "(2^keywords × passes over threshold), pass-count exceeded, fallback " +
      "shaders (raw .shader source parse), render-pipeline / platform-profile " +
      "mismatch, expensive per-platform keywords (mobile blocklist), and " +
      "(full-scan only) duplicate keyword profiles across materials.",
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
// All four remainder fix providers shipped in M25 Plan 2 as real C#
// IFixProvider implementations (registered in the verify package's
// FixProviderRegistry, which apply_fix — bridge-routed — dispatches to):
//   - remove_orphan_meta (safe): deletes a detached .meta. Producers:
//     offline_integrity + project_health (orphan_meta).
//   - fix_duplicate_guid (unsafe): regenerates a colliding GUID. Producers:
//     offline_integrity + project_health (duplicate_guid).
//   - reassign_missing_texture (unsafe, judgment): requires target_texture.
//     Producer: materials (missing_texture).
//   - reassign_missing_shader (unsafe, judgment): requires target_shader.
//     Producer: materials (missing_shader).
// M25 Plan 2 also added safe auto-fix rollback: a non-dry-run apply_fix that
// fails or introduces new errors under enforce is restored to its pre-fix
// state and the response carries a top-level `rollback` block.
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
    implemented: true,
    status: "implemented",
    rules: ["materials"],
    issueCodes: ["missing_texture"],
    // Reassigns a texture to the material's null texture slot(s). A wrong
    // texture silently changes the material's appearance; never auto-applied.
    // Apply via apply_fix with target_texture (asset path or GUID).
    safe: false,
  },
  {
    id: "reassign_missing_shader",
    implemented: true,
    status: "implemented",
    rules: ["materials"],
    issueCodes: ["missing_shader"],
    // Reassigns a shader to a material whose shader is null / the error
    // shader. A wrong shader silently changes rendering; never auto-applied.
    // Apply via apply_fix with target_shader (shader name or asset path).
    safe: false,
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
