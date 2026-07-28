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
    // MigrateError::SourceOverlapsPackage carries no `message` — only the
    // two paths (serde `rename_all = "camelCase"` puts them on the wire as
    // `source` / `packagePath`). Without a typed arm the user saw the bare
    // tag `sourceOverlapsPackage` with no hint which folders overlapped or
    // why the migration was refused.
    if (any.type === "sourceOverlapsPackage") {
      const source = typeof any.source === "string" ? any.source : "(unknown)";
      const packagePath =
        typeof any.packagePath === "string" ? any.packagePath : "(unknown)";
      return (
        `migration refused: source folder ${source} overlaps the package at ` +
        `${packagePath} (copying a file onto itself would truncate it to 0 bytes) — ` +
        `choose a source folder outside the package`
      );
    }
    if (typeof any.message === "string" && any.message.length > 0) return any.message;
    // Serde-flattened enum variants sometimes only carry a `type` tag.
    if (typeof any.type === "string") return any.type;
  }
  return String(e);
}
