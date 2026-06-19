// M16 Plan 10 — extension pack catalog.
//
// Single source of truth for the optional extension packs the toolkit
// advertises. Read by the bridge window's "Extensions" section and (mirrored)
// by the Hub wizard Step 3 opt-in checkbox group. The catalog is intentionally
// hardcoded — packs are few, and a runtime registry would need an out-of-band
// discovery channel that does not exist yet. When a new pack ships, add an
// entry here AND mirror it in hub/src/lib/services/extensions.ts.
//
// Catalog entry shape (stable across consumers):
//
//   id            UPM package id ("com.alexeyperov.unity-open-mcp-ext-<domain>")
//   domain        snake_case domain prefix used in tool ids (e.g. "navigation")
//   displayName   user-facing label
//   description   one-line summary
//   upmDependency the Unity-side dependency this pack wraps (may be empty)
//   localPath     repo-relative path used for `file:` install in dev
//                 ("packages/extensions/<domain>")
//   toolIds       tool ids the pack contributes (snake_case; for discovery)
//   skillPath     path to the pack's SKILL.md under skills/extensions/
//   shipped       true when the pack is implemented (not just planned)

using System.Collections.Generic;

namespace UnityOpenMcpBridge
{
    public class ExtensionPack
    {
        public string Id { get; }
        public string Domain { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string UpmDependency { get; }
        public string LocalPath { get; }
        public string[] ToolIds { get; }
        public string SkillPath { get; }
        public bool Shipped { get; }

        public ExtensionPack(
            string id,
            string domain,
            string displayName,
            string description,
            string upmDependency,
            string localPath,
            string[] toolIds,
            string skillPath,
            bool shipped)
        {
            Id = id;
            Domain = domain;
            DisplayName = displayName;
            Description = description;
            UpmDependency = upmDependency;
            LocalPath = localPath;
            ToolIds = toolIds ?? System.Array.Empty<string>();
            SkillPath = skillPath;
            Shipped = shipped;
        }
    }

