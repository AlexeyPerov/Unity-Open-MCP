#pragma warning disable CS0618
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.TypedTools;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Tests
{
    public class GameObjectsToolsTests
    {
        [Test]
        public void Create_MissingName_ReturnsMissingParameter()
        {
            var result = GameObjectsTools.Create("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'name'", result.ErrorMessage);
        }

        [Test]
        public void Create_BadPrimitive_ReturnsInvalidParameter()
        {
            var result = GameObjectsTools.Create("{\"name\":\"X\",\"primitive_type\":\"Torus\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_parameter", result.ErrorCode);
        }

        [Test]
        public void Create_BadParentPath_ReturnsParentNotFound()
        {
            var result = GameObjectsTools.Create(
                "{\"name\":\"X\",\"parent_path\":\"__Nope__/__NopeToo\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("parent_not_found", result.ErrorCode);
        }

        [Test]
        public void Create_Empty_CreatesAndReportsInstance()
        {
            var result = GameObjectsTools.Create("{\"name\":\"__MCPTest_GO_Empty\"}");
            try
            {
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"instanceId\":", result.Output);
                StringAssert.Contains("\"name\":\"__MCPTest_GO_Empty\"", result.Output);
                StringAssert.Contains("\"components\":[", result.Output);
                // Empty GameObject has exactly one Transform component.
                StringAssert.Contains("\"name\":\"Transform\"", result.Output);
            }
            finally
            {
                var go = GameObject.Find("__MCPTest_GO_Empty");
                if (go != null) Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Create_Primitive_AddsRendererCollider()
        {
            var result = GameObjectsTools.Create(
                "{\"name\":\"__MCPTest_GO_Cube\",\"primitive_type\":\"Cube\"}");
            try
            {
                Assert.IsTrue(result.Success, result.ErrorMessage);
                var go = GameObject.Find("__MCPTest_GO_Cube");
                Assert.IsNotNull(go);
                Assert.IsNotNull(go.GetComponent<MeshRenderer>());
                Assert.IsNotNull(go.GetComponent<BoxCollider>());
            }
            finally
            {
                var go = GameObject.Find("__MCPTest_GO_Cube");
                if (go != null) Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Create_WithParent_AttachesUnderParent()
        {
            var parentResult = GameObjectsTools.Create("{\"name\":\"__MCPTest_GO_Parent\"}");
            try
            {
                Assert.IsTrue(parentResult.Success, parentResult.ErrorMessage);
                var result = GameObjectsTools.Create(
                    "{\"name\":\"__MCPTest_GO_Child\",\"parent_path\":\"__MCPTest_GO_Parent\"}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                var child = GameObject.Find("__MCPTest_GO_Child");
                Assert.IsNotNull(child);
                Assert.IsNotNull(child.transform.parent);
                Assert.AreEqual("__MCPTest_GO_Parent", child.transform.parent.name);
            }
            finally
            {
                var parent = GameObject.Find("__MCPTest_GO_Parent");
                if (parent != null) Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void Destroy_NotFound_ReturnsGameObjectNotFound()
        {
            var result = GameObjectsTools.Destroy("{\"name\":\"__Nope__\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("gameobject_not_found", result.ErrorCode);
        }

        [Test]
        public void Destroy_ByInstanceId_RemovesObject()
        {
            var go = new GameObject("__MCPTest_GO_Destroy");
            try
            {
                var result = GameObjectsTools.Destroy(
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.IsTrue(go == null); // DestroyImmediate nulls the ref in EditMode.
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Duplicate_ClonesUnderSameParent()
        {
            var original = new GameObject("__MCPTest_GO_DupSource");
            try
            {
                var result = GameObjectsTools.Duplicate(
                    "{\"instance_id\":" +InstanceId.Of(original) + "}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"action\":\"duplicated\"", result.Output);
                var clone = GameObject.Find("__MCPTest_GO_DupSource");
                // Both original and clone share the same name (we strip (Clone)).
                Assert.IsNotNull(clone);
            }
            finally
            {
                // Destroy any remaining objects with the test name.
                while (GameObject.Find("__MCPTest_GO_DupSource") != null)
                    Object.DestroyImmediate(GameObject.Find("__MCPTest_GO_DupSource"));
            }
        }

        [Test]
        public void Find_TargetedNotFound_ReturnsEmptyListWithNotFoundFlag()
        {
            var result = GameObjectsTools.Find("{\"name\":\"__Nope__\"}");
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"count\":0", result.Output);
            StringAssert.Contains("\"notFound\":true", result.Output);
        }

        [Test]
        public void Find_TargetedFound_ReturnsSingleResult()
        {
            var go = new GameObject("__MCPTest_GO_Find");
            try
            {
                var result = GameObjectsTools.Find(
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
                Assert.IsTrue(result.Success);
                StringAssert.Contains("\"count\":1", result.Output);
                StringAssert.Contains("\"path\":\"__MCPTest_GO_Find\"", result.Output);
                StringAssert.Contains("\"transform\":", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Find_ListMode_WithNameContainsFilter()
        {
            var a = new GameObject("__MCPTest_GO_Filter_Match");
            var b = new GameObject("__MCPTest_GO_Filter_NoMatch");
            try
            {
                var result = GameObjectsTools.Find(
                    "{\"name_contains\":\"__MCPTest_GO_Filter_Match\"}");
                Assert.IsTrue(result.Success);
                StringAssert.Contains("\"count\":1", result.Output);
                StringAssert.Contains("__MCPTest_GO_Filter_Match", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
            }
        }

        [Test]
        public void Modify_NoFields_ReturnsMissingParameter()
        {
            var go = new GameObject("__MCPTest_GO_Modify");
            try
            {
                var result = GameObjectsTools.Modify(
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("missing_parameter", result.ErrorCode);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Modify_RenamesViaNameTarget()
        {
            var go = new GameObject("__MCPTest_GO_RenameBefore");
            try
            {
                var result = GameObjectsTools.Modify(
                    "{\"name_target\":\"__MCPTest_GO_RenameBefore\",\"name\":\"__MCPTest_GO_RenameAfter\"}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual("__MCPTest_GO_RenameAfter", go.name);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Modify_BadTag_ReturnsInvalidTag()
        {
            var go = new GameObject("__MCPTest_GO_BadTag");
            try
            {
                var result = GameObjectsTools.Modify(
                    "{\"instance_id\":" +InstanceId.Of(go) + ",\"tag\":\"__DefinitelyNotATag__\"}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("invalid_tag", result.ErrorCode);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Modify_BadLayer_ReturnsInvalidLayer()
        {
            var go = new GameObject("__MCPTest_GO_BadLayer");
            try
            {
                var result = GameObjectsTools.Modify(
                    "{\"instance_id\":" +InstanceId.Of(go) + ",\"layer\":\"99\"}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("invalid_layer", result.ErrorCode);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Modify_Transform_UpdatesPosition()
        {
            var go = new GameObject("__MCPTest_GO_Transform");
            try
            {
                var result = GameObjectsTools.Modify(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"position\":\"1,2,3\",\"rotation\":\"0,90,0\",\"scale\":\"2,2,2\"}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual(new Vector3(1, 2, 3), go.transform.position);
                Assert.AreEqual(90f, go.transform.eulerAngles.y, 0.001f);
                Assert.AreEqual(new Vector3(2, 2, 2), go.transform.localScale);
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ---- T22.1.4 — three-surface RFC 7396 form -------------------------

        [Test]
        public void Modify_GameObjectDiffs_RenamesTarget()
        {
            var go = new GameObject("__MCPTest_GO_DiffRename");
            try
            {
                var result = GameObjectsTools.Modify(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"gameObjectDiffs\":{\"name\":\"__MCPTest_GO_DiffRenameDone\"}}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual("__MCPTest_GO_DiffRenameDone", go.name);
                // Root-only call keeps the legacy compact shape (no surfaces block).
                StringAssert.DoesNotContain("\"surfaces\":", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Modify_JsonPatches_UpdatesComponentFields()
        {
            var go = new GameObject("__MCPTest_GO_JsonPatch");
            var rb = go.AddComponent<Rigidbody>();
            try
            {
                rb.mass = 1f;
                var result = GameObjectsTools.Modify(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"jsonPatchesPerGameObject\":{\"Rigidbody\":{\"mass\":2.5}}}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual(2.5f, rb.mass, 0.0001f, "jsonPatch should update Rigidbody.mass via reflection.");
                // A json surface present → extended shape with a surfaces summary.
                StringAssert.Contains("\"surfaces\":", result.Output);
                StringAssert.Contains("\"jsonPatches\"", result.Output);
                StringAssert.Contains("Rigidbody.mass", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Modify_PathPatches_UpdatesChild()
        {
            var go = new GameObject("__MCPTest_GO_PathRoot");
            var child = new GameObject("__MCPTest_GO_PathChild");
            child.transform.SetParent(go.transform, false);
            try
            {
                var result = GameObjectsTools.Modify(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"pathPatchesPerGameObject\":{\"__MCPTest_GO_PathChild\":{\"active\":false}}}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.IsFalse(child.activeSelf, "pathPatch should deactivate the child.");
                StringAssert.Contains("\"surfaces\":", result.Output);
                StringAssert.Contains("\"pathPatches\"", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Modify_ThreeSurface_AppliesInOrder()
        {
            var go = new GameObject("__MCPTest_GO_Three");
            var rb = go.AddComponent<Rigidbody>();
            var child = new GameObject("__MCPTest_GO_ThreeChild");
            child.transform.SetParent(go.transform, false);
            try
            {
                rb.mass = 1f;
                var result = GameObjectsTools.Modify(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"jsonPatchesPerGameObject\":{\"Rigidbody\":{\"mass\":3.0}}" +
                    ",\"pathPatchesPerGameObject\":{\"__MCPTest_GO_ThreeChild\":{\"active\":false}}" +
                    ",\"gameObjectDiffs\":{\"layer\":2}}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                // jsonPatch applied.
                Assert.AreEqual(3.0f, rb.mass, 0.0001f);
                // pathPatch applied.
                Assert.IsFalse(child.activeSelf);
                // root diff applied.
                Assert.AreEqual(2, go.layer);
                // Extended shape surfaces all three.
                StringAssert.Contains("\"jsonPatches\"", result.Output);
                StringAssert.Contains("\"pathPatches\"", result.Output);
                StringAssert.Contains("\"diffs\":{\"applied\":true", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Modify_JsonPatches_BadField_ReportsErrorContinuesBatch()
        {
            var go = new GameObject("__MCPTest_GO_JsonBad");
            go.AddComponent<Rigidbody>();
            try
            {
                var result = GameObjectsTools.Modify(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"jsonPatchesPerGameObject\":{\"Rigidbody\":{\"mass\":4.0,\"__NopeField__\":1}}}");
                // mass applied → status ok; bad field recorded in errors[], not fatal.
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"errorCount\":1", result.Output);
                StringAssert.Contains("__NopeField__", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Modify_JsonPatches_ComponentNotFound_ReportsFailure()
        {
            var go = new GameObject("__MCPTest_GO_NoComp");
            try
            {
                var result = GameObjectsTools.Modify(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"jsonPatchesPerGameObject\":{\"Rigidbody\":{\"mass\":1.0}}}");
                // No Rigidbody on the GO → recorded in jsonPatches.failed. Nothing
                // else applied, so status in the output JSON is "error", but the
                // dispatch itself succeeds (the failure is surfaced, not thrown).
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"status\":\"error\"", result.Output);
                StringAssert.Contains("not found", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void SetParent_NoParentArg_ReturnsMissingParameter()
        {
            var go = new GameObject("__MCPTest_GO_Orphan");
            try
            {
                var result = GameObjectsTools.SetParent(
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("missing_parameter", result.ErrorCode);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void SetParent_CycleDetected_ReturnsInvalidParameter()
        {
            var parent = new GameObject("__MCPTest_GO_CycleParent");
            var child = new GameObject("__MCPTest_GO_CycleChild");
            child.transform.SetParent(parent.transform, false);
            try
            {
                // Try to make parent a child of child (cycle).
                var result = GameObjectsTools.SetParent(
                    "{\"instance_id\":" +InstanceId.Of(parent) +
                    ",\"parent_instance_id\":" +InstanceId.Of(child) + "}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("invalid_parameter", result.ErrorCode);
            }
            finally
            {
                Object.DestroyImmediate(parent);
                Object.DestroyImmediate(child);
            }
        }

        [Test]
        public void SetParent_Basic_Attaches()
        {
            var parent = new GameObject("__MCPTest_GO_SP_Parent");
            var child = new GameObject("__MCPTest_GO_SP_Child");
            try
            {
                var result = GameObjectsTools.SetParent(
                    "{\"instance_id\":" +InstanceId.Of(child) +
                    ",\"parent_instance_id\":" +InstanceId.Of(parent) + "}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual(parent.transform, child.transform.parent);
            }
            finally
            {
                Object.DestroyImmediate(parent);
                Object.DestroyImmediate(child);
            }
        }

        // ---- T1.2 — reparent exactly once, honoring world_position_stays ----

        // world_position_stays:false keeps the child's LOCAL pose, so parenting
        // under a non-origin parent changes the WORLD position. We capture the
        // pre-parent world transform and assert the reparent honored `false`.
        [Test]
        public void SetParent_WorldPositionStaysFalse_KeepsLocalPose()
        {
            var parent = new GameObject("__MCPTest_GO_WPS_Parent");
            parent.transform.position = new Vector3(10, 0, 0);
            var child = new GameObject("__MCPTest_GO_WPS_Child");
            child.transform.position = new Vector3(5, 0, 0);
            try
            {
                var result = GameObjectsTools.SetParent(
                    "{\"instance_id\":" +InstanceId.Of(child) +
                    ",\"parent_instance_id\":" +InstanceId.Of(parent) +
                    ",\"world_position_stays\":false}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual(parent.transform, child.transform.parent);
                // local pose preserved: localPos stays (5,0,0) even though
                // parent moved the world origin to (10,0,0). World is now (15,0,0).
                Assert.AreEqual(new Vector3(5, 0, 0), child.transform.localPosition);
                Assert.AreEqual(new Vector3(15, 0, 0), child.transform.position);
            }
            finally
            {
                Object.DestroyImmediate(parent);
                Object.DestroyImmediate(child);
            }
        }

        // The bug: SetTransformParent (true) + SetParent(false) moved the
        // transform twice, and the undo snapshot captured the wrong pose. After
        // the fix (single call honoring worldPositionStays), Ctrl+Z restores the
        // exact pre-parent WORLD transform even under world_position_stays:false.
        [Test]
        public void SetParent_WorldPositionStaysFalse_UndoRestoresPreParentWorldTransform()
        {
            var parent = new GameObject("__MCPTest_GO_WPSU_Parent");
            parent.transform.position = new Vector3(100, 0, 0);
            var child = new GameObject("__MCPTest_GO_WPSU_Child");
            var preParentWorld = new Vector3(3, 4, 5);
            child.transform.position = preParentWorld;
            var preParentEuler = new Vector3(0, 45, 0);
            child.transform.eulerAngles = preParentEuler;
            try
            {
                var result = GameObjectsTools.SetParent(
                    "{\"instance_id\":" +InstanceId.Of(child) +
                    ",\"parent_instance_id\":" +InstanceId.Of(parent) +
                    ",\"world_position_stays\":false}");
                Assert.IsTrue(result.Success, result.ErrorMessage);

                // Reparent honored false → world pose changed (local preserved).
                Assert.AreNotEqual(preParentWorld, child.transform.position);

                Undo.PerformUndo();

                // A single undo reverts the reparent and restores the exact
                // pre-parent WORLD transform (position equality, not just
                // "object moved"). Before the fix the undo snapshot held the
                // true-pose intermediate, so this restoration was wrong.
                Assert.IsNull(child.transform.parent, "undo should restore child to scene root (no parent)");
                Assert.AreEqual(preParentWorld, child.transform.position,
                    "undo must restore the exact pre-parent world position");
                Assert.AreEqual(preParentEuler.y, child.transform.eulerAngles.y, 0.001f,
                    "undo must restore the exact pre-parent world rotation");
            }
            finally
            {
                Object.DestroyImmediate(parent);
                Object.DestroyImmediate(child);
            }
        }

        // ---- T1.3 — duplicate preserves prefab-ness + sibling index --------

        [Test]
        public void Duplicate_PlainGameObject_SitsNextToSource()
        {
            var parent = new GameObject("__MCPTest_GO_DupSibling_Parent");
            var a = new GameObject("__MCPTest_GO_DupSibling_A");
            var b = new GameObject("__MCPTest_GO_DupSibling_B");
            a.transform.SetParent(parent.transform, false);
            b.transform.SetParent(parent.transform, false);
            try
            {
                Assert.AreEqual(0, a.transform.GetSiblingIndex());
                Assert.AreEqual(1, b.transform.GetSiblingIndex());

                var result = GameObjectsTools.Duplicate(
                    "{\"instance_id\":" +InstanceId.Of(a) + "}");
                Assert.IsTrue(result.Success, result.ErrorMessage);

                // Two objects now share the name (clone strips "(Clone)"); find
                // the one that sits at source.GetSiblingIndex()+1 (the clone).
                var siblings = new System.Collections.Generic.List<GameObject>();
                foreach (Transform t in parent.transform)
                    if (t.gameObject.name == "__MCPTest_GO_DupSibling_A") siblings.Add(t.gameObject);
                Assert.AreEqual(2, siblings.Count, "source + clone expected");

                // The clone sits immediately after the source. Sort by sibling
                // index so the lower one is the source, the higher one the clone.
                siblings.Sort((x, y) => x.transform.GetSiblingIndex().CompareTo(y.transform.GetSiblingIndex()));
                int srcIdx = siblings[0].transform.GetSiblingIndex();
                int cloneIdx = siblings[1].transform.GetSiblingIndex();
                Assert.AreEqual(srcIdx + 1, cloneIdx,
                    "clone sibling index must be immediately after the source");
                Assert.AreEqual(parent.transform, siblings[1].transform.parent,
                    "clone stays under the same parent");
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        // Review follow-up — Duplicate re-applies the source's local
        // position/rotation/scale to the clone (GameObjectsTools.cs:140-143),
        // but the existing tests only covered prefab-connection + sibling
        // index. This pins the local-pose half of the contract: a distinct
        // non-default pose on the source must land on the clone verbatim.
        [Test]
        public void Duplicate_PlainGameObject_PreservesLocalPose()
        {
            var parent = new GameObject("__MCPTest_GO_DupPose_Parent");
            var src = new GameObject("__MCPTest_GO_DupPose_Src");
            src.transform.SetParent(parent.transform, false);
            // Distinct, non-default local pose.
            var pos = new Vector3(1.5f, -2f, 3.25f);
            var rot = Quaternion.Euler(15f, 30f, 45f);
            var scl = new Vector3(2f, 0.5f, 1.25f);
            src.transform.localPosition = pos;
            src.transform.localRotation = rot;
            src.transform.localScale = scl;
            try
            {
                var result = GameObjectsTools.Duplicate(
                    "{\"instance_id\":" + InstanceId.Of(src) + "}");
                Assert.IsTrue(result.Success, result.ErrorMessage);

                // Source + clone share the name (clone strips "(Clone)");
                // the clone sits at the higher sibling index.
                var siblings = new System.Collections.Generic.List<GameObject>();
                foreach (Transform t in parent.transform)
                    if (t.gameObject.name == "__MCPTest_GO_DupPose_Src") siblings.Add(t.gameObject);
                Assert.AreEqual(2, siblings.Count, "source + clone expected");
                siblings.Sort((x, y) => x.transform.GetSiblingIndex().CompareTo(y.transform.GetSiblingIndex()));
                var clone = siblings[1]; // higher sibling index == the clone

                Assert.AreEqual(pos, clone.transform.localPosition,
                    "clone must inherit the source's localPosition");
                Assert.AreEqual(rot, clone.transform.localRotation,
                    "clone must inherit the source's localRotation");
                Assert.AreEqual(scl, clone.transform.localScale,
                    "clone must inherit the source's localScale");
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void Duplicate_PrefabInstance_KeepsPrefabConnection()
        {
            const string TmpDir = "Assets/TmpDupTests";
            const string PrefabPath = TmpDir + "/DupSourcePrefab.prefab";
            // Clean slate.
            if (AssetDatabase.LoadMainAssetAtPath(PrefabPath) != null)
                AssetDatabase.DeleteAsset(PrefabPath);
            if (AssetDatabase.IsValidFolder(TmpDir))
                AssetDatabase.DeleteAsset(TmpDir);
            AssetDatabase.CreateFolder("Assets", "TmpDupTests");

            GameObject instance = null;
            try
            {
                // Build a throwaway prefab + a connected instance in-scene.
                var prefabSrc = new GameObject("DupSourcePrefab");
                prefabSrc.AddComponent<MeshRenderer>();
                PrefabUtility.SaveAsPrefabAsset(prefabSrc, PrefabPath);
                Object.DestroyImmediate(prefabSrc);

                var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                Assert.IsNotNull(prefabAsset, "prefab asset must exist after SaveAsPrefabAsset");
                instance = PrefabUtility.InstantiatePrefab(prefabAsset) as GameObject;
                Assert.AreEqual(PrefabInstanceStatus.Connected,
                    PrefabUtility.GetPrefabInstanceStatus(instance),
                    "sanity: source instance must be Connected before duplicate");

                var result = GameObjectsTools.Duplicate(
                    "{\"instance_id\":" +InstanceId.Of(instance) + "}");
                Assert.IsTrue(result.Success, result.ErrorMessage);

                // Find the clone: two objects named DupSourcePrefab now exist
                // (source + clone share the name because we strip "(Clone)").
                var matches = new System.Collections.Generic.List<GameObject>();
                foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include))
                    if (go.name == "DupSourcePrefab") matches.Add(go);
                Assert.AreEqual(2, matches.Count, "source + clone expected");

                GameObject clone = null;
                foreach (var go in matches)
                {
                    if (go == instance) continue;
                    clone = go;
                    break;
                }
                Assert.IsNotNull(clone, "clone must exist alongside the source");
                Assert.AreEqual(PrefabInstanceStatus.Connected,
                    PrefabUtility.GetPrefabInstanceStatus(clone),
                    "duplicate of a prefab instance must stay Connected to the prefab asset");
            }
            finally
            {
                if (instance != null) Object.DestroyImmediate(instance);
                // Destroy any leftover clones.
                foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include))
                    if (go != null && go.name == "DupSourcePrefab") Object.DestroyImmediate(go);
                if (AssetDatabase.IsValidFolder(TmpDir))
                    AssetDatabase.DeleteAsset(TmpDir);
            }
        }

        [Test]
        public void ResolveInstance_NotFound_ReturnsGameObjectNotFound()
        {
            var r = GameObjectsTools.ResolveInstance("{\"name\":\"__Nope__\"}");
            Assert.IsFalse(r.Ok);
            Assert.AreEqual("gameobject_not_found", r.Result.ErrorCode);
        }
    }
}
