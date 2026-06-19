/**
 * M16 Plan 10 — extension pack catalog (Hub mirror).
 *
 * Mirrors `packages/bridge/Editor/UI/ExtensionCatalog.cs`. Both files are the
 * source of truth for the optional extension packs the toolkit advertises;
 * keep them in sync when a pack ships or a metadata field changes. The bridge
 * side drives the in-Editor Extensions tab; this module drives the wizard's
 * Step 3 opt-in checkbox group.
 *
 * Pure data — no runtime deps. Run with:
 *   node --test --experimental-strip-types --no-warnings src/lib/services/extensions.test.ts
 */

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

/** Catalog shipped with the toolkit. Bridge mirror is authoritative. */
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
    toolIds: [],
    skillPath: "skills/extensions/particlesystem/SKILL.md",
    shipped: false,
  },
  {
    id: "com.alexeyperov.unity-open-mcp-ext-animation",
    domain: "animation",
    displayName: "Animation",
    description: "AnimationClip curves and AnimatorController state machines.",
    upmDependency: "",
    localPath: "packages/extensions/animation",
    toolIds: [],
    skillPath: "skills/extensions/animation/SKILL.md",
    shipped: false,
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
 * `file:` URL derivation for the bridge/verify packages.
 */
export function localPackageEntry(pack: ExtensionPack): string {
  // Same shape the Rust wizard builds for the bridge: file:../../<path>.
  // The manifest writes the path relative to <project>/Packages.
  return `file:../../${pack.localPath}`;
}
