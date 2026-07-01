using System.Collections.Generic;

namespace UnityOpenMcpVerify
{
    /// <summary>
    /// M25 Plan 3 — explainability taxonomy. Per-issue-class root-cause hint
    /// (a stable machine-readable code) + remediation guidance (clean,
    /// user-visible copy). Keyed by <c>ruleId|issueCode</c>.
    ///
    /// This is the *static* per-class metadata: identical across every instance
    /// of the same issue code, so it lives here rather than being repeated on
    /// each <see cref="VerifyIssue"/>. The per-*instance* evidence (the specific
    /// broken ref / value that fired) rides on <see cref="VerifyIssue.Evidence"/>.
    ///
    /// Root-cause codes are a flat, stable taxonomy an agent can branch on:
    ///   - <c>missing_guid_reference</c> — a PPtr points at a GUID no asset owns.
    ///   - <c>missing_fileid_reference</c> — the GUID resolves but the fileID
    ///     is not a top-level object inside that asset.
    ///   - <c>missing_script_class</c> — a MonoBehaviour's script class is gone.
    ///   - <c>missing_dependency</c> — a forward dependency edge does not resolve.
    ///   - <c>orphaned_meta</c> — a .meta has no companion asset on disk.
    ///   - <c>duplicate_guid</c> — two+ assets share one GUID.
    ///   - <c>structural_complexity</c> — counts/depth exceed a health budget.
    ///   - <c>configuration_mismatch</c> — settings/flags disagree with intent.
    ///   - <c>resource_missing</c> — a referenced shader/texture/clip is missing.
    ///   - <c>build_blocker</c> — the asset cannot compile/load.
    /// </summary>
    public static class IssueExplainability
    {
        public class Entry
        {
            public string RootCause { get; }
            public string Remediation { get; }

            public Entry(string rootCause, string remediation)
            {
                RootCause = rootCause;
                Remediation = remediation;
            }
        }

