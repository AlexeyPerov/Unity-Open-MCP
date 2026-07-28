/**
 * Pure helpers extracted from ProjectsTab. These functions have no
 * reactive (Svelte runes) or Tauri dependencies, so they are
 * independently unit-testable.
 */
import type {
  BundleStrategy,
  ProjectEntry,
  ProjectKind,
} from "$lib/services/config";

/**
 * Normalizes a project's `kind` to the four-value union. Legacy
 * entries (added before multi-type support) have no `kind` field on
 * disk and deserialize as `undefined`; they are always Unity
 * projects, matching the Rust default in `schemas::ProjectKind`.
 * When `MULTI_PROJECT_TYPES_ENABLED` is `false` the frontend forces
 * every row to look like Unity so the type chip stays hidden and the
 * launch/AI affordances behave as before.
 */
export function projectKindOf(
  project: ProjectEntry,
  multiTypeEnabled: boolean,
): ProjectKind {
  if (!multiTypeEnabled) return "unity";
  return project.kind ?? "unity";
}

/**
 * Short human label for the type chip in the projects list. Kept
 * compact so the chip fits the existing column width alongside the
 * project name.
 */
export function kindLabel(kind: ProjectKind): string {
  switch (kind) {
    case "unity":
      return "Unity";
    case "package":
      return "Package";
    case "openMcp":
      return "Open-MCP";
    case "custom":
      return "Custom";
  }
}

export type StatusKind =
  | "ok"
  | "warn"
  | "missing"
  | "missingVersion"
  | "missingPath"
  | "stale"
  | "running"
  | "loading"
  | "unknown";

export interface ChipInfo {
  tone: "ok" | "warn" | "missing" | "running" | "stale" | "info" | "muted";
  label: string;
  title: string;
}

export interface RowStatus {
  pathExists: boolean | null;
  hasVersion: boolean;
  running: boolean;
  /** True when the row is tagged as `stale`. Stale rows are kept
   *  visible with a `stale` chip and excluded from launch /
   *  running-Unity actions. */
  stale: boolean;
  chips: ChipInfo[];
  kind: StatusKind;
  launchable: boolean;
}

interface StatusForInput {
  project: ProjectEntry;
  pathExists: boolean | null | undefined;
  running: boolean;
  kind: ProjectKind;
}

/**
 * Computes the {@link RowStatus} (chips, kind, launchable) for a
 * project row. Mirrors the inline `statusFor` that lived in the
 * orchestrator; extracted verbatim so the chip logic is testable.
 */
export function statusFor(input: StatusForInput): RowStatus {
  const { project, kind } = input;
  const exists = input.pathExists;
  const hasVersion = !!project.unityVersion && project.unityVersion.length > 0;
  const running = input.running;
  const stale = !!project.stale;

  if (exists === undefined) {
    return {
      pathExists: null,
      hasVersion,
      running,
      stale,
      chips: [{ tone: "muted", label: "checking…", title: "Checking path" }],
      kind: "loading",
      launchable: false,
    };
  }

  // Multi-type: non-Unity projects (Package / Open-MCP / Custom) are
  // not launchable and never carry a Unity version, so the
  // "version missing" / "launchable" chips would just be noise. Show
  // a single "ok" chip when the path exists, or the standard
  // missing-path chip otherwise. Stale still surfaces separately so
  // the user can clean up the entry.
  if (kind !== "unity") {
    if (!exists) {
      const chips: ChipInfo[] = [
        { tone: "missing", label: "missing path", title: project.path },
      ];
      if (stale) {
        chips.push({
          tone: "stale",
          label: "stale",
          title: "Marked stale — keep the entry but exclude from launch",
        });
      }
      return {
        pathExists: false,
        // `hasVersion: true` keeps non-Unity entries out of the
        // "Missing version" filter — they never carry a Unity version
        // by design, so the filter (which targets Unity projects with
        // an unreadable ProjectVersion.txt) must not pick them up.
        hasVersion: true,
        running: false,
        stale,
        chips,
        kind: "missingPath",
        launchable: false,
      };
    }
    const chips: ChipInfo[] = [
      { tone: "ok", label: "ok", title: "Folder tracked" },
    ];
    if (stale) {
      chips.push({
        tone: "stale",
        label: "stale",
        title: "Marked stale — keep the entry but exclude from launch",
      });
    }
    return {
      pathExists: true,
      hasVersion: true,
      running: false,
      stale,
      chips,
      kind: "ok",
      launchable: false,
    };
  }

  // Stale rows are kept visible but never launchable. A stale row
  // whose path also went missing shows both chips so the user can
  // decide whether to relink or to keep the entry around for
  // record-keeping.
  if (!exists) {
    const chips: ChipInfo[] = [
      { tone: "missing", label: "missing path", title: project.path },
    ];
    if (stale) {
      chips.push({
        tone: "stale",
        label: "stale",
        title: "Marked stale — keep the entry but exclude from launch",
      });
    }
    return {
      pathExists: false,
      hasVersion,
      running: false,
      stale,
      chips,
      kind: "missingPath",
      launchable: false,
    };
  }

  if (stale) {
    return {
      pathExists: true,
      hasVersion,
      running: false,
      stale,
      chips: [
        {
          tone: "stale",
          label: "stale",
          title: "Marked stale — relink to a Unity project root to clear",
        },
        { tone: "info", label: "launchable", title: "Project will try to launch" },
      ],
      kind: "stale",
      launchable: false,
    };
  }

  if (!hasVersion) {
    return {
      pathExists: true,
      hasVersion: false,
      running,
      stale,
      chips: [
        { tone: "warn", label: "version missing", title: "No Unity version detected" },
        { tone: "info", label: "launchable", title: "Project will try to launch" },
      ],
      kind: "missingVersion",
      launchable: false,
    };
  }

  const baseChips: ChipInfo[] = [
    { tone: "ok", label: "ok", title: "Detected" },
    { tone: "info", label: "launchable", title: "Ready to launch" },
  ];
  if (running) {
    baseChips.push({
      tone: "running",
      label: "running",
      title: "Unity is currently running for this project",
    });
  }
  return {
    pathExists: true,
    hasVersion: true,
    running,
    stale,
    chips: baseChips,
    kind: running ? "running" : "ok",
    launchable: true,
  };
}

