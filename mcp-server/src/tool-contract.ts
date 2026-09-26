import { implementedRules } from "./capabilities/rule-catalog.js";
import type { Tool } from "@modelcontextprotocol/sdk/types.js";

type Schema = Record<string, any>;

// Explicit semantic inventory: prose edits must never change a locator contract.
const GAME_OBJECT_PATH_TOOLS = new Set([
  "unity_senses_spatial_query",
  "unity_open_mcp_prefab_create",
  "unity_open_mcp_prefab_apply",
  "unity_open_mcp_prefab_revert",
  "unity_open_mcp_prefab_unpack",
  "unity_open_mcp_prefab_get_overrides",
  "unity_open_mcp_prefab_status",
  "unity_open_mcp_gameobject_destroy",
  "unity_open_mcp_gameobject_duplicate",
  "unity_open_mcp_gameobject_find",
  "unity_open_mcp_gameobject_modify",
  "unity_open_mcp_gameobject_set_parent",
  "unity_open_mcp_component_add",
  "unity_open_mcp_component_destroy",
  "unity_open_mcp_component_get",
  "unity_open_mcp_component_modify",
  "unity_open_mcp_scene_focus",
  "unity_open_mcp_selection_set",
  "unity_open_mcp_navigation_surface_add",
  "unity_open_mcp_navigation_set_bake_settings",
  "unity_open_mcp_navigation_surface_bake",
  "unity_open_mcp_navigation_modifier_add",
  "unity_open_mcp_navigation_modifier_volume_add",
  "unity_open_mcp_navigation_link_add",
  "unity_open_mcp_navigation_agent_add",
  "unity_open_mcp_navigation_agent_set_destination",
  "unity_open_mcp_navigation_get",
  "unity_open_mcp_navigation_modify",
  "unity_open_mcp_probuilder_get_mesh_info",
  "unity_open_mcp_probuilder_extrude",
  "unity_open_mcp_probuilder_delete_faces",
  "unity_open_mcp_probuilder_set_face_material",
  "unity_open_mcp_particle_system_get",
  "unity_open_mcp_particle_system_modify",
  "unity_open_mcp_splines_add_knot",
  "unity_open_mcp_splines_set_knot",
  "unity_open_mcp_splines_set_tangent_mode",
  "unity_open_mcp_splines_evaluate",
  "unity_open_mcp_splines_get_knots",
  "unity_open_mcp_splines_modify",
  "unity_open_mcp_light_add",
  "unity_open_mcp_light_set",
  "unity_open_mcp_light_modify",
  "unity_open_mcp_reflection_probe_bake",
  "unity_open_mcp_reflection_probe_get",
  "unity_open_mcp_audio_source_add",
  "unity_open_mcp_audio_source_modify",
  "unity_open_mcp_ui_canvas_add",
  "unity_open_mcp_ui_layout_group_add",
  "unity_open_mcp_ui_element_modify",
  "unity_open_mcp_constraint_add",
  "unity_open_mcp_lod_group_configure",
  "unity_open_mcp_lod_add_level",
  "unity_open_mcp_terrain_set_heights",
  "unity_open_mcp_terrain_paint_layer",
  "unity_open_mcp_terrain_place_trees",
  "unity_open_mcp_terrain_set_neighbors",
  "unity_open_mcp_cinemachine_set_targets",
  "unity_open_mcp_cinemachine_set_lens",
  "unity_open_mcp_cinemachine_set_body",
  "unity_open_mcp_cinemachine_set_noise",
  "unity_open_mcp_cinemachine_brain_ensure",
  "unity_open_mcp_timeline_director_bind",
  "unity_open_mcp_tilemap_set_tile",
  "unity_open_mcp_tilemap_box_fill",
]);
const ASSET_PATH_TOOLS = new Set([
  "unity_open_mcp_scene_create",
  "unity_open_mcp_scene_open",
  "unity_open_mcp_scene_save",
  "unity_open_mcp_scene_unload",
  "unity_open_mcp_scene_set_active",
  "unity_open_mcp_scene_get_data",
]);

// GameObject host selectors for the "supply only one selector" rule. `name`
// selects by name unless the tool uses it as a payload; such tools name their
// by-name selector here and publish their full set as `x-gameobject-selectors`,
// which the bridge's BatchSchemaValidator reads from the generated schema.
const DEFAULT_GAMEOBJECT_SELECTORS = ["instance_id", "game_object_path", "path", "target_path", "name"];
const NAME_SELECTOR_OVERRIDES: Record<string, string> = {
  // `name` is the new name; the target is resolved by name_target.
  unity_open_mcp_gameobject_modify: "name_target",
};

