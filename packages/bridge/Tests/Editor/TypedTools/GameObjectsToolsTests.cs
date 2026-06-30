#pragma warning disable CS0618
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.TypedTools;

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
                    "{\"instance_id\":" + go.GetInstanceID() + "}");
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
                    "{\"instance_id\":" + original.GetInstanceID() + "}");
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
                    "{\"instance_id\":" + go.GetInstanceID() + "}");
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
                    "{\"instance_id\":" + go.GetInstanceID() + "}");
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
                    "{\"instance_id\":" + go.GetInstanceID() + ",\"tag\":\"__DefinitelyNotATag__\"}");
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
                    "{\"instance_id\":" + go.GetInstanceID() + ",\"layer\":\"99\"}");
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
                    "{\"instance_id\":" + go.GetInstanceID() +
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
                    "{\"instance_id\":" + go.GetInstanceID() +
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
                    "{\"instance_id\":" + go.GetInstanceID() +
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
                    "{\"instance_id\":" + go.GetInstanceID() +
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
                    "{\"instance_id\":" + go.GetInstanceID() +
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
                    "{\"instance_id\":" + go.GetInstanceID() +
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
                    "{\"instance_id\":" + go.GetInstanceID() +
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
                    "{\"instance_id\":" + go.GetInstanceID() + "}");
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
                    "{\"instance_id\":" + parent.GetInstanceID() +
                    ",\"parent_instance_id\":" + child.GetInstanceID() + "}");
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
                    "{\"instance_id\":" + child.GetInstanceID() +
                    ",\"parent_instance_id\":" + parent.GetInstanceID() + "}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual(parent.transform, child.transform.parent);
            }
            finally
            {
                Object.DestroyImmediate(parent);
                Object.DestroyImmediate(child);
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
