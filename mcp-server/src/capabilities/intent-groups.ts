// Intent / tag → tool-group recommendation catalog.
//
// Session-ergonomics surface (M31 Plan 2 / T31.2): a small, explicit map from a
// task intent (free text) and/or caller-supplied tags to the tool groups an
// agent most likely needs. Composes over the canonical TOOL_GROUPS catalog in
// `tool-groups.ts` — it never invents groups; every recommended id must exist
// in TOOL_GROUPS.
//
// Exposed via `unity_open_mcp_manage_tools` actions `suggest` (no state change)
// and `activate_for` (activates the recommended set + emits list_changed).
// Those actions are the agent-first path over hand-picking group ids: the
// agent states what it is about to do and the server brings the right groups
// online in one call.
//
// Design rules (pinned by tests):
//  - Unknown / empty intent returns a structured EMPTY recommendation — never a
//    hallucinated group list. The caller is pointed at `list_groups` to browse.
//  - Mutating / verify-related intents additionally recommend `gate-intelligence`
//    so the pre/post-mutation intelligence tools (impact_preview,
//    gate_budget_estimate, mutation_explain) surface before mutations. The group
//    is cheap (3 tools) and the stated problem is that agents under-activate it.
//  - Recommendations are deterministic and explainable: every recommended group
//    carries the tags/keywords that produced it.
//  - Tags and group ids are distinct vocabularies, but common domain names
//    (navigation, animation, audio, terrain, …) appear as both, so passing a
//    group id as a tag works intuitively.

import { GROUP_IDS, TOOL_GROUPS, getGroup } from "./tool-groups.js";

/**
 * One catalog entry: a canonical tag → the group(s) it implies + a short reason
 * shown to the agent. `keywords` are extra free-text tokens that normalize to
 * this tag (lowercase, matched as single tokens or adjacent-token bigrams so
 * "nav mesh" → "navmesh").
 */
export interface IntentTagEntry {
  /** Canonical tag name (lowercase). */
  tag: string;
  /** Group ids this tag implies. Every id must exist in TOOL_GROUPS. */
  groups: string[];
  /** Short reason shown to the agent for this tag. */
  reason: string;
  /** Additional free-text keywords that normalize to this tag. */
  keywords?: string[];
}

/**
 * The intent/tag catalog. Order is preserved in `suggest` output (catalog
 * order) so the recommendation list is stable. Tags overlap with group ids
 * where the natural domain name is shared (navigation, animation, audio, …) so
 * `tags: ["animation"]` works the way an agent would guess.
 */
