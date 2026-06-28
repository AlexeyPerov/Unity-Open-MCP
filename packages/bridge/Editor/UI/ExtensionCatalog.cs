using System.Collections.Generic;

namespace UnityOpenMcpBridge
{
    /// <summary>
    /// A third-party / community or planned domain extension pack.
    /// </summary>
    /// <remarks>
    /// M18 Plan 5 — this catalog covers ONLY packs that are NOT shipped
    /// embedded in the bridge. The five shipped domains (navigation,
    /// inputsystem, probuilder, particlesystem, animation) live in
    /// <see cref="EmbeddedDomainCatalog"/> now and activate automatically
    /// when their Unity dependency is present. Listing them here again would
    /// double-describe the surface (and would risk duplicate tool
    /// registration if a legacy pack were still installed — see M18 Plan 6).
    /// The entries here are therefore <c>shipped: false</c> planned
    /// placeholders plus any future community pack (third-party
    /// <c>com.*.unity-open-mcp-ext-*</c> UPM packages under
    /// <c>packages/extensions/</c>).
    /// </remarks>
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

    /// <summary>
    /// Catalog of third-party / community and planned domain extension packs.
    /// </summary>
    /// <remarks>
    /// Shipped first-party domains are deliberately ABSENT — see the
    /// <see cref="ExtensionPack"/> class doc. The in-Editor Extensions tab
    /// renders one row per entry here under "Community / planned packs"; the
    /// shipped domains get their own "Optional Unity dependencies" panel
    /// driven by <see cref="EmbeddedDomainCatalog"/>.
    /// </remarks>
    public static class ExtensionCatalog
    {
        // Planned packs — advertised with shipped:false so the window shows
        // "coming soon" rather than hides them. When a planned domain ships
        // as an embedded bridge domain (M18 Plan 7+), it moves into
        // EmbeddedDomainCatalog and is REMOVED from here, not flipped to
        // shipped:true. A real third-party / community pack is added here
        // with shipped:true only when its tools register from an external
        // assembly.
        public static readonly ExtensionPack[] Packs =
        {
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
        };

        /// <summary>
        /// True when the given tool id belongs to a known pack in this
        /// catalog. Used by the window to surface "extension pack not
        /// installed" hints when an agent calls a pack tool that the project
        /// has not opted into. Note: shipped embedded domain tool ids are
        /// NOT in this catalog — they are probed via
        /// <see cref="BridgeToolRegistry"/> / <see cref="EmbeddedDomainCatalog"/>.
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
