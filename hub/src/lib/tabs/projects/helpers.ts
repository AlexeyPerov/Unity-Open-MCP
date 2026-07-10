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
