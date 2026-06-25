/**
 * Validation Suite core package — engine-neutral orchestration.
 *
 * Re-exports the public surface: data contracts, the loader, and state
 * helpers. The package is intentionally framework-free (no Tauri, no
 * Svelte imports) so it is testable with `node --test` and reusable
 * across any future engine profile (idea.md → Multi-engine reuse).
 */

export * from "./types.ts";
export * from "./errors.ts";
export * from "./loader.ts";
export * from "./state.ts";
export * from "./placeholders.ts";
export * from "./patch.ts";
export * from "./actions.ts";
export * from "./export.ts";
