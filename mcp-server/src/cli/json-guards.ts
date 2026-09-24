/** Plain-object check shared by the CLI commands that merge user JSON. */
export function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