export const INTENT_TAGS: readonly IntentTagEntry[] = [
  {
    tag: "scene",
    groups: ["typed-editor"],
    reason: "Scene authoring (create/open/save/get_data/set_active).",
    keywords: ["scene", "scenes", "hierarchy"],
  },
  {
    tag: "gameobject",
    groups: ["typed-editor"],
    reason: "GameObject + Transform authoring (create/find/modify/set_parent/duplicate).",
    keywords: ["gameobject", "gameobjects", "object", "objects", "entity", "entities"],
  },
  {
    tag: "prefab",
    groups: ["typed-editor"],
    reason: "Prefab create/instantiate/open/apply/revert/overrides/status.",
    keywords: ["prefab", "prefabs"],
  },
  {
    tag: "component",
    groups: ["typed-editor"],
    reason: "Component add/get/modify/destroy + list_all.",
    keywords: ["component", "components"],
  },
  {
    tag: "material",
    groups: ["typed-editor"],
    reason: "Material properties, keywords, shader swap + material_create.",
    keywords: ["material", "materials", "mat", "mats"],
  },
  {
    tag: "shader",
    groups: ["typed-editor", "shadergraph"],
    reason: "Shader listing/data (typed-editor) + Shader Graph authoring.",
    keywords: ["shader", "shaders", "shadergraph", "shader-graph"],
  },
  {
    tag: "asset",
    groups: ["asset-intelligence", "typed-editor"],
    reason: "Asset read/search/list (asset-intelligence) + folder/copy/move/delete (typed-editor).",
    keywords: ["asset", "assets", "file", "files", "folder", "folders", "directory"],
  },
  {
    tag: "ui",
    groups: ["ui"],
    reason: "uGUI canvas/elements/layout (TextMesh Pro optional).",
    keywords: ["ui", "canvas", "ugui", "image", "button", "slider", "toggle", "inputfield"],
  },
  {
    tag: "navigation",
    groups: ["navigation"],
    reason: "NavMesh surfaces/modifiers/links/agents + bake.",
    keywords: ["navigation", "navmesh", "nav-mesh", "nav", "ai-nav", "ainav", "pathfinding"],
  },
  {
    tag: "input",
    groups: ["input-system"],
    reason: "Input System .inputactions authoring (maps/actions/bindings/composites/schemes).",
    keywords: ["input", "inputsystem", "input-system", "inputactions", "action-map", "bindings"],
  },
  {
    tag: "animation",
    groups: ["animation"],
    reason: "AnimationClip + AnimatorController create/get/modify.",
    keywords: ["animation", "animations", "animate", "animator", "anim", "anims", "animclip", "animationclip"],
  },
  {
    tag: "probuilder",
    groups: ["probuilder"],
    reason: "ProBuilder shapes/mesh info/extrude/faces.",
    keywords: ["probuilder", "pro-builder", "pb", "shape", "extrude"],
  },
  {
    tag: "particles",
    groups: ["particle-system"],
    reason: "ParticleSystem per-module read/modify.",
    keywords: ["particle", "particles", "particlesystem", "particle-system", "fx", "emitter"],
  },
  {
    tag: "vfx",
    groups: ["vfx"],
    reason: "VFX Graph list/open/block edit.",
    keywords: ["vfx", "vfxgraph", "vfx-graph", "visualeffect", "visual-effect"],
  },
  {
    tag: "splines",
    groups: ["splines"],
    reason: "SplineContainer knots/tangents/evaluate/modify.",
    keywords: ["spline", "splines", "splinecontainer", "knot", "knots", "bezier"],
  },
  {
    tag: "lighting",
    groups: ["lighting"],
    reason: "Lights, reflection probes (bake), skybox.",
    keywords: ["light", "lights", "lighting", "reflection-probe", "reflectionprobe", "skybox", "illumination"],
  },
  {
    tag: "audio",
    groups: ["audio"],
    reason: "AudioSource, AudioMixer exposed params, AudioListener.",
    keywords: ["audio", "sound", "sounds", "mixer", "audiomixer", "audiosource", "listener"],
  },
  {
    tag: "constraints",
    groups: ["constraints"],
    reason: "Animation constraints (Position/Rotation/Aim/Parent/Scale) + LODGroup.",
    keywords: ["constraint", "constraints", "lod", "lodgroup", "aim", "ik"],
  },
  {
    tag: "terrain",
    groups: ["terrain"],
    reason: "Terrain create/heights/paint/trees/neighbors.",
    keywords: ["terrain", "heightmap", "splat", "landscape"],
  },
  {
    tag: "cinemachine",
    groups: ["cinemachine"],
    reason: "Cinemachine 3.x virtual cameras + Brain ensure/list.",
    keywords: ["cinemachine", "cm", "virtual-camera", "vcam"],
  },
  {
    tag: "timeline",
    groups: ["timeline"],
    reason: "Timeline assets/tracks/clips/bindings/modify.",
    keywords: ["timeline", "track", "tracks", "clip", "clips", "playable", "playabledirector"],
  },
  {
    tag: "tilemap",
    groups: ["tilemap"],
    reason: "Grid/Tilemap/Tile/RuleTile authoring.",
    keywords: ["tilemap", "tile", "tiles", "ruletile", "grid", "2d-grid"],
  },
  {
    tag: "sprite2d",
    groups: ["sprite2d"],
    reason: "SpriteAtlas packing + texture import/reimport.",
    keywords: ["sprite", "sprites", "spriteatlas", "atlas", "texture", "textures", "import", "texture-importer", "2d"],
  },
  {
    tag: "build",
    groups: ["build-settings"],
    reason: "Build pipeline + player/quality/physics/lighting/time settings + prefs.",
    keywords: ["build", "builds", "building", "buildtarget", "player-settings", "projectsettings", "defines", "quality"],
  },
  {
    tag: "verify",
    groups: ["gate-and-verify"],
    reason: "Validate/scan/baseline/regression/dependencies/find_references + apply_fix.",
    keywords: ["verify", "validate", "validation", "scan", "lint", "regression", "baseline", "references", "dependencies", "fix", "fixes"],
  },
  {
    tag: "risk",
    groups: ["gate-intelligence", "gate-and-verify"],
    reason: "Impact preview + gate budget estimate + mutation explain (plus the verify surface).",
    keywords: ["risk", "risky", "unsafe", "safe", "safety", "impact", "budget", "mutation-explain", "explain-mutation"],
  },
  {
    tag: "screenshot",
    groups: ["agent-senses"],
    reason: "Screenshot / inline capture / window capture.",
    keywords: ["screenshot", "screenshots", "capture", "snapshot", "frame-debugger", "frame debugger"],
  },
  {
    tag: "profiler",
    groups: ["diagnostics", "agent-senses"],
    reason: "Profiler session controls (diagnostics) + per-frame captures (agent-senses).",
    keywords: ["profiler", "profiling", "performance", "perf", "cpu", "gpu", "frame-time", "memory"],
  },
  {
    tag: "qa",
    groups: ["agent-senses"],
    reason: "run_tests + console read + spatial_query.",
    keywords: ["qa", "test", "tests", "testing", "test-runner", "playmode", "editmode", "console", "logs", "log"],
  },
  {
    tag: "script",
    groups: ["typed-editor"],
    reason: "script_read/write/delete + object_get_data/object_modify.",
    keywords: ["script", "scripts", "csharp", "c#", "cs", "code", "source"],
  },
  {
    tag: "reflection",
    groups: ["core"],
    reason: "find_members + invoke_method + type_schema + execute_csharp.",
    keywords: ["reflection", "reflect", "method", "methods", "invoke", "type-schema", "member", "members", "execute"],
  },
  {
    tag: "package",
    groups: ["typed-editor"],
    reason: "package list/search/add/remove/info/get/check + reimport_package.",
    keywords: ["package", "packages", "upm", "manifest", "npm"],
  },
  {
    tag: "asmdef",
    groups: ["typed-editor"],
    reason: "Assembly Definition create/modify/list/get.",
    keywords: ["asmdef", "assembly", "assembly-definition", "assemblies", "references"],
  },
  {
    tag: "scriptableobject",
    groups: ["typed-editor"],
    reason: "ScriptableObject create + list_assets_of_type.",
    keywords: ["scriptableobject", "scriptable-object", "scriptableobjects", "so", "asset-create"],
  },
  {
    tag: "hub",
    groups: ["unity-hub-control"],
    reason: "Unity Hub editor discovery/install/modules + install path (no bridge needed).",
    keywords: ["hub", "unity-hub", "editor-install", "install-editor", "releases", "install-path"],
  },
  {
    tag: "memoryprofiler",
    groups: ["memoryprofiler"],
    reason: "Memory Profiler snapshot capture (.snap).",
    keywords: ["memoryprofiler", "memory-profiler", "memory-snapshot", "heap", "snap"],
  },
  {
    tag: "tags",
    groups: ["typed-editor"],
    reason: "Tag + layer management (editor_get/add_tags, editor_get/add_layers).",
    keywords: ["tag", "tags", "layer", "layers"],
  },
  {
    tag: "selection",
    groups: ["typed-editor"],
    reason: "Selection get/set + undo/redo + clear history.",
    keywords: ["selection", "select", "undo", "redo", "history"],
  },
  {
    tag: "console",
    groups: ["typed-editor", "agent-senses"],
    reason: "console_log/clear (typed-editor) + read_console + pull_events (agent-senses).",
    keywords: ["console", "log", "logs", "debug-log", "debuglog"],
  },
];