/**
 * Parse a Unity editor version string into its numeric tuple. Unity writes
 * versions as `<major>.<minor>.<patch>[<kind>[<build>]]` where `<kind>` is a
 * single letter (`a` alpha, `b` beta, `f` final, `c` China, `p` patch) and
 * `<build>` is an integer. The patch segment ships well above 9
 * (`2022.3.48f1`, `6000.0.10f1`), so a lexicographic comparison mis-sorts
 * them. Returns `null` for a string that does not match the documented shape.
 *
 * This is a TS port of the Rust `unity_version::UnityVersion::parse`
 * (`hub/src-tauri/src/config/unity_version.rs`) — both sides must agree so
 * the frontend filter and the Rust sort order the same set of installed
 * versions identically. See H14 in the round-2 review.
 */
export interface ParsedUnityVersion {
  major: number;
  minor: number;
  patch: number;
  /** Release-stream letter (`a`, `b`, `f`, `c`, `p`) when present. */
  kind: string | null;
  /** Trailing build number (`1` in `6000.0.1f1`). */
  build: number | null;
}

// `[kind][build]` tail attached to the patch segment (`48f1`, `10b2`, `0`,
// `48f1exrLocal`). Stops at the first non-digit after the kind letter so a
// local-build suffix does not break the parse (mirrors the Rust parser).
const PATCH_TAIL = /^([a-zA-Z]?)(\d*)/;

export function parseUnityVersion(input: string): ParsedUnityVersion | null {
  const trimmed = (input ?? "").trim();
  if (!trimmed) return null;
  const parts = trimmed.split(".");
  if (parts.length < 3) return null;
  const major = Number(parts[0]);
  const minor = Number(parts[1]);
  // The third segment may carry the kind letter + build number attached to
  // the patch (`48f1`) or be a bare number (`48`).
  const third = parts[2];
  const digitRun = third.match(/^\d*/)?.[0] ?? "";
  if (digitRun.length === 0) return null;
  const patch = Number(digitRun);
  if (!Number.isInteger(major) || !Number.isInteger(minor) || !Number.isInteger(patch)) {
    return null;
  }
  const tail = third.slice(digitRun.length);
  const tailMatch = tail.match(PATCH_TAIL);
  const kind = tailMatch && tailMatch[1] ? tailMatch[1] : null;
  const buildRaw = tailMatch && tailMatch[2] ? tailMatch[2] : "";
  const build = buildRaw.length > 0 ? Number(buildRaw) : null;
  if (build !== null && !Number.isInteger(build)) return null;
  return { major, minor, patch, kind, build };
}

