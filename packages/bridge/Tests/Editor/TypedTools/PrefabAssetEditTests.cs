using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpBridge.TypedTools;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Tests
{
    // The prefab-asset container (in_prefab): a typed tool edits a prefab
    // asset in place — resolution only inside the loaded contents (paths from
    // the prefab root), a save only after a mutating call succeeded, an unload
    // always (also when the tool throws), the refusal while the prefab is open
    // in a stage with unsaved changes, variant / nested-prefab overrides, and
    // batch runs that share one load and one save.
    //
    // Fixture prefabs are rebuilt before every test under a temp folder.
    [TestFixture]
    public class PrefabAssetEditTests
    {
        private const string Root = "Assets/Tests/BridgeFixtures/PrefabAssetEdit";
        private const string LeafPath = Root + "/PA_Leaf.prefab";
        private const string CratePath = Root + "/PA_Crate.prefab";
        private const string VariantPath = Root + "/PA_CrateVariant.prefab";

        [SetUp]
        public void SetUp()
        {
            CloseStage();
            EnsureFolder(Root);
            BuildFixtures();
        }

        [TearDown]
        public void TearDown()
        {
            CloseStage();
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

        // ---------------------------- resolution ----------------------------

        [Test]
        public void Path_ResolvesInsideThePrefabContents_FromTheRoot()
        {
            var r = Dispatch("unity_open_mcp_gameobject_find",
                "{\"in_prefab\":\"" + CratePath + "\",\"path\":\"PA_Crate/Lid\"}");
            Assert.IsTrue(r.Success, r.ErrorMessage);
            StringAssert.Contains("\"path\":\"PA_Crate/Lid\"", r.Output);
            StringAssert.Contains("\"kind\":\"prefab_asset\"", r.Output);
            StringAssert.Contains("\"instanceIds\":\"ephemeral\"", r.Output);
            Assert.IsNull(PrefabAssetEdits.Current, "the contents are released after the call");
        }

        [Test]
        public void Path_MissingInThePrefab_IsNotFound_AndNamesTheRoot()
        {
            var r = Dispatch("unity_open_mcp_component_modify",
                "{\"in_prefab\":\"" + CratePath + "\",\"path\":\"Lid\",\"type_name\":\"UnityEngine.Transform\"," +
                "\"fields\":[{\"path\":\"m_LocalPosition\",\"value\":[1,2,3]}]}");
            Assert.IsFalse(r.Success);
            Assert.AreEqual("gameobject_not_found", r.ErrorCode);
            StringAssert.Contains("in_prefab", r.ErrorMessage);
            StringAssert.Contains("'PA_Crate'", r.ErrorMessage);
            StringAssert.Contains("\"saved\":false", r.ErrorDetailJson);
        }

        [Test]
        public void SceneObjectId_IsNotFoundInThePrefab()
        {
            var go = new GameObject("__PA_SceneObject");
            try
            {
                var r = Dispatch("unity_open_mcp_component_get",
                    "{\"in_prefab\":\"" + CratePath + "\",\"instance_id\":\"" + go.GetInstanceID() +
                    "\",\"type_name\":\"UnityEngine.Transform\"}");
                Assert.IsFalse(r.Success);
                Assert.AreEqual("gameobject_not_found", r.ErrorCode);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void InPrefab_WithScenePath_IsRefused()
        {
            var r = Dispatch("unity_open_mcp_gameobject_find",
                "{\"in_prefab\":\"" + CratePath + "\",\"scene_path\":\"Assets/X.unity\",\"path\":\"PA_Crate\"}");
            Assert.AreEqual("invalid_parameter", r.ErrorCode);
        }

        [Test]
        public void InPrefab_OnAToolWithoutIt_IsRefused()
        {
            var r = Dispatch("unity_open_mcp_scene_focus",
                "{\"in_prefab\":\"" + CratePath + "\",\"path\":\"PA_Crate\"}");
            Assert.AreEqual("in_prefab_unsupported", r.ErrorCode);
        }

        [Test]
        public void MissingAsset_IsPrefabAssetNotFound()
        {
            var r = Dispatch("unity_open_mcp_gameobject_find",
                "{\"in_prefab\":\"" + Root + "/Nope.prefab\",\"path\":\"X\"}");
            Assert.AreEqual("prefab_asset_not_found", r.ErrorCode);
        }

        // ------------------------- save and unload --------------------------

        [Test]
        public void Mutation_IsSavedToTheAsset_AndNoStageOpens()
        {
            var r = Dispatch("unity_open_mcp_gameobject_create",
                "{\"in_prefab\":\"" + CratePath + "\",\"name\":\"PA_Added\",\"parent_path\":\"PA_Crate/Lid\"}");
            Assert.IsTrue(r.Success, r.ErrorMessage);
            StringAssert.Contains("\"path\":\"PA_Crate/Lid/PA_Added\"", r.Output);
            StringAssert.Contains("\"saved\":true", r.Output);
            StringAssert.Contains("\"undo\":false", r.Output);
            Assert.IsNull(PrefabStageUtility.GetCurrentPrefabStage(), "no prefab stage is opened");
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(CratePath);
            Assert.IsNotNull(asset.transform.Find("Lid/PA_Added"), "the edit is in the saved asset");
        }

        [Test]
        public void FailedMutation_LeavesTheAssetByteIdentical()
        {
            var before = File.ReadAllBytes(CratePath);
            // The path patch renames Lid in memory, then the root diff fails.
            var r = Dispatch("unity_open_mcp_gameobject_modify",
                "{\"in_prefab\":\"" + CratePath + "\",\"path\":\"PA_Crate\"," +
                "\"pathPatchesPerGameObject\":{\"Lid\":{\"name\":\"Renamed\"}},\"tag\":\"__NoSuchTag__\"}");
            Assert.IsFalse(r.Success);
            Assert.AreEqual("invalid_tag", r.ErrorCode);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(CratePath));
            Assert.IsNull(PrefabAssetEdits.Current);
        }

        [Test]
        public void ToolThatThrows_StillUnloads_AndWritesNothing()
        {
            var before = File.ReadAllBytes(CratePath);
            GameObject loaded = null;
            using (TargetScope.Enter("{}"))
            {
                Assert.Throws<InvalidOperationException>(() => PrefabAssetEdits.Dispatch(
                    "unity_open_mcp_component_modify",
                    "{\"in_prefab\":\"" + CratePath + "\"}",
                    () =>
                    {
                        loaded = PrefabAssetEdits.Current.Root;
                        loaded.name = "Changed";
                        throw new InvalidOperationException("boom");
                    }));
            }
            Assert.IsTrue(loaded == null, "the loaded contents were unloaded");
            Assert.IsNull(PrefabAssetEdits.Current);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(CratePath));
        }

        [Test]
        public void PrefabRoot_CannotBeDestroyed()
        {
            var r = Dispatch("unity_open_mcp_gameobject_destroy",
                "{\"in_prefab\":\"" + CratePath + "\",\"path\":\"PA_Crate\"}");
            Assert.AreEqual("prefab_root_protected", r.ErrorCode);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(CratePath));
        }

        // ------------------------------ stage -------------------------------

        [Test]
        public void OpenStageWithUnsavedChanges_IsRefused_AndKeepsTheStageEdit()
        {
            var stage = PrefabStageUtility.OpenPrefab(CratePath);
            Assert.IsNotNull(stage);
            var marker = new GameObject("PA_StageOnly");
            marker.transform.SetParent(stage.prefabContentsRoot.transform, false);
            EditorSceneManager.MarkSceneDirty(stage.scene);
            var before = File.ReadAllBytes(CratePath);

            var r = Dispatch("unity_open_mcp_gameobject_create",
                "{\"in_prefab\":\"" + CratePath + "\",\"name\":\"PA_Blocked\"}");
            Assert.AreEqual("prefab_stage_unsaved", r.ErrorCode);
            Assert.IsNotNull(r.NextSteps);
            Assert.IsTrue(Array.Exists(r.NextSteps, s => s.Contains("prefab_save")));
            Assert.IsNotNull(stage.prefabContentsRoot.transform.Find("PA_StageOnly"), "the stage edit is intact");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(CratePath));
        }

        [Test]
        public void OpenStageWithoutUnsavedChanges_DoesNotBlockTheEdit()
        {
            Assert.IsNotNull(PrefabStageUtility.OpenPrefab(CratePath));
            var r = Dispatch("unity_open_mcp_gameobject_create",
                "{\"in_prefab\":\"" + CratePath + "\",\"name\":\"PA_Clean\",\"parent_path\":\"PA_Crate\"}");
            Assert.IsTrue(r.Success, r.ErrorMessage);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(CratePath).transform.Find("PA_Clean"));
        }

        // ----------------------- variants and nesting -----------------------

        [Test]
        public void VariantEdit_NamesTheVariant_AndLeavesTheBaseUntouched()
        {
            var baseBefore = File.ReadAllBytes(CratePath);
            var r = Dispatch("unity_open_mcp_gameobject_modify",
                "{\"in_prefab\":\"" + VariantPath + "\",\"path\":\"PA_CrateVariant/Lid\",\"position\":\"0,3,0\",\"local_space\":true}");
            Assert.IsTrue(r.Success, r.ErrorMessage);
            StringAssert.Contains("\"variantOf\":\"" + CratePath + "\"", r.Output);
            StringAssert.Contains("\"kind\":\"variant_base\"", r.Output);
            CollectionAssert.AreEqual(baseBefore, File.ReadAllBytes(CratePath));
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            Assert.AreEqual(3f, variant.transform.Find("Lid").localPosition.y, 1e-4);
        }

        [Test]
        public void NestedInstanceEdit_IsAnOverrideOnTheOuterPrefab()
        {
            var leafBefore = File.ReadAllBytes(LeafPath);
            var r = Dispatch("unity_open_mcp_gameobject_modify",
                "{\"in_prefab\":\"" + CratePath + "\",\"path\":\"PA_Crate/PA_Leaf/Stem\",\"position\":\"0,5,0\",\"local_space\":true}");
            Assert.IsTrue(r.Success, r.ErrorMessage);
            StringAssert.Contains("\"kind\":\"nested_prefab\"", r.Output);
            StringAssert.Contains("\"instancePath\":\"PA_Crate/PA_Leaf\"", r.Output);
            StringAssert.Contains("\"source\":\"" + LeafPath + "\"", r.Output);
            CollectionAssert.AreEqual(leafBefore, File.ReadAllBytes(LeafPath));
            var stem = AssetDatabase.LoadAssetAtPath<GameObject>(CratePath).transform.Find("PA_Leaf/Stem");
            Assert.AreEqual(5f, stem.localPosition.y, 1e-4);
        }

        // The copy of a nested prefab instance is another instance of that
        // prefab in the asset, with the overrides the source carries.
        [Test]
        public void Duplicate_NestedPrefabInstance_KeepsItsPrefabAndOverrides()
        {
            var leafBefore = File.ReadAllBytes(LeafPath);
            var modify = Dispatch("unity_open_mcp_gameobject_modify",
                "{\"in_prefab\":\"" + CratePath + "\",\"path\":\"PA_Crate/PA_Leaf/Stem\",\"position\":\"0,5,0\",\"local_space\":true}");
            Assert.IsTrue(modify.Success, modify.ErrorMessage);

            var r = Dispatch("unity_open_mcp_gameobject_duplicate",
                "{\"in_prefab\":\"" + CratePath + "\",\"path\":\"PA_Crate/PA_Leaf\"}");
            Assert.IsTrue(r.Success, r.ErrorMessage);
            StringAssert.Contains("\"prefabConnection\":\"preserved\"", r.Output);
            StringAssert.Contains("\"saved\":true", r.Output);
            CollectionAssert.AreEqual(leafBefore, File.ReadAllBytes(LeafPath), "the nested prefab is untouched");

            var crate = AssetDatabase.LoadAssetAtPath<GameObject>(CratePath).transform;
            Assert.AreEqual(3, crate.childCount, "Lid, PA_Leaf and its copy");
            var copy = crate.GetChild(2).gameObject;
            Assert.AreEqual("PA_Leaf", copy.name);
            Assert.IsTrue(PrefabUtility.IsAnyPrefabInstanceRoot(copy), "the copy is a prefab instance");
            Assert.AreEqual(LeafPath, PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(copy));
            Assert.AreEqual(5f, copy.transform.Find("Stem").localPosition.y, 1e-4, "the override is copied");
        }

        // ------------------------------ batch -------------------------------

        [Test]
        public void Batch_StepsOnOnePrefab_ShareOneSave()
        {
            var r = BatchExecuteTool.Execute(Batch(true,
                Step("unity_open_mcp_gameobject_create", "\"name\":\"PA_B1\",\"parent_path\":\"PA_Crate\""),
                Step("unity_open_mcp_gameobject_create", "\"name\":\"PA_B2\",\"parent_path\":\"PA_Crate/PA_B1\""),
                Step("unity_open_mcp_component_add", "\"path\":\"PA_Crate/PA_B1/PA_B2\",\"component_types\":[\"UnityEngine.BoxCollider\"]"),
                Step("unity_open_mcp_gameobject_modify", "\"path\":\"PA_Crate/PA_B1\",\"position\":\"0,1,0\",\"local_space\":true")));
            Assert.IsTrue(r.Success, r.ErrorMessage + " " + r.Output);
            StringAssert.Contains("\"prefabAssets\":[{\"path\":\"" + CratePath + "\",\"steps\":[0,1,2,3],\"saved\":true", r.Output);
            var b2 = AssetDatabase.LoadAssetAtPath<GameObject>(CratePath).transform.Find("PA_B1/PA_B2");
            Assert.IsNotNull(b2);
            Assert.IsNotNull(b2.GetComponent<BoxCollider>());
            Assert.IsNull(PrefabAssetEdits.Current);
        }

        [Test]
        public void Batch_FailingMiddleStep_WritesNothing()
        {
            var before = File.ReadAllBytes(CratePath);
            var r = BatchExecuteTool.Execute(Batch(true,
                Step("unity_open_mcp_gameobject_create", "\"name\":\"PA_F1\",\"parent_path\":\"PA_Crate\""),
                Step("unity_open_mcp_component_add", "\"path\":\"PA_Crate/Missing\",\"component_types\":[\"UnityEngine.BoxCollider\"]"),
                Step("unity_open_mcp_gameobject_create", "\"name\":\"PA_F2\",\"parent_path\":\"PA_Crate\"")));
            Assert.IsFalse(r.Success);
            Assert.IsFalse(r.PartialCommit, "a dropped prefab run committed nothing");
            StringAssert.Contains("\"saved\":false", r.Output);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(CratePath));
            Assert.IsNull(PrefabAssetEdits.Current);
        }

        // ------------------------------- gate -------------------------------

        [Test]
        public void GateScope_AddsTheEditedPrefab_WhenPathsHintLeavesItOut()
        {
            var scope = PrefabAssetEdits.GateScope(new[] { "Assets/Scenes/Main.unity" },
                new[] { "{\"in_prefab\":\"" + CratePath + "\"}" });
            CollectionAssert.AreEqual(new[] { "Assets/Scenes/Main.unity", CratePath }, scope);
            var covered = PrefabAssetEdits.GateScope(new[] { Root }, new[] { "{\"in_prefab\":\"" + CratePath + "\"}" });
            CollectionAssert.AreEqual(new[] { Root }, covered);
        }

        [Test]
        public void Advisory_NamesSavedAndDroppedEdits_NotReadOnlyRuns()
        {
            Assert.IsNull(PrefabAssetEdits.Advisory(new[]
            {
                new PrefabAssetEditOutcome { AssetPath = CratePath, Reason = "no step changed it", Steps = new System.Collections.Generic.List<int> { 0 } },
            }));
            var text = PrefabAssetEdits.Advisory(new[]
            {
                new PrefabAssetEditOutcome { AssetPath = CratePath, Mutating = true, Saved = true },
                new PrefabAssetEditOutcome { AssetPath = VariantPath, Mutating = true, Discarded = true, Reason = "step 1 failed" },
            });
            StringAssert.Contains("Saved the prefab asset '" + CratePath + "'", text);
            StringAssert.Contains("undo does not cover", text);
            StringAssert.Contains("Nothing was written to '" + VariantPath + "'", text);
        }

        // ------------------------------ helpers ------------------------------

        private static ToolDispatchResult Dispatch(string tool, string body) =>
            BridgeHttpServer.DispatchTool(tool, body);

        private static string Step(string tool, string paramsInner) =>
            "{\"tool\":\"" + tool + "\",\"params\":{\"in_prefab\":\"" + CratePath + "\"," + paramsInner + "}}";

        private static string Batch(bool failFast, params string[] steps) =>
            "{\"fail_fast\":" + (failFast ? "true" : "false") + ",\"paths_hint\":[\"" + CratePath +
            "\"],\"commands\":[" + string.Join(",", steps) + "]}";

        private static void CloseStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null) return;
            stage.ClearDirtiness();
            StageUtility.GoToMainStage();
        }

        // PA_Leaf (Stem) is nested in PA_Crate (Lid, PA_Leaf); PA_CrateVariant
        // is a variant of PA_Crate. Built in a preview scene so no user scene
        // is touched.
        private static void BuildFixtures()
        {
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var leaf = new GameObject("PA_Leaf");
                SceneManager.MoveGameObjectToScene(leaf, preview);
                new GameObject("Stem").transform.SetParent(leaf.transform, false);
                var leafAsset = PrefabUtility.SaveAsPrefabAsset(leaf, LeafPath);
                Object.DestroyImmediate(leaf);

                var crate = new GameObject("PA_Crate");
                SceneManager.MoveGameObjectToScene(crate, preview);
                new GameObject("Lid").transform.SetParent(crate.transform, false);
                var nested = (GameObject)PrefabUtility.InstantiatePrefab(leafAsset, preview);
                nested.transform.SetParent(crate.transform, false);
                var crateAsset = PrefabUtility.SaveAsPrefabAsset(crate, CratePath);
                Object.DestroyImmediate(crate);

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(crateAsset, preview);
                instance.name = "PA_CrateVariant";
                PrefabUtility.SaveAsPrefabAsset(instance, VariantPath);
                Object.DestroyImmediate(instance);
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
