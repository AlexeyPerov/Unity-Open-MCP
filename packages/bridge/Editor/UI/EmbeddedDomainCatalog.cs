using System;
using System.Collections.Generic;

namespace UnityOpenMcpBridge
{
    /// <summary>
    /// One Unity domain whose typed tools are embedded inside the bridge.
    /// Mirrors <c>hub/src/lib/services/extensions.ts</c> <c>EMBEDDED_DOMAINS</c>
    /// and the bridge root asmdef <c>versionDefines</c> block — every entry
    /// here maps to one <c>UNITY_OPEN_MCP_EXT_&lt;DOMAIN&gt;</c> define that the
    /// Optional Dependencies panel surfaces as a one-click install / status
    /// row. Keep all three sources in sync when a domain ships, is renamed,
    /// or its Unity package id changes.
    /// </summary>
    public class EmbeddedDomain
    {
        /// <summary>Tool-group id from the canonical MCP catalog
        /// (<c>mcp-server/src/capabilities/tool-groups.ts</c>). Must match
        /// exactly so the bridge-side group reconciliation stays aligned.
        /// Examples: <c>"navigation"</c>, <c>"input-system"</c>.</summary>
        public string Group { get; }
        /// <summary>Snake_case domain stem used in tool ids
        /// (<c>unity_open_mcp_&lt;stem&gt;_*</c>) and in define symbols.</summary>
        public string Domain { get; }
        public string DisplayName { get; }
        public string Description { get; }
        /// <summary>UPM package id that activates this domain
        /// (<c>com.unity.ai.navigation</c>, …). Empty for built-in Unity
        /// module domains — those render as "always-on" rows and have no
        /// install action.</summary>
        public string UpmDependency { get; }
        /// <summary>Assembly-qualified (or simple) type name probed via
        /// <see cref="Type.GetType"/> to decide live-compiled status. Used
        /// for built-in module domains where there is no manifest entry to
        /// check.</summary>
        public string TypeProbe { get; }
        /// <summary><c>true</c> when the Unity dependency is a built-in
        /// module (no UPM package, no manifest entry, always present).</summary>
        public bool Builtin { get; }

        public EmbeddedDomain(
            string group,
            string domain,
            string displayName,
            string description,
            string upmDependency,
            string typeProbe,
            bool builtin)
        {
            Group = group;
            Domain = domain;
            DisplayName = displayName;
            Description = description;
            UpmDependency = upmDependency ?? "";
            TypeProbe = typeProbe ?? "";
            Builtin = builtin;
        }
    }

    /// <summary>
    /// Static catalog of embedded Unity domains, mirroring
    /// <c>hub/src/lib/services/extensions.ts</c> <c>EMBEDDED_DOMAINS</c>.
    /// The Optional Dependencies panel in the bridge window consumes this
    /// to render one install / status row per shipped domain.
    /// </summary>
    public static class EmbeddedDomainCatalog
    {
        public static readonly EmbeddedDomain[] Domains =
        {
            new EmbeddedDomain(
                group: "navigation",
                domain: "navigation",
                displayName: "Navigation (NavMesh)",
                description: "NavMeshSurface bake, agent setup, off-mesh links, and navigation modifiers.",
                upmDependency: "com.unity.ai.navigation",
                typeProbe: "UnityEngine.AI.NavMesh, UnityEngine.AIModule",
                builtin: false),
            new EmbeddedDomain(
                group: "input-system",
                domain: "inputsystem",
                displayName: "Input System",
                description: "Input Action asset authoring (maps, actions, bindings, control schemes).",
                upmDependency: "com.unity.inputsystem",
                typeProbe: "UnityEngine.InputSystem.InputSystem, Unity.InputSystem",
                builtin: false),
            new EmbeddedDomain(
                group: "probuilder",
                domain: "probuilder",
                displayName: "ProBuilder",
                description: "In-editor mesh editing: shape creation, extrude, face materials.",
                upmDependency: "com.unity.probuilder",
                typeProbe: "UnityEngine.ProBuilder.ProBuilderMesh, Unity.ProBuilder",
                builtin: false),
            new EmbeddedDomain(
                group: "particle-system",
                domain: "particle_system",
                displayName: "Particle System",
                description: "Particle module discovery and reflective mutation.",
                upmDependency: "",
                typeProbe: "UnityEngine.ParticleSystem",
                builtin: true),
            new EmbeddedDomain(
                group: "animation",
                domain: "animation",
                displayName: "Animation",
                description: "AnimationClip curves and AnimatorController state machines.",
                upmDependency: "",
                typeProbe: "UnityEngine.Animation",
                builtin: true),
            new EmbeddedDomain(
                group: "splines",
                domain: "splines",
                displayName: "Splines",
                description: "SplineContainer authoring: knots, tangent modes, evaluation.",
                upmDependency: "com.unity.splines",
                typeProbe: "UnityEngine.Splines.SplineContainer, Unity.Splines",
                builtin: false),
            // M20 Plan 6 — Ivan-breadth compile-gated domains. Cinemachine is
            // deliberately ABSENT here: it is reflection-gated (the assembly
            // always compiles), so it is not an installable compile-gated
            // domain and the panel would mislead by suggesting a one-click
            // install. Per-call detection surfaces the install/upgrade error.
            new EmbeddedDomain(
                group: "timeline",
                domain: "timeline",
                displayName: "Timeline",
                description: "TimelineAsset authoring: tracks, clips, PlayableDirector binding.",
                upmDependency: "com.unity.timeline",
                typeProbe: "UnityEngine.Timeline.TimelineAsset, Unity.Timeline",
                builtin: false),
            new EmbeddedDomain(
                group: "tilemap",
                domain: "tilemap",
                displayName: "Tilemap",
                description: "2D Tilemap authoring: Grid, tiles, box fill, RuleTile (extras).",
                upmDependency: "com.unity.2d.tilemap",
                typeProbe: "UnityEngine.Tilemaps.Tilemap, UnityEngine.TilemapModule",
                builtin: false),
        };

        /// <summary>Enumerate the installable (non-builtin) domains.</summary>
        public static IEnumerable<EmbeddedDomain> Installable()
        {
            foreach (var d in Domains)
                if (!d.Builtin && !string.IsNullOrEmpty(d.UpmDependency)) yield return d;
        }

        /// <summary>Enumerate the always-on (built-in module) domains.</summary>
        public static IEnumerable<EmbeddedDomain> Builtin()
        {
            foreach (var d in Domains)
                if (d.Builtin) yield return d;
        }
    }
}
