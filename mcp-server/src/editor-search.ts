/** Literal terms only: callers cannot inject Search operators through text/type. */
export function buildEditorSearch(text: string, assetType?: string): string {
  const literal = text.replace(/["\\\r\n]/g, " ").trim();
  if (assetType && !/^[A-Za-z_][A-Za-z0-9_.]*$/.test(assetType)) throw new Error("asset_type must be a Unity type name (letters, digits, underscore or dot).");
  return ["p:", assetType ? `t:${assetType}` : "", literal ? `"${literal}"` : ""].filter(Boolean).join(" ");
}
