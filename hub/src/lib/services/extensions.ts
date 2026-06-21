/**
 * M16 Plan 10 → M18 Plan 4 — extension catalog (Hub mirror).
 *
 * Two catalogs live here:
 *
 * 1. `EMBEDDED_DOMAINS` — the Unity domains whose typed tools are
 *    **embedded inside the bridge** (compile-gated by
 *    `UNITY_OPEN_MCP_EXT_<DOMAIN>` versionDefines). The wizard Step 3
 *    uses this catalog to surface "install Unity domain dependency"
 *    toggles. When the Unity package is present, the embedded bridge
 *    tools compile in and register automatically — no separate
 *    install. Built-in Unity module domains (Particle System,
 *    Animation) have no UPM id and render as info-only "always-on"
 *    cards.
 *
 * 2. `EXTENSION_PACKS` — the legacy extension-pack mirror. The
 *    bridge's in-Editor Extensions tab (`ExtensionCatalog.cs`) still
 *    surfaces these for community / planned packs, so the mirror
 *    stays in sync. The wizard Step 3 no longer consumes this
 *    catalog for shipped domains (M18 Plan 4) — only
 *    `EMBEDDED_DOMAINS` drives the new Unity-dep install flow.
 *
 * Pure data — no runtime deps. Run with:
 *   node --test --experimental-strip-types --no-warnings src/lib/services/extensions.test.ts
 */

// ---------------------------------------------------------------------------
// Embedded domain catalog (M18 Plan 4 — wizard Unity-domain-dep toggles).
// ---------------------------------------------------------------------------

/**
 * A Unity domain whose typed tools are bundled inside the bridge.
 * The wizard surfaces one toggle per `upmDependency` (when non-empty);
 * domains with `builtin: true` render as info-only cards because the
 * Unity module ships with the Editor and there is nothing to install.
 */
export interface EmbeddedDomain {
  /** Tool-group id and define-stem (lowercase, matches M18 Plan 2). */
  domain: string;
  /** User-facing label. */
  displayName: string;
  /** One-line summary. */
  description: string;
  /**
   * UPM package id that activates this domain (`com.unity.ai.navigation`,
   * …). Empty string for built-in Unity module domains — those render
   * as "always-on" cards and are not installable from the wizard.
   */
  upmDependency: string;
  /** Default version string written to `Packages/manifest.json` on
   *  opt-in install. Empty for built-in module domains. */
  defaultVersion: string;
  /** `true` when the Unity dependency is a built-in module (no UPM
   *  package, no manifest entry needed). */
  builtin: boolean;
  /** Tool ids the embedded domain contributes (snake_case). */
  toolIds: string[];
}

/**
 * The embedded Unity domain catalog. Mirrors the bridge root asmdef
 * `versionDefines` block (`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
 * — every domain here maps to one `UNITY_OPEN_MCP_EXT_<DOMAIN>` define.
 * Keep both files in sync when a domain ships or its UPM id changes.
 */
export const EMBEDDED_DOMAINS: readonly EmbeddedDomain[] = [
  {
    domain: "navigation",
    displayName: "Navigation (NavMesh)",
    description:
      "NavMeshSurface bake, agent setup, off-mesh links, and navigation modifiers.",
    upmDependency: "com.unity.ai.navigation",
    defaultVersion: "2.0.0",
    builtin: false,
    toolIds: [
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
    ],
  },
  {
    domain: "inputsystem",
    displayName: "Input System",
    description:
      "Input Action asset authoring (maps, actions, bindings, control schemes).",
    upmDependency: "com.unity.inputsystem",
    defaultVersion: "1.7.0",
    builtin: false,
    toolIds: [
      "unity_open_mcp_inputsystem_asset_create",
      "unity_open_mcp_inputsystem_actionmap_add",
      "unity_open_mcp_inputsystem_action_add",
      "unity_open_mcp_inputsystem_binding_add",
      "unity_open_mcp_inputsystem_binding_composite_add",
      "unity_open_mcp_inputsystem_controlscheme_add",
      "unity_open_mcp_inputsystem_get",
    ],
  },
  {
    domain: "probuilder",
    displayName: "ProBuilder",
    description: "In-editor mesh editing: shape creation, extrude, face materials.",
    upmDependency: "com.unity.probuilder",
    defaultVersion: "6.0.9",
    builtin: false,
    toolIds: [
      "unity_open_mcp_probuilder_create_shape",
      "unity_open_mcp_probuilder_get_mesh_info",
      "unity_open_mcp_probuilder_extrude",
      "unity_open_mcp_probuilder_delete_faces",
      "unity_open_mcp_probuilder_set_face_material",
    ],
  },
  {
    domain: "particle_system",
    displayName: "Particle System",
    description: "Particle module discovery and reflective mutation.",
    upmDependency: "",
    defaultVersion: "",
    builtin: true,
    toolIds: [
      "unity_open_mcp_particle_system_get",
      "unity_open_mcp_particle_system_modify",
    ],
  },
  {
    domain: "animation",
    displayName: "Animation",
    description: "AnimationClip curves and AnimatorController state machines.",
    upmDependency: "",
    defaultVersion: "",
    builtin: true,
    toolIds: [
      "unity_open_mcp_animation_create",
      "unity_open_mcp_animation_get_data",
      "unity_open_mcp_animation_modify",
      "unity_open_mcp_animator_create",
      "unity_open_mcp_animator_get_data",
      "unity_open_mcp_animator_modify",
    ],
  },
] as const;

