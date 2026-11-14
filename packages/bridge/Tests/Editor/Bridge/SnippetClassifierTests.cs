using NUnit.Framework;

namespace UnityOpenMcpBridge.Tests
{
    // The one classification of an execute_csharp snippet, and the three
    // decisions read off it: the lifecycle the call reports, whether the
    // dirty-scene guard preflights it, and whether read_only is honoured.
    public static class SnippetClassifierTests
    {
        private const string Tool = "unity_open_mcp_execute_csharp";

        private static string Body(string code, string extra = "") =>
            "{" + extra + "\"code\":\"" + BridgeJson.EscapeStringContent(code) + "\"}";

        // ----- Plain: reads and asset / object writes -----

        [TestCase("return 42;")]
        [TestCase("var so = new UnityEditor.SerializedObject(UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.ScriptableObject>(\"Assets/A.asset\")); so.FindProperty(\"m_Value\").intValue = 3; so.ApplyModifiedProperties(); UnityEditor.AssetDatabase.SaveAssets(); return 1;")]
        [TestCase("var m = new UnityEngine.Material(UnityEngine.Shader.Find(\"Standard\")); UnityEditor.AssetDatabase.CreateAsset(m, \"Assets/M.mat\"); UnityEditor.EditorUtility.SetDirty(m); UnityEditor.AssetDatabase.SaveAssetIfDirty(m);")]
        [TestCase("UnityEditor.EditorPrefs.SetInt(\"k\", 1); return UnityEditor.EditorApplication.isPlaying;")]
        // Reflection with literal names shows what it runs.
        [TestCase("return System.AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).First(t => t.Name == \"Mathf\").GetMethod(\"Clamp01\").Invoke(null, new object[] { 2f });")]
        [TestCase("typeof(Foo).GetMethod( \"Bar\" , System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null);")]
        [TestCase("typeof(Foo).GetProperty(nameof(Foo.Value)).SetValue(target, 3);")]
        // A field lookup by a computed name runs no code.
        [TestCase("foreach (var n in names) typeof(Foo).GetField(n).SetValue(target, 1);")]
        // Names that only resemble a listed call.
        [TestCase("var mode = UnityEditor.SceneManagement.OpenSceneMode.Additive; var s = UnityEngine.SceneManagement.LoadSceneMode.Single; return mode;")]
        [TestCase("var p = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene(); UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(p);")]
        [TestCase("return UnityEditor.PlayerSettings.productName == \"x\" ? UnityEditor.PlayerSettings.GetScriptingBackend(t) : 0;")]
        [TestCase("return UnityEditor.EditorApplication.isPlaying == false && UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode;")]
        [TestCase("return System.IO.File.ReadAllText(\"Assets/Scripts/Foo.cs\").Length;")]
        public static void Plain(string code)
        {
            var verdict = SnippetClassifier.Classify(code);
            Assert.AreEqual(SnippetEffect.Plain, verdict.Effect, verdict.Reason);
            Assert.IsNull(verdict.Reason);
        }

        // ----- Disruptive: anything that can compile, reload, import, switch, play, build or prompt -----