        private static readonly Dictionary<string, Entry> _table = new Dictionary<string, Entry>
        {
            // -----------------------------------------------------------------
            // missing_references
            // -----------------------------------------------------------------
            ["missing_references|missing_fileid_and_guid"] = new Entry(
                "missing_guid_reference",
                "The serialized reference points at a GUID no asset owns and has no fileID. Find the intended target asset and relink the reference with its GUID (and fileID), or remove the reference."),
            ["missing_references|missing_guid"] = new Entry(
                "missing_guid_reference",
                "The reference's GUID does not resolve to a loadable asset — the target was likely deleted, moved, or never committed. Relink to the correct target GUID via apply_fix (relink_broken_guid) with a deliberately chosen target_guid, or remove the reference."),
            ["missing_references|missing_fileid"] = new Entry(
                "missing_fileid_reference",
                "The GUID resolves but the fileID is not a top-level object in the target asset (the sub-object was removed or the asset was re-exported). Relink to the correct fileID, or repoint the reference."),
            ["missing_references|missing_local_fileid"] = new Entry(
                "missing_fileid_reference",
                "A local (same-file) fileID is referenced but no longer declared in the file. Reconnect the reference to a valid local object, or remove it."),
            ["missing_references|empty_local_ref"] = new Entry(
                "missing_fileid_reference",
                "An empty local fileID reference was serialized. Reconnect it to a valid local object, or remove it."),
            ["missing_references|missing_method"] = new Entry(
                "missing_script_class",
                "A serialized call (e.g. UnityEvent) targets a method that no longer exists on the receiver class. Re-add the method, update the call to the new name, or clear the target."),
            ["missing_references|type_mismatch"] = new Entry(
                "missing_script_class",
                "A serialized type name cannot be resolved to a loaded class (renamed, removed, or in an assembly that no longer compiles). Restore or rename the class so the type resolves."),
            ["missing_references|missing_script"] = new Entry(
                "missing_script_class",
                "A MonoBehaviour's script GUID no longer resolves to a compiled script (the script was deleted, renamed, or its assembly no longer compiles). Remove the missing script component via apply_fix (remove_missing_script), or re-add the correct script."),
            ["missing_references|duplicate_component"] = new Entry(
                "configuration_mismatch",
                "The same component type appears more than once on one GameObject, which Unity does not allow on reload. Remove the duplicate component(s), keeping the one with the intended serialized values."),
            ["missing_references|invalid_layer"] = new Entry(
                "configuration_mismatch",
                "The serialized layer index is out of range (the layer was removed from TagManager). Reassign the GameObject to a valid layer."),

            // -----------------------------------------------------------------
            // scene_prefab_health
            // -----------------------------------------------------------------
            ["scene_prefab_health|broken_reference"] = new Entry(
                "missing_guid_reference",
                "The scene/prefab holds a broken object reference. Identify the referenced object via the issue description, then relink or remove it."),
            ["scene_prefab_health|high_risk_bootstrap"] = new Entry(
                "structural_complexity",
                "A bootstrap scene carries more than half the object budget, making load slow and startup fragile. Split content into additive scenes that load after startup."),
            ["scene_prefab_health|scene_object_count"] = new Entry(
                "structural_complexity",
                "The scene exceeds the configured object budget. Reduce object count, move static content into prefabs, or raise the budget deliberately."),
            ["scene_prefab_health|component_hotspot"] = new Entry(
                "structural_complexity",
                "A single GameObject holds an unusually high component count, which hurts performance and editability. Split responsibilities across child objects."),
            ["scene_prefab_health|inactive_expensive"] = new Entry(
                "structural_complexity",
                "Inactive objects still carry Renderers (and their meshes/materials), wasting memory. Disable or remove the Renderer components, or delete the inactive objects."),
            ["scene_prefab_health|inactive_heavy"] = new Entry(
                "structural_complexity",
                "A large pool of inactive objects bloats the scene file and Hierarchy. Move them into prefabs instantiated on demand, or delete unused ones."),
            ["scene_prefab_health|deep_nesting"] = new Entry(
                "structural_complexity",
                "Prefab nesting exceeds the configured depth; deeply nested prefabs are fragile and slow to edit. Flatten the hierarchy or extract the inner prefab."),
            ["scene_prefab_health|override_explosion"] = new Entry(
                "structural_complexity",
                "The prefab instance has too many overrides, undermining the point of a prefab base. Apply intentional overrides into the base prefab, or unpack the instance."),

            // -----------------------------------------------------------------
            // dependencies
            // -----------------------------------------------------------------
            ["dependencies|broken_dependency"] = new Entry(
                "missing_dependency",
                "A forward dependency edge targets a GUID that does not resolve. Relink the dependency to the correct target GUID via apply_fix (relink_broken_guid), or remove the dependency."),
            ["dependencies|dependency_cycle"] = new Entry(
                "configuration_mismatch",
                "The forward dependency graph contains a cycle. Break the cycle by removing or reversing one edge in the loop."),

            // -----------------------------------------------------------------
            // project_health (live Editor counterpart of offline_integrity)
            // -----------------------------------------------------------------
            ["project_health|orphan_meta"] = new Entry(
                "orphaned_meta",
                "A .meta file has no companion asset on disk — the asset was deleted while its .meta remained. Remove the orphaned .meta via apply_fix (remove_orphan_meta), or restore the missing asset."),
            ["project_health|duplicate_guid"] = new Entry(
                "duplicate_guid",
                "Two or more assets share one GUID, so Unity cannot reliably resolve references to it. Regenerate the GUID on the less-referenced asset via apply_fix (fix_duplicate_guid) — choose the target path deliberately."),
            ["project_health|missing_project_setting"] = new Entry(
                "configuration_mismatch",
                "A required ProjectSettings file or field is missing or empty. Restore the setting (e.g. re-add the file, or set the editor version) via the Project Settings window or settings_set_player."),
            ["project_health|project_empty_folder"] = new Entry(
                "structural_complexity",
                "An empty folder adds clutter without content. Delete it (and its .meta), or add the intended content."),
            ["project_health|project_meta_only_folder"] = new Entry(
                "orphaned_meta",
                "A folder .meta exists but the folder itself is gone. Delete the orphaned .meta, or recreate the folder."),
            ["project_health|project_deep_nesting"] = new Entry(
                "structural_complexity",
                "Folder nesting exceeds the configured depth, making paths long and hard to navigate. Flatten the directory structure."),
            ["project_health|project_large_folder"] = new Entry(
                "structural_complexity",
                "A folder holds more assets than the configured budget, slowing AssetDatabase and obscuring structure. Split it into sub-folders."),
            ["project_health|project_broken_asset"] = new Entry(
                "build_blocker",
                "The asset failed to load in the Editor (corrupted or unimportable). Reimport the asset, or restore it from version control."),
            ["project_health|project_empty_scene"] = new Entry(
                "structural_complexity",
                "The scene has zero root objects and is effectively empty. Populate it, or delete it if unused."),

            // -----------------------------------------------------------------
            // offline_integrity (offline aggregator — same canonical codes as
            // project_health where they overlap)
            // -----------------------------------------------------------------
            ["offline_integrity|orphan_meta"] = new Entry(
                "orphaned_meta",
                "A .meta file has no companion asset on disk. Remove the orphaned .meta via apply_fix (remove_orphan_meta), or restore the missing asset."),
            ["offline_integrity|duplicate_guid"] = new Entry(
                "duplicate_guid",
                "Two or more assets share one GUID. Regenerate the GUID on the less-referenced asset via apply_fix (fix_duplicate_guid) — choose the target path deliberately."),
            ["offline_integrity|missing_reference"] = new Entry(
                "missing_guid_reference",
                "A serialized reference resolves to a GUID no asset owns. Relink to the correct target, or remove the reference."),
            ["offline_integrity|missing_script_reference"] = new Entry(
                "missing_script_class",
                "A MonoBehaviour script GUID does not resolve to a compiled script. Remove the missing script component via apply_fix (remove_missing_script), or re-add the correct script."),

            // -----------------------------------------------------------------
            // asmdef_audit
            // -----------------------------------------------------------------
            ["asmdef_audit|broken_asmdef_reference"] = new Entry(
                "missing_dependency",
                "An asmdef references a GUID or assembly name that does not resolve to a compiled assembly or known asmdef. Correct the reference name/GUID, or create the referenced assembly."),
            ["asmdef_audit|asmdef_missing_name"] = new Entry(
                "configuration_mismatch",
                "The asmdef has no 'name' field, so Unity cannot compile it. Add a name matching the assembly (Unity sets it automatically when the file is renamed correctly)."),
            ["asmdef_audit|malformed_asmdef"] = new Entry(
                "build_blocker",
                "The asmdef JSON failed to parse. Fix the JSON syntax error shown in the issue description."),
            ["asmdef_audit|asmdef_duplicate_name"] = new Entry(
                "duplicate_guid",
                "Two or more asmdef files declare the same assembly name, which prevents compilation. Rename one of the assemblies so each name is unique."),
            ["asmdef_audit|asmdef_circular_reference"] = new Entry(
                "configuration_mismatch",
                "The assembly reference graph contains a cycle. Break the cycle by removing or reversing one reference in the loop."),
            ["asmdef_audit|asmdef_editor_in_runtime"] = new Entry(
                "configuration_mismatch",
                "A runtime assembly references an editor-only assembly. Either mark this assembly editor-only (add the Editor platform), or remove the editor reference."),
            ["asmdef_audit|asmdef_auto_referenced_orphan"] = new Entry(
                "configuration_mismatch",
                "The assembly has autoReferenced=false but no other assembly references it, so it will not compile into the project. Add a reference from the consuming assembly, or set autoReferenced=true."),
            ["asmdef_audit|asmdef_platform_filter_broad"] = new Entry(
                "configuration_mismatch",
                "The assembly compiles for all platforms with no filters. Add include/exclude platform filters if a narrower target is intended."),
            ["asmdef_audit|asmdef_platform_filter_contradict"] = new Entry(
                "configuration_mismatch",
                "The asmdef sets both includePlatforms and excludePlatforms, which is contradictory. Keep one and remove the other."),
            ["asmdef_audit|asmdef_version_define_invalid"] = new Entry(
                "configuration_mismatch",
                "A version define references a package by id, which can silently fail to match. Verify the package id and version expression, or use a different expression."),

            // -----------------------------------------------------------------
            // materials
            // -----------------------------------------------------------------
            ["materials|missing_shader"] = new Entry(
                "resource_missing",
                "The material's shader is null or the error shader — the original shader failed to compile or is missing. Reassign a valid shader via apply_fix (reassign_missing_shader) with a target_shader name or path."),
            ["materials|missing_texture"] = new Entry(
                "resource_missing",
                "A material texture slot is null or a builtin placeholder. Reassign a valid texture via apply_fix (reassign_missing_texture) with a target_texture asset path or GUID."),
            ["materials|builtin_shader"] = new Entry(
                "configuration_mismatch",
                "The material uses a built-in shader. If intentional, ignore; otherwise assign the project's render-pipeline shader."),
            ["materials|builtin_texture"] = new Entry(
                "configuration_mismatch",
                "A material texture slot holds a unity_builtin placeholder. Reassign the intended texture."),
            ["materials|render_queue_override"] = new Entry(
                "configuration_mismatch",
                "The material overrides the shader's render queue, which can break draw ordering. Remove the override unless a specific queue is intended."),
            ["materials|unable_to_load"] = new Entry(
                "build_blocker",
                "The material could not be loaded by the Editor (corrupted or unimportable). Reimport the material, or restore it from version control."),
            ["materials|duplicate_material"] = new Entry(
                "structural_complexity",
                "Two or more materials share an identical content fingerprint. Consolidate them into one shared material."),
            ["materials|unused_material"] = new Entry(
                "structural_complexity",
                "The material is not referenced by any renderer or dependency. Delete it if truly unused."),
            ["materials|variant_parent_invalid"] = new Entry(
                "missing_dependency",
                "A material variant's parent is missing or invalid. Reparent the variant to a valid base material, or convert it to a standalone material."),
            ["materials|variant_deep_chain"] = new Entry(
                "structural_complexity",
                "The material-variant chain exceeds the configured depth. Flatten the chain by applying overrides into an earlier variant."),
            ["materials|variant_heavy_overrides"] = new Entry(
                "structural_complexity",
                "The material variant has too many overrides, undermining the point of a variant base. Apply the overrides into the parent, or reduce them."),
            ["materials|gpu_instancing_off"] = new Entry(
                "configuration_mismatch",
                "GPU instancing is disabled on a material that could benefit from it. Enable GPU instancing in the material if the shader supports it."),
            ["materials|srp_batcher_incompatible"] = new Entry(
                "configuration_mismatch",
                "The material is incompatible with the SRP Batcher (mixed shader or non-batchable properties). Make shared materials use the same shader to allow batching."),
            ["materials|null_material"] = new Entry(
                "resource_missing",
                "A renderer references a null material. Assign a material to the slot."),
            ["materials|null_material_slot"] = new Entry(
                "resource_missing",
                "A renderer has a material slot that is null. Assign a material, or remove the slot."),
            ["materials|builtin_material"] = new Entry(
                "configuration_mismatch",
                "A renderer uses a unity_builtin material. Assign a project material instead."),

            // -----------------------------------------------------------------
            // animation_analysis
            // -----------------------------------------------------------------
            ["animation_analysis|missing_clip"] = new Entry(
                "resource_missing",
                "An animator state has no motion/clip assigned, or its clip is missing. Assign a valid AnimationClip to the state."),
            ["animation_analysis|empty_clip"] = new Entry(
                "configuration_mismatch",
                "An AnimationClip declares no curves, so it animates nothing. Add curves to the clip, or remove it if unused."),
            ["animation_analysis|unreachable_state"] = new Entry(
                "configuration_mismatch",
                "An animator state is unreachable from any entry/default/any-state transition. Add a transition to it, or remove the state."),
            ["animation_analysis|complexity_over_threshold"] = new Entry(
                "structural_complexity",
                "The animator's state count exceeds the configured threshold. Split the controller into sub-state machines or simplify it."),
            ["animation_analysis|anystate_overuse"] = new Entry(
                "structural_complexity",
                "AnyState transition count exceeds the threshold; overusing AnyState hurts performance and obscures flow. Replace some with direct transitions."),
            ["animation_analysis|parameter_mismatch"] = new Entry(
                "configuration_mismatch",
                "A transition references a parameter that is not declared in the controller (renamed or removed). Re-add the parameter, or fix the transition condition."),
            ["animation_analysis|expensive_curves_density"] = new Entry(
                "structural_complexity",
                "The clip's keyframe density exceeds the threshold (too many keys per second). Reduce keyframe density by baking or simplifying the curves."),
            ["animation_analysis|expensive_curves_count"] = new Entry(
                "structural_complexity",
                "The clip's curve count exceeds the threshold. Reduce the number of animated curves."),
            ["animation_analysis|duplicate_clip"] = new Entry(
                "structural_complexity",
                "Two or more clips are byte-identical duplicates. Consolidate them into one shared clip."),

            // -----------------------------------------------------------------
            // shader_analysis
            // -----------------------------------------------------------------
            ["shader_analysis|shader_compile_error"] = new Entry(
                "build_blocker",
                "The shader failed to compile and fell back to the error shader. Fix the compile error (check the Editor console for the shader error), or reassign materials using it."),
            ["shader_analysis|missing_shader_asset"] = new Entry(
                "resource_missing",
                "The shader asset failed to load — it may be corrupted or removed. Restore the shader asset, or reassign materials using it."),
            ["shader_analysis|variant_explosion"] = new Entry(
                "structural_complexity",
                "The shader's estimated variant count (2^keywords × passes) exceeds the threshold. Reduce shader keywords or passes to shrink the variant space."),
            ["shader_analysis|pass_count_exceeded"] = new Entry(
                "structural_complexity",
                "The shader's pass count exceeds the threshold. Reduce the number of passes."),
            ["shader_analysis|fallback_shader"] = new Entry(
                "configuration_mismatch",
                "The shader declares a fallback. Confirm the fallback is intended; remove it if not."),
            ["shader_analysis|expensive_feature_platform"] = new Entry(
                "configuration_mismatch",
                "The shader uses keywords flagged expensive for the active platform profile (mobile). Remove or gate the expensive keywords for that profile."),
            ["shader_analysis|platform_keyword_mismatch"] = new Entry(
                "configuration_mismatch",
                "The shader's render pipeline does not match the active platform profile. Align the shader's pipeline with the target profile."),
            ["shader_analysis|duplicate_keyword_profiles"] = new Entry(
                "structural_complexity",
                "Two or more materials share an identical keyword profile. Consolidate them into one material to reduce variant count."),
        };

        /// <summary>Lookup key: <c>{ruleId}|{issueCode}</c>.</summary>
        public static bool TryGet(string ruleId, string issueCode, out Entry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(ruleId) || string.IsNullOrEmpty(issueCode)) return false;
            return _table.TryGetValue($"{ruleId}|{issueCode}", out entry);
        }
    }
}
