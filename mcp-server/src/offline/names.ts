// Shared field-name display helpers.
//
// Extracted from the former monolithic offline.ts (M28-refactoring Plan 3,
// T3.1). displayFieldName normalizes backing-field and propertyPath names so
// the YAML renderer, the prefab-override parser, and the reference-location
// extractor all produce the same display labels. A leaf module — no internal
// imports.

/**
 * Normalize a serialized field name for display: strip the
 * `<Name>k__BackingField` wrapper C# emits for auto-property backing fields,
 * leaving the property name. Used by the field renderer, prefab-override
 * propertyPath, and reference-location extractor.
 */
export function displayFieldName(name: string): string {
  if (name.startsWith("<") && name.endsWith(">k__BackingField")) {
    return name.slice(1, -">k__BackingField".length);
  }
  return name;
}