/** Selector keys the one-selector rule applies to; null when the schema has no GameObject host. */
function gameObjectSelectors(schema: Schema): string[] | null {
  if (Array.isArray(schema["x-gameobject-selectors"])) return schema["x-gameobject-selectors"];
  if (schema["x-project-command"] || !schema.properties?.game_object_path) return null;
  return DEFAULT_GAMEOBJECT_SELECTORS.filter(k => schema.properties[k]);
}

/** Locator names are explicit in the published contract; aliases retain their wire key. */
export function canonicalSchema(name: string, input: Schema): Tool["inputSchema"] {
  const schema = structuredClone(input);
  const props = schema.properties;
  // offline_integrity is a reporting aggregator, not a registered VerifyRunner rule.
  for (const key of ["include_rules", "exclude_rules", ...(name === "unity_open_mcp_scan_paths" ? ["categories"] : [])])
    if (props[key]?.items) props[key].items.enum = implementedRules().filter(r => r.id !== "offline_integrity").map(r => r.id);
  const rename = (s: Schema, old: string, key: string) => {
    if (!s.properties?.[old] || s.properties[key]) return;
    s.properties[key] = { ...s.properties[old], "x-wire-key": old };
    s.properties[old] = { ...s.properties[old], deprecated: true, "x-alias-for": key,
      description: `Deprecated alias; use ${key}.` };
    delete s.properties[old].default;
    if (s.required?.includes(old)) {
      s.required = s.required.filter((k: string) => k !== old);
      s.allOf = [...(s.allOf ?? []), { anyOf: [{ required: [key] }, { required: [old] }] }];
    }
  };
  if (GAME_OBJECT_PATH_TOOLS.has(name)) rename(schema, "path", "game_object_path");
  if (ASSET_PATH_TOOLS.has(name)) rename(schema, "path", "asset_path");
  rename(schema, "prefab_asset_path", "asset_path");
  if (props.prefab_asset_path) props.prefab_path = { type: "string", deprecated: true, "x-alias-for": "asset_path", description: "Deprecated alias; use asset_path." };
  if (props.game_object_path && !props.target_path) props.target_path = { type: "string", deprecated: true, "x-alias-for": "game_object_path", description: "Deprecated alias; use game_object_path." };
  if (/component_(get|modify|add)/.test(name)) rename(schema, "type_name", "component_type");
  if (name.endsWith("component_modify")) rename(props.fields.items, "path", "property_path");
  if (name === "unity_open_mcp_find_references" || name === "unity_open_mcp_dependencies") {
    props.path = { type: "string", deprecated: true, "x-alias-for": "asset_path", description: "Deprecated alias; use asset_path." };
    for (const branch of schema.oneOf ?? []) if (branch.required?.includes("asset_path")) {
      branch.required = branch.required.filter((k: string) => k !== "asset_path");
      branch.anyOf = [{ required: ["asset_path"] }, { required: ["path"] }];
    }
  }
  if (props.prefab_path) for (const clause of schema.allOf ?? []) if (clause.anyOf?.some((b: Schema) => b.required?.includes("asset_path"))) clause.anyOf.push({ required: ["prefab_path"] });
  if (props.game_object_path) {
    const nameSelector = NAME_SELECTOR_OVERRIDES[name] ?? "name";
    if (nameSelector !== "name")
      schema["x-gameobject-selectors"] = DEFAULT_GAMEOBJECT_SELECTORS.map(k => k === "name" ? nameSelector : k).filter(k => props[k]);
    props.game_object_path.description = `GameObject hierarchy path, e.g. Root/Child. Supply only one host selector: instance_id, game_object_path, or ${nameSelector}.`;
  }
  if (props.component_type) props.component_type.description = "Component type (full name preferred); alternative to component_instance_id.";
  return schema as Tool["inputSchema"];
}

