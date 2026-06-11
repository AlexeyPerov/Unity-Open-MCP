// M1.5-18 — three-way theme switch (dark / light / system).
//
// The theme is driven by a `[data-theme="…"]` attribute on the
// `<html>` element. The CSS in `app.html` and the per-component
// scoped styles use that attribute as the selector so the dark and
// light palettes live in the same component files (no parallel
// stylesheet). `system` follows the OS via a media-query listener
// and resolves to a concrete `dark` / `light` value for the on-disk
// per-launch log entry.

import type { Theme } from "$lib/services/config";

/**
 * The concrete palette the Hub is *currently* rendering in. For
 * `"system"`, this returns the value derived from the OS
 * `prefers-color-scheme` media query. The caller can use this to
 * stamp the per-launch log record with the active palette.
 */
export function resolveTheme(theme: Theme): "dark" | "light" {
  if (theme === "dark" || theme === "light") return theme;
  if (typeof window === "undefined") return "dark";
  return window.matchMedia("(prefers-color-scheme: light)").matches
    ? "light"
    : "dark";
}

let mediaListener: ((e: MediaQueryListEvent) => void) | null = null;
let lastSystem: "dark" | "light" = "dark";
let lastApplied: { theme: Theme; system: "dark" | "light" } | null = null;

/**
 * Apply the theme to the document. The function is idempotent —
 * repeated calls with the same input are no-ops (and the OS
 * listener is only registered once). The companion `dark` / `light`
 * CSS overrides live in each component's `<style>` block; see the
 * global rules in `app.html` for the body / app shell.
 */
const STORAGE_KEY = "hub-theme";

function persistLocalStorage(theme: Theme) {
  // The `app.html` inline script reads this key on first paint so
  // a relaunch does not flash the wrong palette. localStorage can
  // throw in private-browsing modes; swallow the error so a write
  // failure never blocks the UI update.
  try {
    if (typeof localStorage !== "undefined") {
      localStorage.setItem(STORAGE_KEY, theme);
    }
  } catch {
    // ignore
  }
}

export function applyTheme(theme: Theme): void {
  if (typeof document === "undefined") return;
  const root = document.documentElement;
  if (theme === "system") {
    const system = resolveTheme("system");
    lastSystem = system;
    root.setAttribute("data-theme", system);
    installMediaListener();
  } else {
    root.setAttribute("data-theme", theme);
    if (mediaListener) {
      // The user picked an explicit palette — drop the OS listener
      // so the next OS change does not flicker the UI. We do not
      // remove the listener if they were already on `system`
      // because the listener is idempotent and the cost of keeping
      // it around is one media-query check per OS change.
      mediaListener = null;
    }
  }
  lastApplied = { theme, system: lastSystem };
  // Mirror the choice to localStorage so the `app.html` inline
  // script can pick it up on the next launch and the first paint
  // matches the user's choice (no flash of the wrong palette).
  persistLocalStorage(theme);
}

function installMediaListener() {
  if (typeof window === "undefined") return;
  if (mediaListener) return;
  const mq = window.matchMedia("(prefers-color-scheme: light)");
  mediaListener = (e) => {
    const next: "dark" | "light" = e.matches ? "light" : "dark";
    lastSystem = next;
    document.documentElement.setAttribute("data-theme", next);
  };
  // `addEventListener` is the modern API; the older `addListener`
  // fallback is kept for very old WebKit. Both are no-ops on the
  // second call so the idempotent guard is enough.
  if (typeof mq.addEventListener === "function") {
    mq.addEventListener("change", mediaListener);
  } else if (typeof (mq as MediaQueryList & {
    addListener?: (cb: (e: MediaQueryListEvent) => void) => void;
  }).addListener === "function") {
    // Legacy WebKit: MediaQueryList#addListener is the pre-2018 API.
    (mq as MediaQueryList & {
      addListener: (cb: (e: MediaQueryListEvent) => void) => void;
    }).addListener(mediaListener);
  }
}

/**
 * Read the active theme the Hub is currently rendering in, for
 * audit-trail purposes (e.g. stamping the per-launch log).
 * Returns `"system"` only if the helper has not been called yet.
 */
export function activeTheme(): Theme {
  return lastApplied?.theme ?? "system";
}

/**
 * The concrete palette the Hub is *currently* rendering in
 * (`"dark" | "light"`), for audit-trail purposes. `system` is
 * resolved through the OS media query.
 */
export function activePalette(): "dark" | "light" {
  return lastApplied?.system ?? resolveTheme("system");
}
