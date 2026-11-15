using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityOpenMcpBridge.ObjectRefs;
using UnityOpenMcpBridge.TypedTools;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Tests
{
    // The shared GameObject resolver (TypedTargets): which container a path
    // resolves in (named > paths_hint > open prefab stage > active scene,
    // ambiguous_path otherwise), the prefab stage (paths from the prefab
    // root, with or without the "Canvas (Environment)" wrapper, and by
    // instance id), and instance-id safety (persistent / environment /
    // non-GameObject ids are refused).
    //
    // Fixture prefabs and scenes live under a temp folder. Multi-scene tests
    // replace the open scenes and restore them afterwards, so they are
    // skipped when a loaded scene has unsaved changes.
    [TestFixture]
    public class TargetResolverTests
    {
        private const string Root = "Assets/Tests/BridgeFixtures/TargetResolver";
        private const string HudPath = Root + "/TR_Hud.prefab";
        private const string CratePath = Root + "/TR_Crate.prefab";
        private const string ScenePathA = Root + "/TR_SceneA.unity";
        private const string ScenePathB = Root + "/TR_SceneB.unity";
        private const string ScenePathC = Root + "/TR_SceneC.unity";

        private bool _scenesReady;
        private SceneSetup[] _setup;
        private bool _replacedScenes;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            CloseStage();
            EnsureFolder(Root);
            CreatePrefab(HudPath, ui: true);
            CreatePrefab(CratePath, ui: false);

            // Scene assets are built in Single mode (an untitled scene blocks
            // additive creation), so only when nothing unsaved would be lost.
            if (AnyLoadedSceneDirty()) return;
            var setup = CaptureSetup();
            try
            {
                BuildScene(ScenePathA, "TR_Canvas", "Slot", alsoCrate: true);
                BuildScene(ScenePathB, "TR_Canvas", "Slot", alsoCrate: false);
                BuildScene(ScenePathC, "TR_Floor", null, alsoCrate: false);
                _scenesReady = true;
            }
            finally
            {
                RestoreSetup(setup);
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            CloseStage();
            if (AssetDatabase.IsValidFolder(Root))
            {
                AssetDatabase.DeleteAsset(Root);
                AssetDatabase.Refresh();
            }
        }

        [TearDown]
        public void TearDown()
        {
            CloseStage();
            if (_replacedScenes)
            {
                RestoreSetup(_setup);
                _replacedScenes = false;
            }
        }

        // ------------------------- which container --------------------------

        [Test]
        public void Path_InOneScene_ResolvesThere()
        {
            OpenScenes(ScenePathA, ScenePathC, active: ScenePathC);
            var r = TypedTargets.Resolve(0, "TR_Canvas/Slot", null, TargetRequest.None);
            Assert.IsTrue(r.Ok, r.Failure?.Message);
            Assert.AreEqual(ScenePathA, r.Container.AssetPath);
            Assert.AreEqual(TargetContainerKind.Scene, r.Container.Kind);
        }

        [Test]
        public void Path_InTwoScenes_HintedSceneWinsOverActive()
        {
            OpenScenes(ScenePathA, ScenePathB, active: ScenePathB);
            var r = TypedTargets.Resolve(0, "TR_Canvas", null, new TargetRequest(null, null, new[] { ScenePathA }));
            Assert.IsTrue(r.Ok, r.Failure?.Message);
            Assert.AreEqual(ScenePathA, r.Container.AssetPath);
        }

        [Test]
        public void Path_InTwoScenes_ActiveSceneWinsWithoutHint()
        {
            OpenScenes(ScenePathA, ScenePathB, active: ScenePathB);
            var r = TypedTargets.Resolve(0, "TR_Canvas", null, TargetRequest.None);
            Assert.IsTrue(r.Ok, r.Failure?.Message);
            Assert.AreEqual(ScenePathB, r.Container.AssetPath, "never the first loaded scene");
        }

        [Test]
        public void Path_InTwoScenes_ThirdSceneActive_IsAmbiguous()
        {
            OpenScenes(ScenePathA, ScenePathB, ScenePathC, active: ScenePathC);
            var r = TypedTargets.Resolve(0, "TR_Canvas", null, new TargetRequest(null, null, new[] { ScenePathC }));
            Assert.IsFalse(r.Ok);
            Assert.AreEqual("ambiguous_path", r.Failure.Code);
            StringAssert.Contains(ScenePathA, r.Failure.DetailJson);
            StringAssert.Contains(ScenePathB, r.Failure.DetailJson);
            StringAssert.Contains("scene_path", r.Failure.Message);
            Assert.IsTrue(Array.Exists(r.Failure.NextSteps, s => s.Contains(ScenePathA)));
        }

        [Test]
        public void Name_InTwoScenes_ThirdSceneActive_IsAmbiguous()
        {
            OpenScenes(ScenePathA, ScenePathB, ScenePathC, active: ScenePathC);
            var r = TypedTargets.Resolve(0, null, "Slot", TargetRequest.None);
            Assert.AreEqual("ambiguous_path", r.Failure?.Code);
        }

        [Test]
        public void Path_NamedScene_WinsOverHintAndActive()
        {
            OpenScenes(ScenePathA, ScenePathB, active: ScenePathB);
            var r = TypedTargets.Resolve(0, "TR_Canvas", null, new TargetRequest(ScenePathA, null, new[] { ScenePathB }));
            Assert.IsTrue(r.Ok, r.Failure?.Message);
            Assert.AreEqual(ScenePathA, r.Container.AssetPath);
        }

        [Test]
        public void Path_NamedScene_WithoutTheObject_IsNotFound_AndSaysWhereItIs()
        {
            OpenScenes(ScenePathA, ScenePathC, active: ScenePathA);
            var r = TypedTargets.Resolve(0, "TR_Canvas", null, new TargetRequest(ScenePathC, null, null));
            Assert.AreEqual("gameobject_not_found", r.Failure?.Code);
            StringAssert.Contains(ScenePathA, r.Failure.Message);
        }

        [Test]
        public void Path_NamedScene_NotLoaded_IsSceneNotFound()
        {
            OpenScenes(ScenePathA, active: ScenePathA);
            var r = TypedTargets.Resolve(0, "TR_Canvas", null, new TargetRequest(ScenePathB, null, null));
            Assert.AreEqual("scene_not_found", r.Failure?.Code);
        }

        [Test]
        public void PrefabInstantiate_ParentPath_LandsInHintedScene_AndNamesIt()
        {
            OpenScenes(ScenePathA, ScenePathB, active: ScenePathB);
            var result = PrefabTools.Instantiate("{\"prefab_asset_path\":\"" + CratePath +
                "\",\"parent_path\":\"TR_Canvas\",\"paths_hint\":[\"" + ScenePathB + "\",\"" + CratePath + "\"]}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"container\":{\"kind\":\"scene\",\"path\":\"" + ScenePathB + "\"}", result.Output);
            Assert.IsFalse(SceneManager.GetSceneByPath(ScenePathA).isDirty, "the first loaded scene stays clean");
            Assert.IsTrue(SceneManager.GetSceneByPath(ScenePathB).isDirty);
            var slot = SceneManager.GetSceneByPath(ScenePathB).GetRootGameObjects();
            Assert.IsTrue(Array.Exists(slot, g => g.name == "TR_Canvas" && g.transform.Find("TR_Crate") != null));
        }

        [Test]
        public void PrefabInstantiate_ParentPath_Ambiguous_IsRefused()
        {
            OpenScenes(ScenePathA, ScenePathB, ScenePathC, active: ScenePathC);
            var result = PrefabTools.Instantiate("{\"prefab_asset_path\":\"" + CratePath +
                "\",\"parent_path\":\"TR_Canvas\",\"paths_hint\":[\"" + CratePath + "\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("ambiguous_path", result.ErrorCode);
            Assert.IsFalse(SceneManager.GetSceneByPath(ScenePathA).isDirty);
            Assert.IsFalse(SceneManager.GetSceneByPath(ScenePathB).isDirty);
        }

        // ---------------------------- prefab stage -----------------------------

        [Test]
        public void Stage_PathFromPrefabRoot_Resolves()
        {
            var stage = OpenStage(HudPath);
            var r = TypedTargets.Resolve(0, "TR_Hud/Icon", null, TargetRequest.None);
            Assert.IsTrue(r.Ok, r.Failure?.Message);
            Assert.AreEqual(TargetContainerKind.PrefabStage, r.Container.Kind);
            Assert.AreEqual(HudPath, r.Container.AssetPath);
            Assert.AreEqual(stage.prefabContentsRoot.transform.Find("Icon").gameObject, r.GameObject);
        }

        [Test]
        public void Stage_PathThroughEnvironmentWrapper_Resolves_AndCanonicalPathDropsIt()
        {
            var stage = OpenStage(HudPath);
            var wrapper = stage.prefabContentsRoot.transform.parent;
            if (wrapper == null) Assert.Ignore("Unity added no UI environment around this prefab (uGUI missing).");
            var r = TypedTargets.Resolve(0, wrapper.name + "/TR_Hud/Icon", null, TargetRequest.None);
            Assert.IsTrue(r.Ok, r.Failure?.Message);
            Assert.AreEqual("TR_Hud/Icon", TypedTargets.HierarchyPath(r.GameObject));
            Assert.AreEqual("{\"kind\":\"prefab_stage\",\"path\":\"" + HudPath + "\"}", TypedTargets.ContainerJson(r.GameObject));
        }

        [Test]
        public void Stage_InstanceIds_Resolve()
        {
            var stage = OpenStage(HudPath);
            var root = stage.prefabContentsRoot;
            var icon = root.transform.Find("Icon").gameObject;
            Assert.AreEqual(root, TypedTargets.Resolve(InstanceId.Of(root), null, null, TargetRequest.None).GameObject);
            Assert.AreEqual(icon, TypedTargets.Resolve(InstanceId.Of(icon), null, null, TargetRequest.None).GameObject);
        }

        [Test]
        public void Stage_EnvironmentId_IsRefused()
        {
            var stage = OpenStage(HudPath);
            var wrapper = stage.prefabContentsRoot.transform.parent;
            if (wrapper == null) Assert.Ignore("Unity added no UI environment around this prefab (uGUI missing).");
            var r = TypedTargets.Resolve(InstanceId.Of(wrapper.gameObject), null, null, TargetRequest.None);
            Assert.AreEqual("preview_scene_object", r.Failure?.Code);
        }

        [Test]
        public void Stage_ComponentModify_ByWrapperPath_EditsTheStage()
        {
            var stage = OpenStage(HudPath);
            var wrapper = stage.prefabContentsRoot.transform.parent;
            var path = (wrapper != null ? wrapper.name + "/" : "") + "TR_Hud";
            var result = ComponentsTools.Modify("{\"path\":\"" + path + "\",\"type_name\":\"UnityEngine.RectTransform\"," +
                "\"fields\":[{\"path\":\"m_SizeDelta\",\"value\":[123,45]}],\"paths_hint\":[\"" + HudPath + "\"]}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"gameObjectPath\":\"TR_Hud\"", result.Output);
            StringAssert.Contains("\"kind\":\"prefab_stage\"", result.Output);
            Assert.AreEqual(new Vector2(123, 45), stage.prefabContentsRoot.GetComponent<RectTransform>().sizeDelta);
            Assert.IsTrue(stage.scene.isDirty, "a typed edit marks the stage dirty");
        }

        [Test]
        public void Stage_GameObjectCreate_WithoutParent_GoesUnderPrefabRoot()
        {
            var stage = OpenStage(CratePath);
            var result = GameObjectsTools.Create("{\"name\":\"TR_New\",\"paths_hint\":[\"" + CratePath + "\"]}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsNotNull(stage.prefabContentsRoot.transform.Find("TR_New"));
            StringAssert.Contains("\"path\":\"TR_Crate/TR_New\"", result.Output);
        }

        [Test]
        public void Stage_DuplicateChild_StaysInTheStage()
        {
            var stage = OpenStage(CratePath);
            var result = GameObjectsTools.Duplicate("{\"path\":\"TR_Crate/Lid\",\"paths_hint\":[\"" + CratePath + "\"]}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"container\":{\"kind\":\"prefab_stage\",\"path\":\"" + CratePath + "\"}", result.Output);
            var root = stage.prefabContentsRoot.transform;
            Assert.AreEqual(2, root.childCount, "Lid and its copy");
            Assert.AreEqual("Lid", root.GetChild(1).name);
        }

        [Test]
        public void Stage_GameObjectCreate_HintNamingTheScene_GoesToTheActiveScene()
        {
            OpenScenes(ScenePathA, active: ScenePathA);
            var stage = OpenStage(CratePath);
            var result = GameObjectsTools.Create("{\"name\":\"TR_SceneSide\",\"paths_hint\":[\"" + ScenePathA + "\"]}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"container\":{\"kind\":\"scene\",\"path\":\"" + ScenePathA + "\"}", result.Output);
            Assert.IsNull(stage.prefabContentsRoot.transform.Find("TR_SceneSide"));
        }

        [Test]
        public void Duplicate_RootOfInactiveScene_StaysInItsScene()
        {
            OpenScenes(ScenePathA, ScenePathB, active: ScenePathB);
            var result = GameObjectsTools.Duplicate("{\"path\":\"TR_Crate\",\"paths_hint\":[\"" + ScenePathA + "\"]}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"container\":{\"kind\":\"scene\",\"path\":\"" + ScenePathA + "\"}", result.Output);
            Assert.IsFalse(SceneManager.GetSceneByPath(ScenePathB).isDirty);
        }

        [Test]
        public void Stage_DestroyPrefabRoot_IsRefused()
        {
            OpenStage(CratePath);
            var result = GameObjectsTools.Destroy("{\"path\":\"TR_Crate\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("stage_root_protected", result.ErrorCode);
        }

        [Test]
        public void Stage_WinsOverScene_WithoutHint_SceneWinsWithHint()
        {
            OpenScenes(ScenePathA, active: ScenePathA);
            OpenStage(CratePath);
            var staged = TypedTargets.Resolve(0, "TR_Crate/Lid", null, TargetRequest.None);
            Assert.AreEqual(TargetContainerKind.PrefabStage, staged.Container?.Kind, staged.Failure?.Message);
            var hinted = TypedTargets.Resolve(0, "TR_Crate/Lid", null, new TargetRequest(null, null, new[] { ScenePathA }));
            Assert.AreEqual(ScenePathA, hinted.Container?.AssetPath, hinted.Failure?.Message);
            var named = TypedTargets.Resolve(0, "TR_Crate/Lid", null, new TargetRequest(ScenePathA, null, null));
            Assert.AreEqual(ScenePathA, named.Container?.AssetPath, named.Failure?.Message);
            var stageNamed = TypedTargets.Resolve(0, "TR_Crate/Lid", null, new TargetRequest(CratePath, null, new[] { ScenePathA }));
            Assert.AreEqual(TargetContainerKind.PrefabStage, stageNamed.Container?.Kind, stageNamed.Failure?.Message);
        }

        // --------------------------- instance ids ---------------------------

        [Test]
        public void Id_ObjectInsidePrefabAsset_IsRefused_AndAssetUnchanged()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(CratePath);
            var lid = asset.transform.Find("Lid").gameObject;
            var before = File.ReadAllText(CratePath);
            var result = GameObjectsTools.Modify("{\"instance_id\":\"" + InstanceId.Of(lid) +
                "\",\"position\":\"1,2,3\",\"paths_hint\":[\"" + CratePath + "\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("persistent_object", result.ErrorCode);
            Assert.AreEqual(Vector3.zero, lid.transform.localPosition);
            Assert.AreEqual(before, File.ReadAllText(CratePath));
        }

        [Test]
        public void Id_Component_IsRefused_AndNamesTheGameObjectId()
        {
            var go = new GameObject("__TR_IdComponent");
            try
            {
                var r = TypedTargets.Resolve(InstanceId.Of(go.transform), null, null, TargetRequest.None);
                Assert.AreEqual("not_a_gameobject", r.Failure?.Code);
                StringAssert.Contains(InstanceId.Of(go).ToString(), r.Failure.Message);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Id_NonGameObjectObject_IsRefused()
        {
            var mesh = new Mesh { name = "__TR_Mesh" };
            try
            {
                var r = TypedTargets.Resolve(InstanceId.Of(mesh), null, null, TargetRequest.None);
                Assert.AreEqual("not_a_gameobject", r.Failure?.Code);
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void Id_SceneObject_Resolves()
        {
            var go = new GameObject("__TR_IdScene");
            try
            {
                var r = TypedTargets.Resolve(InstanceId.Of(go), null, null, TargetRequest.None);
                Assert.IsTrue(r.Ok, r.Failure?.Message);
                Assert.AreEqual(go, r.GameObject);
                Assert.AreEqual(TargetContainerKind.Scene, r.Container.Kind);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // --------------------------- request scope --------------------------

        [Test]
        public void Request_BatchStep_InheritsTheBatchHint()
        {
            var outer = TargetScope.Current;
            using (TargetScope.Enter("{\"paths_hint\":[\"Assets/A.unity\"],\"scene_path\":\"Assets/B.unity\"}"))
            {
                var step = TargetRequest.For("{\"path\":\"X\"}");
                CollectionAssert.AreEqual(new[] { "Assets/A.unity" }, step.PathsHint);
                Assert.AreEqual("Assets/B.unity", step.ScenePath);
                var own = TargetRequest.For("{\"path\":\"X\",\"paths_hint\":[\"Assets/C.unity\"]}");
                CollectionAssert.AreEqual(new[] { "Assets/C.unity" }, own.PathsHint);
            }
            Assert.AreSame(outer, TargetScope.Current);
        }

        // ------------------------------- gate --------------------------------

        // A typed edit that lands only in the open stage changes no file, so
        // the gate does not claim a validated delta for it, and dirtyScenes
        // names the stage's prefab.
        [Test]
        public void Gate_StageOnlyEdit_IsReportedAsSkipped_WithTheStageDirty()
        {
            OpenStage(CratePath);
            var result = (GateDispatchResult)typeof(BridgeHttpServer)
                .GetMethod("DispatchWithGateCore", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[]
                {
                    "unity_open_mcp_gameobject_create",
                    "{\"name\":\"TR_Gate\",\"parent_path\":\"TR_Crate\",\"paths_hint\":[\"" + CratePath + "\"]}",
                    "enforce", new[] { CratePath }
                });
            Assert.IsTrue(result.Mutation.Success, result.Mutation.ErrorMessage);
            Assert.IsFalse(result.GateRan);
            Assert.AreEqual("prefab_stage_edit", result.SkippedReason);
            CollectionAssert.Contains(result.DirtyScenePaths, CratePath);
            StringAssert.Contains("prefab stage", result.AgentNextSteps[0]);
        }

        // ------------------------------ helpers ------------------------------

        private static PrefabStage OpenStage(string prefabPath)
        {
            var stage = PrefabStageUtility.OpenPrefab(prefabPath);
            Assert.IsNotNull(stage, "prefab stage did not open");
            return stage;
        }

        // Leave the stage without Unity's save prompt (as prefab_close does).
        private static void CloseStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null) return;
            stage.ClearDirtiness();
            StageUtility.GoToMainStage();
        }

        private void OpenScenes(string first, string second = null, string third = null, string active = null)
        {
            if (!_scenesReady) Assert.Ignore("Fixture scenes not built: a loaded scene had unsaved changes.");
            if (!_replacedScenes)
            {
                if (AnyLoadedSceneDirty()) Assert.Ignore("A loaded scene has unsaved changes.");
                _setup = CaptureSetup();
                _replacedScenes = true;
            }
            EditorSceneManager.OpenScene(first, OpenSceneMode.Single);
            if (second != null) EditorSceneManager.OpenScene(second, OpenSceneMode.Additive);
            if (third != null) EditorSceneManager.OpenScene(third, OpenSceneMode.Additive);
            if (active != null) SceneManager.SetActiveScene(SceneManager.GetSceneByPath(active));
        }

        private static SceneSetup[] CaptureSetup() => EditorSceneManager.GetSceneManagerSetup();

        // An empty setup (only an untitled scene was open) comes back as a new
        // untitled scene.
        private static void RestoreSetup(SceneSetup[] setup)
        {
            CloseStage();
            if (setup == null || setup.Length == 0)
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            else
                EditorSceneManager.RestoreSceneManagerSetup(setup);
        }

        private static bool AnyLoadedSceneDirty()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && s.isDirty) return true;
            }
            return false;
        }

        private static void BuildScene(string path, string rootName, string childName, bool alsoCrate)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject(rootName);
            if (childName != null) new GameObject(childName).transform.SetParent(root.transform, false);
            if (alsoCrate)
            {
                var crate = new GameObject("TR_Crate");
                new GameObject("Lid").transform.SetParent(crate.transform, false);
            }
            Assert.IsTrue(EditorSceneManager.SaveScene(scene, path), "could not save " + path);
        }

        // Built in a preview scene so no user scene is touched.
        private static void CreatePrefab(string path, bool ui)
        {
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = ui ? new GameObject("TR_Hud", typeof(RectTransform)) : new GameObject("TR_Crate");
                SceneManager.MoveGameObjectToScene(root, preview);
                var child = ui ? new GameObject("Icon", typeof(RectTransform)) : new GameObject("Lid");
                child.transform.SetParent(root.transform, false);
                if (ui)
                {
                    // A Graphic makes Unity wrap the prefab in a UI environment
                    // in its stage; the test assembly does not reference uGUI.
                    var image = Type.GetType("UnityEngine.UI.Image, UnityEngine.UI");
                    if (image != null) child.AddComponent(image);
                }
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