/** Validate before dispatch, including nested records; arbitrary value payloads stay opaque. */
export function validateSchema(value: any, schema: Schema, path = "args"): string[] {
  const errors: string[] = [];
  for (const op of ["anyOf", "oneOf", "allOf"]) if (schema[op]) {
    const n = schema[op].filter((s: Schema) => validateSchema(value, s, path).length === 0).length;
    if ((op === "anyOf" && !n) || (op === "oneOf" && n !== 1) || (op === "allOf" && n !== schema[op].length)) errors.push(`${path} must satisfy ${op}`);
  }
  const matches = (t: string) => t === "array" ? Array.isArray(value) : t === "object" ? value !== null && typeof value === "object" && !Array.isArray(value) : t === "integer" ? Number.isInteger(value) : t === "null" ? value === null : typeof value === t;
  if (schema.type && !(Array.isArray(schema.type) ? schema.type : [schema.type]).some(matches)) return [`${path} must be ${schema.type}`];
  if (schema.enum && !schema.enum.includes(value)) errors.push(`${path} must be one of ${schema.enum.join(", ")}`);
  if (value && typeof value === "object" && !Array.isArray(value)) {
    for (const key of schema.required ?? []) if (value[key] === undefined) errors.push(`${path}.${key} is required`);
    const selectorKeys = gameObjectSelectors(schema);
    if (selectorKeys) {
      const supplied = (k: string) => value[k] !== undefined && value[k] !== null && value[k] !== "" && value[k] !== 0 && value[k] !== "0";
      const selectors = selectorKeys.filter(supplied);
      if (selectors.length > 1) errors.push(`${path}: supply only one GameObject selector (prefer game_object_path); ignored keys: ${selectors.join(", ")}`);
      if (supplied("component_instance_id") && ["component_type", "type_name"].some(supplied)) errors.push(`${path}: component_instance_id and component_type are alternatives`);
    }
    if (schema.properties) for (const [key, child] of Object.entries(value)) {
      const prop = Object.prototype.hasOwnProperty.call(schema.properties, key) ? schema.properties[key] : undefined;
      if (!prop && schema.additionalProperties === false) errors.push(`${path}.${key} is unknown; canonical keys: ${Object.keys(schema.properties).filter(k => !schema.properties[k].deprecated).join(", ")}`);
      if (prop) {
        if (prop["x-alias-for"] && Object.keys(value).some(other => other !== key && (other === prop["x-alias-for"] || schema.properties[other]?.["x-alias-for"] === prop["x-alias-for"]))) errors.push(`${path}.${key} conflicts with another selector; use only ${prop["x-alias-for"]}`);
        errors.push(...validateSchema(child, prop, `${path}.${key}`));
      }
    }
  }
  if (Array.isArray(value) && schema.items) value.forEach((v, i) => errors.push(...validateSchema(v, schema.items, `${path}[${i}]`)));
  if (typeof value === "string" && schema.pattern && !new RegExp(schema.pattern).test(value)) errors.push(`${path} does not match ${schema.pattern}`);
  const size = typeof value === "number" ? value : typeof value === "string" || Array.isArray(value) ? value.length : undefined;
  if (size !== undefined) {
    for (const k of ["minimum", "minItems", "minLength"]) if (schema[k] !== undefined && size < schema[k]) errors.push(`${path} violates ${k}`);
    if (typeof value === "number" && schema.exclusiveMinimum !== undefined && value <= schema.exclusiveMinimum) errors.push(`${path} violates exclusiveMinimum`);
    if (typeof value === "number" && schema.exclusiveMaximum !== undefined && value >= schema.exclusiveMaximum) errors.push(`${path} violates exclusiveMaximum`);
    for (const k of ["maximum", "maxItems", "maxLength"]) if (schema[k] !== undefined && size > schema[k]) errors.push(`${path} violates ${k}`);
  }
  return errors;
}

export function wireArguments(value: any, schema: Schema, notes: string[] = []): any {
  if (Array.isArray(value)) return value.map(v => wireArguments(v, schema.items ?? {}, notes));
  if (!value || typeof value !== "object" || !schema.properties) return value;
  const out: Record<string, unknown> = {};
  for (const [key, v] of Object.entries(value)) {
    const p = schema.properties[key] ?? {};
    const canonical = p["x-alias-for"];
    if (canonical) notes.push(`${key} is deprecated; use ${canonical}.`);
    const target = p["x-wire-key"] ?? (canonical ? schema.properties[canonical]?.["x-wire-key"] ?? canonical : key);
    out[target] = wireArguments(v, p, notes);
  }
  return out;
}
