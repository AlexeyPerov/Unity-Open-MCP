/**
 * Shared error-message extraction for Tauri `invoke` rejections.
 *
 * Tauri `invoke` rejections arrive as the backend's serialized error enum
 * (e.g. `{ type: "spawnFailed", message: "No such file or directory …" }`),
 * but the value can also be a plain `Error` or a string depending on where
 * the rejection originated. Without this helper a `catch (e)` that does
 * `e instanceof Error ? e.message : String(e)` renders a serde-tagged
 * object as `"[object Object]"`, hiding the actual reason from the user —
 * the primary failure path of several project-settings tabs is a backend
 * `ParseFailed` / `MigrateError` / `LaunchError` enum (A14).
 *
 * Pure: no Svelte or Tauri runtime dependency, so it is unit-testable.
 */
export function describeInvokeError(e: unknown): string {
  if (typeof e === "string") return e;
  if (e instanceof Error) return e.message;
  if (e && typeof e === "object") {
    const any = e as Record<string, unknown>;
    if (typeof any.message === "string" && any.message.length > 0) return any.message;
    // Serde-flattened enum variants sometimes only carry a `type` tag.
    if (typeof any.type === "string") return any.type;
  }
  return String(e);
}