/**
 * Domains the wizard can install a Unity dependency for. Built-in
 * module domains (Particle System, Animation) are filtered out — the
 * wizard renders them as info-only cards instead.
 */
export function installableEmbeddedDomains(): EmbeddedDomain[] {
  return EMBEDDED_DOMAINS.filter((d) => !d.builtin && d.upmDependency.length > 0);
}

/** Built-in Unity module domains (always-on; no install action). */
export function builtinEmbeddedDomains(): EmbeddedDomain[] {
  return EMBEDDED_DOMAINS.filter((d) => d.builtin);
}

/** Lookup by UPM id (returns undefined when not found). */
export function findEmbeddedDomainByUpmId(upmId: string): EmbeddedDomain | undefined {
  return EMBEDDED_DOMAINS.find((d) => d.upmDependency === upmId);
}

// ---------------------------------------------------------------------------
// Legacy extension-pack catalog (mirror of ExtensionCatalog.cs).
//
// The bridge's in-Editor Extensions tab still renders every entry here
// so users see community / planned packs; the Hub keeps the mirror in
// sync. The wizard Step 3 no longer consumes this catalog for shipped
// domains after M18 Plan 4 — see EMBEDDED_DOMAINS above for the new
// Unity-domain-dep install flow.
// ---------------------------------------------------------------------------

export interface ExtensionPack {
  /** UPM package id ("com.alexeyperov.unity-open-mcp-ext-<domain>"). */
  id: string;
  /** snake_case domain prefix used in tool ids (e.g. "navigation"). */
  domain: string;
  /** User-facing label. */
  displayName: string;
  /** One-line summary. */
  description: string;
  /** Unity-side dependency this pack wraps ("" for built-in modules). */
  upmDependency: string;
  /** Repo-relative path used for `file:` install in dev. */
  localPath: string;
  /** Tool ids the pack contributes (snake_case; empty for planned). */
  toolIds: string[];
  /** Path to the pack's SKILL.md under skills/extensions/. */
  skillPath: string;
  /** true when the pack is implemented (not just planned). */
  shipped: boolean;
}

/**
 * Catalog of legacy / community extension packs. Shipped first-party
 * domains (Nav, Input, ProBuilder, Particles, Animation) are listed
 * with `shipped: true` for parity with the bridge's Extensions tab,
 * but the wizard Step 3 ignores them — embedded tools ship with the
 * bridge now. Community / planned packs remain real UPM packages.
 */