        [TestCase("UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();", "RequestScriptCompilation (recompile / domain reload)")]
        [TestCase("UnityEditor.EditorUtility.RequestScriptReload();", "RequestScriptReload (recompile / domain reload)")]
        [TestCase("typeof(UnityEditor.PlayerSettings).GetMethod(\"SetScriptingDefineSymbolsForGroup\", types).Invoke(null, args);", "SetScriptingDefineSymbolsForGroup (recompile / domain reload)")]
        [TestCase("UnityEditor.AssetDatabase.Refresh();", "Refresh (asset import)")]
        [TestCase("UnityEditor.AssetDatabase.ImportAsset(\"Assets/A.cs\");", "ImportAsset (asset import)")]
        [TestCase("importer.SaveAndReimport();", "SaveAndReimport (asset import)")]
        [TestCase("UnityEditor.AssetDatabase.MoveAsset(a, b);", "MoveAsset (asset move / delete)")]
        [TestCase("UnityEditor.AssetDatabase.DeleteAsset(a);", "DeleteAsset (asset move / delete)")]
        [TestCase("UnityEditor.SceneManagement.EditorSceneManager.OpenScene(\"Assets/A.unity\");", "OpenScene (scene switch)")]
        [TestCase("UnityEditor.SceneManagement.EditorSceneManager.NewScene(setup, mode);", "NewScene (scene switch)")]
        [TestCase("UnityEditor.SceneManagement.EditorSceneManager.CloseScene(s, true);", "CloseScene (scene switch)")]
        [TestCase("UnityEditor.SceneManagement.EditorSceneManager.RestoreSceneManagerSetup(setup);", "RestoreSceneManagerSetup (scene switch)")]
        [TestCase("UnityEngine.SceneManagement.SceneManager.LoadScene(1);", "LoadScene (scene switch)")]
        [TestCase("UnityEditor.AssetDatabase.OpenAsset(obj);", "OpenAsset (asset open: a scene or prefab asset switches the scene or stage)")]
        [TestCase("UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(\"Assets/P.prefab\");", "OpenPrefab (prefab stage switch)")]
        [TestCase("UnityEditor.SceneManagement.StageUtility.GoToMainStage();", "GoToMainStage (prefab stage switch)")]
        [TestCase("UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();", "SaveCurrentModifiedScenesIfUserWantsTo (native save prompt)")]
        [TestCase("UnityEditor.EditorApplication.EnterPlaymode();", "EnterPlaymode (play mode change)")]
        [TestCase("UnityEditor.EditorApplication.ExecuteMenuItem(\"File/New Scene\");", "ExecuteMenuItem (menu execution)")]
        [TestCase("UnityEditor.BuildPipeline.BuildPlayer(options);", "BuildPlayer (build / platform change)")]
        [TestCase("UnityEditor.EditorUserBuildSettings.SwitchActiveBuildTargetAsync(g, t);", "SwitchActiveBuildTargetAsync (build / platform change)")]
        [TestCase("new UnityEditor.TestTools.TestRunner.Api.TestRunnerApi();", "TestRunnerApi (test run)")]
        // A listed name counts wherever it stands — that is how a reflective call by literal name is caught.
        [TestCase("typeof(UnityEditor.SceneManagement.EditorSceneManager).GetMethod(\"OpenScene\", new[] { typeof(string) }).Invoke(null, new object[] { p });", "OpenScene (scene switch)")]
        public static void Disruptive_NamesTheCall(string code, string expected)
        {
            var verdict = SnippetClassifier.Classify(code);
            Assert.AreEqual(SnippetEffect.Disruptive, verdict.Effect);
            Assert.AreEqual("the snippet calls " + expected, verdict.Reason);
        }

        [TestCase("UnityEditor.EditorApplication.isPlaying = true;", "sets isPlaying (play mode change)")]
        [TestCase("UnityEditor.EditorApplication.isPlaying ^= true;", "sets isPlaying (play mode change)")]
        [TestCase("typeof(UnityEditor.EditorApplication).GetProperty(\"isPlaying\").SetValue(null, true);", "sets isPlaying through reflection (play mode change)")]
        [TestCase("UnityEditor.PlayerSettings.allowUnsafeCode = true;", "writes PlayerSettings (player settings change, which can recompile)")]
        [TestCase("UnityEditor.PlayerSettings.Android.minSdkVersion = v;", "writes PlayerSettings (player settings change, which can recompile)")]
        [TestCase("UnityEditor.PlayerSettings.SetIl2CppCompilerConfiguration(t, c);", "writes PlayerSettings (player settings change, which can recompile)")]
        [TestCase("UnityEditor.PackageManager.Client.Add(\"com.unity.splines\");", "changes installed packages (package change)")]
        [TestCase("UnityEditor.EditorApplication.OpenProject(path);", "exits the Editor or opens another project (editor exit / project switch)")]
        [TestCase("System.IO.File.WriteAllText(\"Assets/Scripts/Foo.cs\", src);", "writes a compile input (.cs / .asmdef / .dll / manifest.json) (recompile / domain reload)")]
        [TestCase("System.IO.File.Copy(src, \"Packages/manifest.json\", true);", "writes a compile input (.cs / .asmdef / .dll / manifest.json) (recompile / domain reload)")]
        public static void Disruptive_DescribesTheWrite(string code, string expected)
        {
            var verdict = SnippetClassifier.Classify(code);
            Assert.AreEqual(SnippetEffect.Disruptive, verdict.Effect);
            Assert.AreEqual("the snippet " + expected, verdict.Reason);
        }