/**
 * Keywords that signal a mutating / verify-related task. When any of these
 * appear in the free-text intent OR a matched tag implies mutation, the
 * recommendation additionally includes `gate-intelligence` (3 cheap tools:
 * impact_preview, gate_budget_estimate, mutation_explain) so the pre/post-
 * mutation intelligence surface is visible before mutations run. The group id
 * must still exist in the catalog — it always does today, but the check keeps
 * the contract honest if the catalog ever drops it.
 */
export const MUTATION_SIGNAL_KEYWORDS: readonly string[] = [
  "mutate", "mutation", "mutating", "mutated",
  "edit", "edits", "editing", "edited",
  "modify", "modifying", "modified", "modifies",
  "change", "changing", "changed", "changes",
  "update", "updating", "updated", "updates",
  "write", "writing", "writes",
  "create", "creates", "creating", "created",
  "delete", "deletes", "deleting", "deleted",
  "remove", "removes", "removing", "removed",
  "destroy", "destroys", "destroying", "destroyed",
  "move", "moves", "moving", "moved",
  "rename", "renaming", "renamed",
  "duplicate", "duplicating", "duplicated",
  "reparent", "reparenting",
  "add", "adds", "adding", "added",
  "set", "sets", "setting",
  "build", "builds", "building",
  "bake", "bakes", "baking", "baked",
  "install", "installing", "installed",
  "paint", "painting", "painted",
  "apply", "applies", "applying", "applied",
  "refactor", "refactoring",
  "migrate", "migrating", "migration",
  "generate", "generating", "generated",
  "verify", "verifying", "verified",
  "validate", "validating", "validated", "validation",
  "scan", "scanning", "scanned",
  "fix", "fixes", "fixing", "fixed",
  "regression", "baseline",
  "risk", "risky", "unsafe",
  "impact",
  "check", "checking", "checked",
  "test", "testing", "tested",
  "qa",
  "clean", "cleanup",
];