    public static class ExtensionCatalog
    {
        public static readonly ExtensionPack[] Packs =
        {
            new ExtensionPack(
                id: "com.alexeyperov.unity-open-mcp-ext-navigation",
                domain: "navigation",
                displayName: "Navigation (NavMesh)",
                description: "NavMeshSurface bake, agent setup, off-mesh links, and navigation modifiers.",
                upmDependency: "com.unity.ai.navigation",
                localPath: "packages/extensions/navigation",
                toolIds: new[]
                {
                    "unity_open_mcp_navigation_surface_add",
                    "unity_open_mcp_navigation_set_bake_settings",
                    "unity_open_mcp_navigation_surface_bake",
                    "unity_open_mcp_navigation_modifier_add",
                    "unity_open_mcp_navigation_modifier_volume_add",
                    "unity_open_mcp_navigation_link_add",
                    "unity_open_mcp_navigation_agent_add",
                    "unity_open_mcp_navigation_agent_set_destination",
                    "unity_open_mcp_navigation_list",
                    "unity_open_mcp_navigation_get",
                    "unity_open_mcp_navigation_modify",
                },
                skillPath: "skills/extensions/navigation/SKILL.md",
                shipped: true),
            // Planned packs — advertised with shipped:false so the window /
            // wizard can show "coming soon" rather than hide them. When a
            // pack lands (T6.6.4+), flip shipped to true and fill the fields.
            new ExtensionPack(
                id: "com.alexeyperov.unity-open-mcp-ext-inputsystem",
                domain: "inputsystem",
                displayName: "Input System",
                description: "Input Action asset authoring (maps, actions, bindings, control schemes).",
                upmDependency: "com.unity.inputsystem",
                localPath: "packages/extensions/inputsystem",
                toolIds: new[]
                {
                    "unity_open_mcp_inputsystem_asset_create",
                    "unity_open_mcp_inputsystem_actionmap_add",
                    "unity_open_mcp_inputsystem_action_add",
                    "unity_open_mcp_inputsystem_binding_add",
                    "unity_open_mcp_inputsystem_binding_composite_add",
                    "unity_open_mcp_inputsystem_controlscheme_add",
                    "unity_open_mcp_inputsystem_get",
                },
                skillPath: "skills/extensions/inputsystem/SKILL.md",
                shipped: true),
            new ExtensionPack(
                id: "com.alexeyperov.unity-open-mcp-ext-probuilder",
                domain: "probuilder",
                displayName: "ProBuilder",
                description: "In-editor mesh editing: shape creation, extrude, face materials.",
                upmDependency: "com.unity.probuilder",
                localPath: "packages/extensions/probuilder",
                toolIds: new[]
                {
                    "unity_open_mcp_probuilder_create_shape",
                    "unity_open_mcp_probuilder_get_mesh_info",
                    "unity_open_mcp_probuilder_extrude",
                    "unity_open_mcp_probuilder_delete_faces",
                    "unity_open_mcp_probuilder_set_face_material",
                },
                skillPath: "skills/extensions/probuilder/SKILL.md",
                shipped: true),
            new ExtensionPack(
                id: "com.alexeyperov.unity-open-mcp-ext-splines",
                domain: "splines",
                displayName: "Splines",
                description: "Spline container / knot / tangent authoring and evaluation.",
                upmDependency: "com.unity.splines",
                localPath: "packages/extensions/splines",
                toolIds: System.Array.Empty<string>(),
                skillPath: "skills/extensions/splines/SKILL.md",
                shipped: false),
            new ExtensionPack(
                id: "com.alexeyperov.unity-open-mcp-ext-terrain",
                domain: "terrain",
                displayName: "Terrain",
                description: "Terrain heightmaps, splatmaps, trees, and neighbor stitching.",
                upmDependency: "",
                localPath: "packages/extensions/terrain",
                toolIds: System.Array.Empty<string>(),
                skillPath: "skills/extensions/terrain/SKILL.md",
                shipped: false),
            new ExtensionPack(
                id: "com.alexeyperov.unity-open-mcp-ext-tilemap",
                domain: "tilemap",
                displayName: "Tilemap",
                description: "2D tilemap hierarchy, tile assets, and RuleTile authoring.",
                upmDependency: "com.unity.2d.tilemap",
                localPath: "packages/extensions/tilemap",
                toolIds: System.Array.Empty<string>(),
                skillPath: "skills/extensions/tilemap/SKILL.md",
                shipped: false),
            new ExtensionPack(
                id: "com.alexeyperov.unity-open-mcp-ext-particlesystem",
                domain: "particle_system",
                displayName: "Particle System",
                description: "Particle module discovery and reflective mutation.",
                upmDependency: "",
                localPath: "packages/extensions/particlesystem",
                toolIds: System.Array.Empty<string>(),
                skillPath: "skills/extensions/particlesystem/SKILL.md",
                shipped: false),
            new ExtensionPack(
                id: "com.alexeyperov.unity-open-mcp-ext-animation",
                domain: "animation",
                displayName: "Animation",
                description: "AnimationClip curves and AnimatorController state machines.",
                upmDependency: "",
                localPath: "packages/extensions/animation",
                toolIds: System.Array.Empty<string>(),
                skillPath: "skills/extensions/animation/SKILL.md",
                shipped: false),
        };

        /// <summary>
        /// True when the bridge assembly that owns the given tool id belongs
        /// to a shipped extension pack. Used by the window to surface
        /// "extension pack not installed" hints when an agent calls a pack
        /// tool that the project has not opted into.
        /// </summary>
        public static bool IsExtensionToolId(string toolName)
        {
            foreach (var pack in Packs)
            {
                if (pack.ToolIds == null) continue;
                foreach (var id in pack.ToolIds)
                {
                    if (id == toolName) return true;
                }
            }
            return false;
        }

        /// <summary>Enumerate only shipped packs (skip planned placeholders).</summary>
        public static IEnumerable<ExtensionPack> ShippedPacks()
        {
            foreach (var pack in Packs)
                if (pack.Shipped) yield return pack;
        }
    }
}