        [Test]
        public static void Disruptive_ReportsTheFirstCallInSourceOrder()
        {
            var verdict = SnippetClassifier.Classify(
                "UnityEditor.EditorApplication.ExecuteMenuItem(\"Assets/Refresh\"); UnityEditor.SceneManagement.EditorSceneManager.OpenScene(p);");
            Assert.AreEqual("the snippet calls ExecuteMenuItem (menu execution)", verdict.Reason);
        }

        // ----- Opaque: the text does not show what runs -----

        [TestCase("typeof(UnityEditor.SceneManagement.EditorSceneManager).GetMethod(\"Open\" + \"Scene\", new[] { typeof(string) }).Invoke(null, args);", "GetMethod")]
        [TestCase("var n = string.Concat(\"Enter\", \"Playmode\"); typeof(UnityEditor.EditorApplication).GetMethod(n).Invoke(null, null);", "GetMethod")]
        [TestCase("typeof(UnityEditor.EditorApplication).GetProperty($\"is{state}\").SetValue(null, true);", "GetProperty")]
        [TestCase("t.InvokeMember(name, flags, null, null, args);", "InvokeMember")]
        [TestCase("System.Delegate.CreateDelegate(typeof(System.Action), t, name).DynamicInvoke();", "CreateDelegate")]
        [TestCase("go.SendMessage(message);", "SendMessage")]
        [TestCase("typeof(UnityEditor.EditorApplication).GetMethods().First(m => m.Name.StartsWith(prefix)).Invoke(null, null);", "GetMethods")]
        [TestCase("foreach (var p in t.GetProperties()) p.SetValue(null, true);", "GetProperties")]
        public static void Opaque_ComputedMemberName(string code, string api)
        {
            var verdict = SnippetClassifier.Classify(code);
            Assert.AreEqual(SnippetEffect.Opaque, verdict.Effect, verdict.Reason);
            StringAssert.Contains("computed name (" + api + ")", verdict.Reason);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  \n")]
        public static void Opaque_NoCode(string code)
        {
            var verdict = SnippetClassifier.Classify(code);
            Assert.AreEqual(SnippetEffect.Opaque, verdict.Effect);
            Assert.IsNull(verdict.Reason);
        }

        // ----- SavesScenes -----

        [TestCase("UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes(); return 1;", "SaveOpenScenes")]
        [TestCase("var s = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(\"Assets/A.unity\"); return UnityEditor.SceneManagement.EditorSceneManager.SaveScene(s);", "SaveScene")]
        public static void SavesScenes(string code, string api)
        {
            var verdict = SnippetClassifier.Classify(code);
            Assert.AreEqual(SnippetEffect.SavesScenes, verdict.Effect);
            Assert.AreEqual("the snippet calls " + api + " (scene save)", verdict.Reason);
        }

        [Test]
        public static void SaveSceneAs_IsAPrompt_NotASave()
        {
            Assert.AreEqual(SnippetEffect.Disruptive,
                SnippetClassifier.Classify("UnityEditor.SceneManagement.EditorSceneManager.SaveSceneAs(s);").Effect);
            // A save next to a disruptive call is still disruptive.
            Assert.AreEqual(SnippetEffect.Disruptive, SnippetClassifier.Classify(
                "UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes(); UnityEditor.AssetDatabase.Refresh();").Effect);
        }

        // ----- the decisions read off the verdict -----

        // An asset write and a scene save settle the asset database and are not
        // preflighted; a disruptive or opaque snippet keeps the guarded class.
        [TestCase("so.ApplyModifiedProperties(); UnityEditor.AssetDatabase.SaveAssets();", LifecyclePolicy.EditorSettle, false)]
        [TestCase("UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes(); return 1;", LifecyclePolicy.EditorSettle, false)]
        [TestCase("UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();", LifecyclePolicy.RestartThenSettle, true)]
        [TestCase("UnityEditor.SceneManagement.EditorSceneManager.OpenScene(p);", LifecyclePolicy.RestartThenSettle, true)]
        [TestCase("t.GetMethod(name).Invoke(null, null);", LifecyclePolicy.RestartThenSettle, true)]
        [TestCase("", LifecyclePolicy.RestartThenSettle, true)]
        public static void WritingSnippet_LifecycleAndGuardFollowTheVerdict(string code, LifecyclePolicy lifecycle, bool guarded)
        {
            var body = Body(code, "\"paths_hint\":[\"Assets/A.asset\"],");
            var contract = EffectiveToolContract.Resolve(Tool, body);
            Assert.IsTrue(contract.IsMutating);
            Assert.AreEqual(lifecycle, contract.Lifecycle);
            Assert.AreEqual(guarded, SceneDirtyGuard.AppliesTo(Tool, body));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo(Tool, Body(code, "\"ignore_scene_dirty\":true,")));
        }