const TAG_BY_CANONICAL = new Map(INTENT_TAGS.map((e) => [e.tag, e]));

// Index every known keyword (tag name + declared keywords) → canonical tag.
// Single tokens AND adjacent-token bigrams (joined) are matched, so "nav mesh"
// → bigram "navmesh" → navigation, and "game object" → "gameobject" →
// gameobject. Built once at module load.
const KEYWORD_TO_TAG: Map<string, string> = (() => {
  const m = new Map<string, string>();
  for (const entry of INTENT_TAGS) {
    // The tag name itself is a keyword.
    if (!m.has(entry.tag)) m.set(entry.tag, entry.tag);
    for (const kw of entry.keywords ?? []) {
      const norm = kw.toLowerCase();
      // Prefer the canonical tag form when two entries share a keyword; first
      // write wins so catalog order is the tie-breaker.
      if (!m.has(norm)) m.set(norm, entry.tag);
      // Bigram-join variant: a multi-word keyword like "input-system" also
      // matches the joined form "inputsystem".
      const joined = norm.replace(/[^a-z0-9]/g, "");
      if (joined && joined !== norm && !m.has(joined)) m.set(joined, entry.tag);
    }
  }
  return m;
})();

const MUTATION_SIGNAL_SET: ReadonlySet<string> = new Set(
  MUTATION_SIGNAL_KEYWORDS.map((k) => k.toLowerCase()),
);

// ---------------------------------------------------------------------------
// Pure helpers
// ---------------------------------------------------------------------------

/**
 * Lowercase the text and split into alphanumeric tokens. Non-alphanumeric
 * runs (spaces, punctuation, hyphens, slashes) are separators — so
 * "NavMesh / path-finding" → ["navmesh", "path", "finding"].
 */
export function tokenize(text: string): string[] {
  return text
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter((t) => t.length > 0);
}

/**
 * Extract the catalog signals present in free-text intent. Matches single
 * tokens AND adjacent-token bigrams joined (so "nav mesh" → "navmesh"). Also
 * surfaces mutation-signal keywords separately via {@link extractMutationSignals}.
 * Returns canonical tag names + the raw keywords that matched (for the
 * `intentKeywords` debug field).
 */