/**
 * Compare two Unity version strings by their parsed numeric tuples. Order:
 * higher major wins; ties break on minor, then patch, then the kind letter's
 * ASCII codepoint (so `f` > `c` > `b` > `a`, matching Unity's release-stream
 * ordering and the Rust `Ord` impl which uses `kind as u32`), then the build
 * number. Returns >0 if `a` is newer, <0 if `b` is newer, 0 on tie. Falls
 * back to a plain lexicographic compare when either side fails to parse, so
 * malformed values still order deterministically rather than being dropped.
 *
 * Ported from `unity_version::compare_versions` — keep the two in sync.
 */
export function compareUnityVersions(a: string, b: string): number {
  const pa = parseUnityVersion(a);
  const pb = parseUnityVersion(b);
  if (!pa || !pb) {
    return a < b ? -1 : a > b ? 1 : 0;
  }
  // Major first, then minor, then patch.
  if (pa.major !== pb.major) return pa.major - pb.major;
  if (pa.minor !== pb.minor) return pa.minor - pb.minor;
  if (pa.patch !== pb.patch) return pa.patch - pb.patch;
  // Kind letter by ASCII codepoint; an absent kind ranks below any present
  // letter (a bare patch like `0` is an unfinished parse on the other side,
  // so the well-formed side is treated as greater), matching the Rust impl.
  const ka = pa.kind ? pa.kind.charCodeAt(0) : 0;
  const kb = pb.kind ? pb.kind.charCodeAt(0) : 0;
  if (ka !== kb) return ka - kb;
  const ba = pa.build ?? 0;
  const bb = pb.build ?? 0;
  return ba - bb;
}

/**
 * True when `candidate` is strictly higher than `current` using the parsed
 * numeric tuple comparison. Replaces the lexicographic `candidate > current`
 * comparisons that mis-sorted patch numbers >= 10 (H14). Ported from
 * `unity_version::version_is_higher`.
 */
export function unityVersionIsHigher(candidate: string, current: string): boolean {
  return candidate !== current && compareUnityVersions(candidate, current) > 0;
}

/** Bytes → human-readable size string (B / KB / MB / GB). */
export function formatSize(bytes: number): string {
  if (bytes === 0) return "—";
  const units = ["B", "KB", "MB", "GB"];
  let i = 0;
  let size = bytes;
  while (size >= 1024 && i < units.length - 1) {
    size /= 1024;
    i++;
  }
  return `${size.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

/**
 * Compute the preview bundle version for the upgrade modal's radio
 * group. The Rust bump math is mirrored client-side so a pure-CLI
 * user can pick the strategy without round-tripping every keystroke.
 */
export function previewBundleFor(
  current: string,
  strategy: BundleStrategy,
): { previous: string; next: string } {
  const trimmed = (current || "0.0.0").trim();
  if (strategy === "none") return { previous: trimmed, next: trimmed };
  const match = trimmed.match(/^(\d+)\.(\d+)\.(\d+)$/);
  if (!match) return { previous: trimmed, next: trimmed };
  const major = Number(match[1]);
  const minor = Number(match[2]);
  const patch = Number(match[3]);
  if (strategy === "patch") return { previous: trimmed, next: `${major}.${minor}.${patch + 1}` };
  if (strategy === "minor") return { previous: trimmed, next: `${major}.${minor + 1}.0` };
  return { previous: trimmed, next: `${major + 1}.0.0` };
}

/** Regex matching characters that are unsafe in launch args. */
export const UNSAFE_RE = /[\n\r\0`$|&;<>]/;

/** Validate a launch-args string; returns an error message or null. */
export function validateArgs(value: string): string | null {
  const match = value.match(UNSAFE_RE);
  if (match) {
    return `unsafe character "${match[0]}"`;
  }
  return null;
}

export type EnvVarDraft = {
  uid: string;
  key: string;
  value: string;
};

export type EnvVarValidation =
  | { ok: true; map: Record<string, string> }
  | { ok: false; error: string };

/** Validate env-var draft rows; returns a merged map or an error. */
export function isValidEnvVarDraft(rows: EnvVarDraft[]): EnvVarValidation {
  const map: Record<string, string> = {};
  for (const row of rows) {
    const key = row.key.trim();
    if (key === "") {
      return { ok: false, error: "env-var keys cannot be empty" };
    }
    if (key.includes("=")) {
      return { ok: false, error: `env-var key cannot contain '=': ${key}` };
    }
    if (Object.prototype.hasOwnProperty.call(map, key)) {
      return { ok: false, error: `duplicate env-var key: ${key}` };
    }
    map[key] = row.value;
  }
  return { ok: true, map };
}
