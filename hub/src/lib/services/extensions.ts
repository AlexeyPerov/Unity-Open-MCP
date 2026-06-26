/**
 * M16 Plan 10 → M18 Plan 5 — extension catalog (Hub mirror).
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
 *    cards. This is the catalog for the five shipped first-party
 *    domains (Nav, Input, ProBuilder, Particles, Animation).
 *
 * 2. `EXTENSION_PACKS` — the third-party / community / planned pack
 *    mirror. Shipped first-party domains are deliberately absent here
 *    (they live in `EMBEDDED_DOMAINS`); this catalog advertises only
 *    planned placeholders (Splines, Terrain, Tilemap) and any future
 *    real third-party community pack. Mirrors the bridge's in-Editor
 *    `ExtensionCatalog.cs` (the "Community / planned packs" section).
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
  {
    domain: "splines",
    displayName: "Splines",
    description: "SplineContainer authoring: knots, tangent modes, evaluation.",
    upmDependency: "com.unity.splines",
    defaultVersion: "2.0.0",
    builtin: false,
    toolIds: [
      "unity_open_mcp_splines_container_create",
      "unity_open_mcp_splines_add_knot",
      "unity_open_mcp_splines_set_knot",
      "unity_open_mcp_splines_set_tangent_mode",
      "unity_open_mcp_splines_evaluate",
      "unity_open_mcp_splines_get_knots",
      "unity_open_mcp_splines_modify",
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
// M18 Plan 4 T18.4.2 — Hub read-only install-state surface.
//
// The Hub cannot run `Client.Add` (it is a separate process), so it surfaces
// the installed/missing status of each embedded Unity domain dependency as a
// read-only panel. The Rust `detect_project_state` snapshot already carries
// one entry per installable UPM id; this helper joins it with the static
// `EMBEDDED_DOMAINS` catalog so the panel can render display names + the
// always-on built-in module domains in one pass.
// ---------------------------------------------------------------------------

/** View-model row for the Hub's Unity-domain-deps panel. Joins the static
 *  `EMBEDDED_DOMAINS` catalog with the live install-state snapshot. */
export interface EmbeddedDomainInstallRow {
  /** snake_case domain stem (matches `EmbeddedDomain.domain`). */
  domain: string;
  /** User-facing label. */
  displayName: string;
  /** `true` for built-in Unity module domains (always-on, no install). */
  builtin: boolean;
  /** UPM package id (empty for built-in module domains). */
  upmDependency: string;
  /** `true` when the manifest carries the UPM id. Always `true` for
   *  built-in module domains (the Unity module ships with the Editor). */
  installed: boolean;
  /** Manifest reference string (`2.0.0`, `file:…`, git URL) when
   *  installed; `null` otherwise. */
  reference: string | null;
}

/**
 * Build the Hub's read-only install-state view for every embedded Unity
 * domain. Built-in module domains (Particle System, Animation) render as
 * always-on; installable domains report the install state from the Rust
 * snapshot keyed by UPM id.
 *
 * @param depStates Live install-state entries from `detect_project_state`
 *   (`ProjectState.unityDomainDeps`). May be empty/undefined when the
 *   backend snapshot is stale — installable domains then report missing.
 */
export function buildEmbeddedDomainInstallRows(
  depStates:
    | readonly { id: string; installed: boolean; reference: string | null }[]
    | undefined,
): EmbeddedDomainInstallRow[] {
  const byId = new Map((depStates ?? []).map((d) => [d.id, d] as const));
  return EMBEDDED_DOMAINS.map((d) => {
    if (d.builtin) {
      return {
        domain: d.domain,
        displayName: d.displayName,
        builtin: true,
        upmDependency: "",
        installed: true,
        reference: null,
      };
    }
    const state = byId.get(d.upmDependency);
    return {
      domain: d.domain,
      displayName: d.displayName,
      builtin: false,
      upmDependency: d.upmDependency,
      installed: state?.installed ?? false,
      reference: state?.reference ?? null,
    };
  });
}

// ---------------------------------------------------------------------------
// Legacy extension-pack catalog (mirror of ExtensionCatalog.cs).
//
// M18 Plan 5 — this catalog covers ONLY third-party / community and
// planned packs. The five shipped first-party domains (Nav, Input,
// ProBuilder, Particles, Animation) are deliberately ABSENT: their
// tools are embedded in the bridge and live in EMBEDDED_DOMAINS above.
// Listing them here again would double-describe the surface (and risk
// duplicate tool registration if a legacy pack were still installed —
// see M18 Plan 6). The Hub wizard Step 3 consumes EMBEDDED_DOMAINS for
// the Unity-domain-dep install flow; this catalog is kept only so the
// bridge's in-Editor "Community / planned packs" section + the Hub stay
// in sync on placeholders and any future community pack.
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
 * Catalog of legacy / community / planned extension packs. M18 Plan 5
 * narrowed this to third-party + planned packs only: the shipped
 * first-party domains are embedded in the bridge and tracked in
 * EMBEDDED_DOMAINS, so they are deliberately absent here. Planned
 * placeholders (Terrain, Tilemap) advertise coming-soon domains; Splines
 * graduated out of this list into EMBEDDED_DOMAINS in M18 Plan 7. A real
 * third-party community pack is added here with `shipped: true` only when
 * its tools register from an external assembly. Mirrors
 * `ExtensionCatalog.cs` — keep both in sync.
 */
export const EXTENSION_PACKS: readonly ExtensionPack[] = [
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
 * contributor / community-pack workflows (see development-setup.md) — the
 * default wizard flow no longer uses this for shipped domains.
 */
export function localPackageEntry(pack: ExtensionPack): string {
  // Same shape the Rust wizard builds for the bridge: file:../../<path>.
  // The manifest writes the path relative to <project>/Packages.
  return `file:../../${pack.localPath}`;
}