export function extractIntentTags(text: string): {
  tags: string[];
  keywords: string[];
} {
  const tokens = tokenize(text);
  const tagSet = new Set<string>();
  const keywordSet = new Set<string>();
  const consider = (kw: string) => {
    const tag = KEYWORD_TO_TAG.get(kw);
    if (tag) {
      tagSet.add(tag);
      keywordSet.add(kw);
    }
  };
  for (const t of tokens) consider(t);
  for (let i = 0; i < tokens.length - 1; i++) {
    consider(tokens[i] + tokens[i + 1]);
  }
  return { tags: Array.from(tagSet), keywords: Array.from(keywordSet) };
}

/** Mutation/verify signal keywords present in the free-text intent. */
export function extractMutationSignals(text: string): string[] {
  const tokens = tokenize(text);
  const out = new Set<string>();
  for (const t of tokens) {
    if (MUTATION_SIGNAL_SET.has(t)) out.add(t);
  }
  // Bigrams too: "set up" → "setup"? Keep it simple — single tokens cover the
  // vast majority of cases; bigram joins rarely produce mutation verbs.
  return Array.from(out);
}

// ---------------------------------------------------------------------------
// Recommendation engine
// ---------------------------------------------------------------------------

export interface RecommendOptions {
  /** Free-text task intent (e.g. "bake a NavMesh for the dungeon scene"). */
  intent?: string;
  /** Explicit caller-supplied tags (canonical names or group ids). */
  tags?: string[];
}

export interface RecommendedGroup {
  /** Group id (guaranteed to exist in TOOL_GROUPS). */
  id: string;
  /** Short reason: joined distinct reasons from the matched tags. */
  reason: string;
  /** Canonical tags that produced this recommendation. */
  matchedTags: string[];
  /** True when this group was added via the mutation/verify signal rule. */
  fromMutationSignal?: boolean;
}

export interface Recommendation {
  /** Recommended groups (deduped, in catalog order). */
  groups: RecommendedGroup[];
  /** Canonical tags that matched at least one group. */
  matchedTags: string[];
  /** Caller-supplied tags that did not match any catalog entry. */
  unmatchedTags: string[];
  /** Raw keywords from the free-text intent that matched a catalog tag. */
  intentKeywords: string[];
  /** Mutation/verify keywords matched in the intent. */
  mutationSignals: string[];
  /** True when `gate-intelligence` was added via the mutation/verify rule. */
  gateIntelligenceAdded: boolean;
  /** True when nothing matched (no tags, no intent keywords). */
  empty: boolean;
  /**
   * Hint shown to the caller when the recommendation is empty: call
   * `list_groups` to browse the full catalog.
   */
  hint: string;
}

const EMPTY_HINT =
  "No groups matched the intent or tags. Call " +
  "manage_tools(action=\"list_groups\") to browse every group with its tool " +
  "roster, then activate the one you need with action=\"activate\".";

/**
 * Compute a group recommendation from an intent and/or tags. Pure: does not
 * touch session state. Unknown tags are reported in `unmatchedTags` (not
 * invented as groups). When the intent or matched tags look mutating /
 * verify-related, `gate-intelligence` is added (if it exists in the catalog).
 *
 * Recommendation order follows TOOL_GROUPS (catalog order) so the list is
 * stable regardless of which tag matched first.
 */
