import test from "node:test";
import assert from "node:assert/strict";
import { typedToolHints } from "./typed-tool-hints.js";

test("typedToolHints names the typed tool a snippet re-implements", () => {
  assert.match(typedToolHints("return UnityEngine.PlayerPrefs.GetInt(\"coins\");").join(), /playerprefs_get/);
  assert.match(typedToolHints("UnityEngine.PlayerPrefs.SetInt(\"coins\", 5); return 1;").join(), /playerprefs_set/);
  assert.match(typedToolHints("UnityEngine.Time.timeScale = 4f; return 1;").join(), /settings_set_time/);
  assert.match(typedToolHints("UnityEngine.Application.onBeforeRender += Tick; return 1;").join(), /playtest_probe/);
  assert.match(typedToolHints("return System.AppDomain.CurrentDomain.GetData(\"ep04.log\");").join(), /playtest_probe/);
  assert.deepEqual(typedToolHints("return UnityEngine.Time.timeScale == 1f;"), [], "a comparison is not a write");
  assert.match(typedToolHints("var f = t.GetField(\"_hp\", BindingFlags.NonPublic | BindingFlags.Instance); return f.GetValue(o);").join(), /include_non_public/);
  assert.match(typedToolHints("return typeof(X).GetMethod(\"Run\").Invoke(null, null);").join(), /invoke_method/);
  assert.match(typedToolHints("UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes(); return 1;").join(), /scene_save/);
  const prefab = typedToolHints(
    "var root = UnityEditor.PrefabUtility.LoadPrefabContents(\"Assets/UI/Shop.prefab\"); " +
    "UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, \"Assets/UI/Shop.prefab\"); " +
    "UnityEditor.PrefabUtility.UnloadPrefabContents(root); return 1;").join();
  assert.match(prefab, /in_prefab/, "a LoadPrefabContents edit points at the typed in_prefab route");
  assert.deepEqual(typedToolHints("return 1 + 1;"), []);
  assert.deepEqual(typedToolHints(undefined), []);
  const many = typedToolHints("PlayerPrefs.GetInt(\"a\"); PlayerPrefs.SetInt(\"a\",1); Time.timeScale = 2; GameObject.Find(\"A\"); return 1;");
  assert.equal(many.length, 3, "at most three hints");
});