export const EXTENSION_PACKS: readonly ExtensionPack[] = [
  {
    id: "com.alexeyperov.unity-open-mcp-ext-navigation",
    domain: "navigation",
    displayName: "Navigation (NavMesh)",
    description:
      "NavMeshSurface bake, agent setup, off-mesh links, and navigation modifiers.",
    upmDependency: "com.unity.ai.navigation",
    localPath: "packages/extensions/navigation",
    toolIds: [
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
    ],
    skillPath: "skills/extensions/navigation/SKILL.md",
    shipped: true,
  },
  {
    id: "com.alexeyperov.unity-open-mcp-ext-inputsystem",
    domain: "inputsystem",
    displayName: "Input System",
    description:
      "Input Action asset authoring (maps, actions, bindings, control schemes).",
    upmDependency: "com.unity.inputsystem",
    localPath: "packages/extensions/inputsystem",
    toolIds: [
      "unity_open_mcp_inputsystem_asset_create",
      "unity_open_mcp_inputsystem_actionmap_add",
      "unity_open_mcp_inputsystem_action_add",
      "unity_open_mcp_inputsystem_binding_add",
      "unity_open_mcp_inputsystem_binding_composite_add",
      "unity_open_mcp_inputsystem_controlscheme_add",
      "unity_open_mcp_inputsystem_get",
    ],
    skillPath: "skills/extensions/inputsystem/SKILL.md",
    shipped: true,
  },
  {
    id: "com.alexeyperov.unity-open-mcp-ext-probuilder",
    domain: "probuilder",
    displayName: "ProBuilder",
    description: "In-editor mesh editing: shape creation, extrude, face materials.",
    upmDependency: "com.unity.probuilder",
    localPath: "packages/extensions/probuilder",
    toolIds: [
      "unity_open_mcp_probuilder_create_shape",
      "unity_open_mcp_probuilder_get_mesh_info",
      "unity_open_mcp_probuilder_extrude",
      "unity_open_mcp_probuilder_delete_faces",
      "unity_open_mcp_probuilder_set_face_material",
    ],
    skillPath: "skills/extensions/probuilder/SKILL.md",
    shipped: true,
  },
  {
    id: "com.alexeyperov.unity-open-mcp-ext-splines",
    domain: "splines",
    displayName: "Splines",
    description: "Spline container / knot / tangent authoring and evaluation.",
    upmDependency: "com.unity.splines",
    localPath: "packages/extensions/splines",
    toolIds: [],
    skillPath: "skills/extensions/splines/SKILL.md",
    shipped: false,
  },
  {
    id: "com.alexeyperov.unity-open-mcp-ext-terrain",
    domain: "terrain",
    displayName: "Terrain",
    description: "Terrain heightmaps, splatmaps, trees, and neighbor stitching.",
    upmDependency: "",
    localPath: "packages/extensions/terrain",
    toolIds: [],
    skillPath: "skills/extensions/terrain/SKILL.md",
    shipped: false,
  },
  {
    id: "com.alexeyperov.unity-open-mcp-ext-tilemap",
    domain: "tilemap",
    displayName: "Tilemap",
    description: "2D tilemap hierarchy, tile assets, and RuleTile authoring.",
    upmDependency: "com.unity.2d.tilemap",
    localPath: "packages/extensions/tilemap",
    toolIds: [],
    skillPath: "skills/extensions/tilemap/SKILL.md",
    shipped: false,
  },
  {
    id: "com.alexeyperov.unity-open-mcp-ext-particlesystem",
    domain: "particle_system",
    displayName: "Particle System",
    description: "Particle module discovery and reflective mutation.",
    upmDependency: "",
    localPath: "packages/extensions/particlesystem",
    toolIds: [
      "unity_open_mcp_particle_system_get",
      "unity_open_mcp_particle_system_modify",
    ],
    skillPath: "skills/extensions/particlesystem/SKILL.md",
    shipped: true,
  },
  {
    id: "com.alexeyperov.unity-open-mcp-ext-animation",
    domain: "animation",
    displayName: "Animation",
    description: "AnimationClip curves and AnimatorController state machines.",
    upmDependency: "",
    localPath: "packages/extensions/animation",
    toolIds: [
      "unity_open_mcp_animation_create",
      "unity_open_mcp_animation_get_data",
      "unity_open_mcp_animation_modify",
      "unity_open_mcp_animator_create",
      "unity_open_mcp_animator_get_data",
      "unity_open_mcp_animator_modify",
    ],
    skillPath: "skills/extensions/animation/SKILL.md",
    shipped: true,
  },
] as const;

/** Only shipped packs — planned placeholders are filtered out. */
export function shippedPacks(): ExtensionPack[] {
  return EXTENSION_PACKS.filter((p) => p.shipped);
}

/** Lookup by UPM id (returns undefined when not found). */
export function findPack(id: string): ExtensionPack | undefined {
  return EXTENSION_PACKS.find((p) => p.id === id);
}

/**
 * Build the local-package manifest entry for a pack. Mirrors the wizard's
 * `file:` URL derivation for the bridge/verify packages. Retained for
 * contributor / community-pack workflows (see manual-setup.md) — the
 * default wizard flow no longer uses this for shipped domains.
 */
export function localPackageEntry(pack: ExtensionPack): string {
  // Same shape the Rust wizard builds for the bridge: file:../../<path>.
  // The manifest writes the path relative to <project>/Packages.
  return `file:../../${pack.localPath}`;
}