export function recommendGroups(opts: RecommendOptions): Recommendation {
  const intent = typeof opts.intent === "string" ? opts.intent.trim() : "";
  const callerTags = Array.isArray(opts.tags)
    ? opts.tags
        .filter((t): t is string => typeof t === "string")
        .map((t) => t.trim().toLowerCase())
        .filter((t) => t.length > 0)
    : [];

  // 1) Pull tags from the free-text intent.
  const intentTags = intent.length > 0 ? extractIntentTags(intent) : { tags: [], keywords: [] };
  const mutationSignals = intent.length > 0 ? extractMutationSignals(intent) : [];

  // 2) Merge caller-supplied tags. A caller tag matches when it is a canonical
  //    catalog tag OR a known keyword OR a known group id (lenient: group ids
  //    work as tags so "navigation" does the obvious thing).
  const matchedTags = new Set<string>(intentTags.tags);
  const intentKeywords = new Set<string>(intentTags.keywords);
  const unmatchedTags: string[] = [];

  for (const tag of callerTags) {
    const entry = TAG_BY_CANONICAL.get(tag);
    if (entry) {
      matchedTags.add(entry.tag);
      intentKeywords.add(tag);
      continue;
    }
    const keywordTag = KEYWORD_TO_TAG.get(tag);
    if (keywordTag) {
      matchedTags.add(keywordTag);
      intentKeywords.add(tag);
      continue;
    }
    // Lenient: a caller tag that is itself a group id activates that group
    // directly. Synthesize a synthetic tag reason from the group description.
    if (GROUP_IDS.has(tag)) {
      matchedTags.add(tag);
      intentKeywords.add(tag);
      continue;
    }
    unmatchedTags.push(tag);
  }

  // 3) Aggregate per-group reasons + matched tags, in TOOL_GROUPS order.
  //    groupReasons: id → { reasons: Set, tags: Set }
  const byGroup = new Map<string, { reasons: Set<string>; tags: Set<string> }>();
  const ensure = (id: string) => {
    let slot = byGroup.get(id);
    if (!slot) {
      slot = { reasons: new Set<string>(), tags: new Set<string>() };
      byGroup.set(id, slot);
    }
    return slot;
  };

  for (const tag of matchedTags) {
    const entry = TAG_BY_CANONICAL.get(tag);
    if (entry) {
      for (const gid of entry.groups) {
        if (!GROUP_IDS.has(gid)) continue; // defensive — catalog entries are validated at build
        const slot = ensure(gid);
        slot.reasons.add(entry.reason);
        slot.tags.add(entry.tag);
      }
      continue;
    }
    // Synthetic (caller passed a bare group id as a tag).
    if (GROUP_IDS.has(tag)) {
      const g = getGroup(tag);
      const slot = ensure(tag);
      slot.reasons.add(g?.description ?? `Group '${tag}'.`);
      slot.tags.add(tag);
    }
  }

  // 4) Mutation / verify signal → add gate-intelligence (when present).
  const looksMutating =
    mutationSignals.length > 0 ||
    // A matched risk/verify tag also counts.
    matchedTags.has("risk") ||
    matchedTags.has("verify");
  let gateIntelligenceAdded = false;
  if (looksMutating && GROUP_IDS.has("gate-intelligence")) {
    const slot = ensure("gate-intelligence");
    slot.reasons.add(
      "Pre/post-mutation intelligence: impact_preview, gate_budget_estimate, mutation_explain.",
    );
    // We intentionally do NOT add a tag here — gate-intelligence is surfaced
    // by the mutation-signal rule, not by a caller tag. `fromMutationSignal`
    // on the RecommendedGroup records that provenance.
    gateIntelligenceAdded = true;
  }

  // 5) Assemble recommended groups in catalog order.
  const groups: RecommendedGroup[] = [];
  for (const g of TOOL_GROUPS) {
    const slot = byGroup.get(g.id);
    if (!slot) continue;
    groups.push({
      id: g.id,
      reason: Array.from(slot.reasons).join(" "),
      matchedTags: Array.from(slot.tags).sort(),
      ...(g.id === "gate-intelligence" && gateIntelligenceAdded
        ? { fromMutationSignal: !slot.tags.has("risk") }
        : {}),
    });
  }

  const matchedTagList = Array.from(matchedTags).sort();
  const empty = groups.length === 0;

  return {
    groups,
    matchedTags: matchedTagList,
    unmatchedTags: unmatchedTags.sort(),
    intentKeywords: Array.from(intentKeywords).sort(),
    mutationSignals: mutationSignals.sort(),
    gateIntelligenceAdded,
    empty,
    hint: empty ? EMPTY_HINT : "",
  };
}