        // read_only is an assertion: honoured unless the text contradicts it.
        [TestCase("return System.AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).First(t => t.Name == \"Mathf\").GetMethod(\"Clamp01\").Invoke(null, new object[] { 2f });", true)]
        [TestCase("return t.GetMethod(name).Invoke(null, null);", true)]
        [TestCase("UnityEditor.AssetDatabase.Refresh();", false)]
        [TestCase("UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();", false)]
        public static void ReadOnlyAssertion_HonouredUnlessTheTextContradictsIt(string code, bool honoured)
        {
            var body = Body(code, "\"read_only\":true,");
            var contract = EffectiveToolContract.Resolve(Tool, body);
            Assert.AreEqual(!honoured, contract.IsMutating);
            Assert.AreEqual(honoured, EffectiveToolContract.ReadOnlyOverrideReason(Tool, body) == null);
            if (honoured)
            {
                Assert.AreEqual(LifecyclePolicy.None, contract.Lifecycle);
                Assert.IsFalse(SceneDirtyGuard.AppliesTo(Tool, body));
            }
            else
                Assert.AreEqual(SnippetClassifier.Classify(code).Reason, EffectiveToolContract.ReadOnlyOverrideReason(Tool, body));
        }

        [Test]
        public static void SetupRoslyn_KeepsTheDeclaredLifecycle()
        {
            var contract = EffectiveToolContract.Resolve(Tool, "{\"setup_roslyn\":true,\"code\":\"return 1;\"}");
            Assert.AreEqual(LifecyclePolicy.RestartThenSettle, contract.Lifecycle);
            Assert.IsNull(contract.GuardReason);
        }

        // The contract carries what the guard needs: the narrowed preflight of
        // a save snippet, and the reason a guarded snippet is guarded.
        [Test]
        public static void Contract_CarriesTheSaveFlagAndTheGuardReason()
        {
            var save = EffectiveToolContract.Resolve(Tool, Body("UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();"));
            Assert.IsTrue(save.SavesScenes);
            Assert.IsNull(save.GuardReason);

            var open = EffectiveToolContract.Resolve(Tool, Body("UnityEditor.SceneManagement.EditorSceneManager.OpenScene(p);"));
            Assert.IsFalse(open.SavesScenes);
            Assert.AreEqual("the snippet calls OpenScene (scene switch)", open.GuardReason);

            var plain = EffectiveToolContract.Resolve(Tool, Body("return 1;"));
            Assert.IsFalse(plain.SavesScenes);
            Assert.IsNull(plain.GuardReason);
            Assert.IsNull(EffectiveToolContract.Resolve("unity_open_mcp_recompile_scripts", "{}").GuardReason);
        }
    }
}
