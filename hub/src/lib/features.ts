export const AI_SETUP_ENABLED = true;

/**
 * Multi-type project support. When `true`, "Add Project" accepts any
 * folder and classifies it into one of four kinds (Unity / Open-MCP /
 * Package / Custom); the list row and settings popup adapt per kind.
 * When `false`, the hub behaves as before — Unity projects only, the
 * `add_project` command still accepts any folder (the Rust side is
 * always multi-type), but the frontend hides the type chip and the
 * non-Unity settings popups. Kept as a kill-switch in case a
 * per-type popup needs an emergency revert without a Rust rebuild.
 */
export const MULTI_PROJECT_TYPES_ENABLED = true;
