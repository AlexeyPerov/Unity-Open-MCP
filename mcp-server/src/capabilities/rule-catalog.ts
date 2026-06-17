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

// ---------------------------------------------------------------------------
// Planned rules — mirror stubs in VerifyGateAdapter.SelectRuleIds
// ---------------------------------------------------------------------------

const PLANNED_RULES: RuleCapability[] = [
  {
    id: "asmdef_audit",
    title: "Assembly definition audit",
    description:
      "Validates assembly definition references, naming, and dependency graph health.",
    applicableAssetKinds: ["asmdef"],
    applicableExtensions: [".cs", ".asmdef"],
    implemented: false,
    status: "planned",
    issues: [],
    guidance:
      "Not yet ported. C#/asmdef files currently pass through the gate without rule coverage. " +
      "Track asset references manually or use find_references on GUID-based deps.",
  },
  {
    id: "materials",
    title: "Material health",
    description:
      "Detects unreferenced materials, missing textures, and broken shader references.",
    applicableAssetKinds: ["material"],
    applicableExtensions: [".mat", ".shader", ".shadergraph"],
    implemented: false,
    status: "planned",
    issues: [],
    guidance:
      "Not yet ported. Use find_references to check material usage and read_asset to inspect shader refs.",
  },
  {
    id: "shader_analysis",
    title: "Shader analysis",
    description:
      "Inspects shader compile state, variant count, and keyword explosion risk.",
    applicableAssetKinds: ["shader"],
    applicableExtensions: [".mat", ".shader", ".shadergraph"],
    implemented: false,
    status: "planned",
    issues: [],
    guidance:
      "Not yet ported. Use read_console to catch shader compile errors after a build.",
  },
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
    id: "animation_analysis",
    title: "Animation analysis",
    description:
      "Detects missing animation curves, broken animator transitions, and unused clips.",
    applicableAssetKinds: ["animation"],
    applicableExtensions: [".controller", ".anim"],
    implemented: false,
    status: "planned",
    issues: [],
    guidance:
      "Not yet ported. Use find_references to trace animator usage and read_asset to inspect clips.",
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
  ...PLANNED_RULES,
];

// ---------------------------------------------------------------------------
// Planned fixes — deferred from M12 T2.4 (no emitting rule yet)
// ---------------------------------------------------------------------------
//
// These targets have no implemented rule today (orphan_meta / duplicate_guid
// are not emitted by any rule; reassign_missing_* wait on the `materials` rule
// which is deferred to M17). They are advertised as planned so agents get a
// structured "not yet available" signal instead of trial-and-error.

const PLANNED_FIXES: FixCapability[] = [
  {
    id: "remove_orphan_meta",
    implemented: false,
    status: "planned",
    rules: [],
    issueCodes: [],
    safe: true,
    guidance:
      "Not yet ported. No rule emits an orphan_meta issue today. " +
      "Find .meta files whose asset was deleted manually via find_members or " +
      "AssetDatabase and delete them with execute_csharp in the meantime.",
  },
  {
    id: "fix_duplicate_guid",
    implemented: false,
    status: "planned",
    rules: [],
    issueCodes: [],
    safe: false,
    guidance:
      "Not yet ported. No rule emits a duplicate_guid issue today. " +
      "Inspect .meta files manually (or via execute_csharp) and regenerate the " +
      "duplicated GUID on the less-referenced asset, then update references.",
  },
  {
    id: "reassign_missing_texture",
    implemented: false,
    status: "planned",
    rules: ["materials"],
    issueCodes: [],
    safe: false,
    guidance:
      "Not yet ported — depends on the `materials` rule (deferred to M17). " +
      "Use read_asset on the .mat file and execute_csharp to reassign the " +
      "_MainTex / texture properties in the meantime.",
  },
  {
    id: "reassign_missing_shader",
    implemented: false,
    status: "planned",
    rules: ["materials"],
    issueCodes: [],
    safe: false,
    guidance:
      "Not yet ported — depends on the `materials` rule (deferred to M17). " +
      "Use find_references to locate the intended shader and execute_csharp " +
      "to reassign m_Shader on the material in the meantime.",
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
