/**
 * M4 Plan 3 — wizard Step 3 manifest UI helpers.
 *
 * Pure functions only. The Rust backend (`hub/src-tauri/src/config/wizard.rs`)
 * owns the actual read / merge / write; this module gives the Svelte
 * wizard:
 *
 * - a stable label + tone for each [`ChangeKind`];
 * - a sorted, human-readable diff preview suitable for the
 *   "Preview manifest diff" panel;
 * - a stable summary string for the Done checklist.
 *
 * Run with: `node --test --experimental-strip-types --no-warnings src/lib/services/manifest.test.ts`
 */

import type {
  ChangeKind,
  ManifestError,
  PackageChange,
} from "./config.ts";

/** Short, user-facing label for each change kind. */
export function changeKindLabel(kind: ChangeKind): string {
  switch (kind) {
    case "add":
      return "will add";
    case "upgrade":
      return "will upgrade";
    case "unchanged":
      return "already installed";
  }
}

/** Tone class for the diff preview rows. */
export function changeKindTone(
  kind: ChangeKind
): "ok" | "warn" | "muted" {
  switch (kind) {
    case "add":
      return "ok";
    case "upgrade":
      return "warn";
    case "unchanged":
      return "muted";
  }
}

/** Short, human-readable package id (last segment after the dot). */
export function shortPackageName(id: string): string {
  const idx = id.lastIndexOf(".");
  if (idx === -1) return id;
  return id.slice(idx + 1);
}

/**
 * Render a `PackageChange` as a single line of diff text. Used by
 * the diff preview panel and by the Done checklist.
 */
export function formatChangeLine(change: PackageChange): string {
  const name = shortPackageName(change.id);
  if (change.kind === "add") {
    return `${name}: add ${change.after}`;
  }
  if (change.kind === "unchanged") {
    return `${name}: unchanged (${change.after})`;
  }
  return `${name}: upgrade ${change.before ?? "<missing>"} → ${change.after}`;
}

/** Render the full diff preview body, one change per line. */
export function formatDiffPreview(changes: PackageChange[]): string {
  return changes.map(formatChangeLine).join("\n");
}

/** One-line summary suitable for the Done checklist. */
export function summarizeChanges(changes: PackageChange[]): string {
  const added = changes.filter((c) => c.kind === "add").length;
  const upgraded = changes.filter((c) => c.kind === "upgrade").length;
  const unchanged = changes.filter((c) => c.kind === "unchanged").length;
  const parts: string[] = [];
  if (added > 0) parts.push(`${added} added`);
  if (upgraded > 0) parts.push(`${upgraded} upgraded`);
  if (unchanged > 0) parts.push(`${unchanged} unchanged`);
  return parts.length === 0 ? "no changes" : parts.join(", ");
}

/**
 * Map a Rust `ManifestError` to a user-facing remediation hint.
 * The wizard shows this as the inline error message under the
 * Install button.
 */
export function describeManifestError(err: ManifestError): string {
  switch (err.kind) {
    case "invalidJson":
      return `Cannot parse Packages/manifest.json: ${err.message}. Fix the JSON by hand and re-run.`;
    case "notAUnityProject":
      return `${err.message}`;
    case "upgradeNotConfirmed":
      return `${err.message} (or revert the version pin in Advanced.)`;
    case "writeFailed":
      return `Failed to write manifest: ${err.message}. Check folder permissions.`;
    case "serializeFailed":
      return `Failed to serialize manifest: ${err.message}.`;
    default:
      return `${err.kind}: ${err.message}`;
  }
}
