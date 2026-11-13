// Point execute_csharp callers at the typed tool that already does what their
// snippet does. Snippets that read PlayerPrefs, set Time.timeScale, save a
// scene, call a method by reflection or dump private fields each have a typed,
// schema-checked equivalent that agents rarely discover on their own. The hint
// is advisory: it rides on the result as `typedToolHints` and never changes
// the outcome.

interface HintRule {
  /** All patterns must match the snippet. */
  patterns: RegExp[];
  hint: string;
}

const RULES: HintRule[] = [
  {
    patterns: [/\bPlayerPrefs\s*\.\s*(?:GetInt|GetFloat|GetString|HasKey)\b/],
    hint: "unity_open_mcp_playerprefs_get reads PlayerPrefs keys (several at once) without a snippet.",
  },
  {
    patterns: [/\bPlayerPrefs\s*\.\s*(?:SetInt|SetFloat|SetString|DeleteKey|DeleteAll)\b/],
    hint: "unity_open_mcp_playerprefs_set writes / deletes PlayerPrefs keys with a typed value.",
  },
  {
    patterns: [/\bTime\s*\.\s*timeScale\s*=(?!=)/],
    hint: "unity_open_mcp_settings_set_time sets Time.timeScale (and fixed delta time); for a wait only, playtest_wait time_scale restores it on every exit.",
  },
  {
    patterns: [/\bEditorSceneManager\s*\.\s*(?:SaveScene|SaveOpenScenes)\b/],
    hint: "unity_open_mcp_scene_save saves an open scene (or saves it as a new asset_path) through the gate.",
  },
  {
    patterns: [/\.\s*GetMethod\s*\(/, /\.\s*Invoke\s*\(/],
    hint: "unity_open_mcp_invoke_method calls a static or instance method (non-public included) by name with typed arguments.",
  },
  {
    patterns: [/\bBindingFlags\s*\.\s*NonPublic\b/, /\.\s*GetField\s*\(/],
    hint: "unity_open_mcp_object_get_data {\"instance_id\":…,\"include_non_public\":true} dumps an object's private fields without reflection code.",
  },
  {
    patterns: [/\bGameObject\s*\.\s*Find\s*\(/],
    hint: "unity_open_mcp_gameobject_find finds GameObjects by path / name / tag / component and returns their instance ids.",
  },
  {
    patterns: [/\bAssetDatabase\s*\.\s*FindAssets\s*\(/],
    hint: "unity_open_mcp_search_assets searches assets by name / type / label (works offline too).",
  },
];

const MAX_HINTS = 3;

/** Typed-tool suggestions for a snippet's code, at most MAX_HINTS, in rule order. */
export function typedToolHints(code: unknown): string[] {
  if (typeof code !== "string" || !code) return [];
  const hints: string[] = [];
  for (const rule of RULES) {
    if (rule.patterns.every((p) => p.test(code))) hints.push(rule.hint);
    if (hints.length >= MAX_HINTS) break;
  }
  return hints;
}
