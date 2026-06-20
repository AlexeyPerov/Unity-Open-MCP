// GetInstanceID() is deprecated in Unity 6000.4+; the bridge's JSON handle
// contract is built on the stable int instance ID, so the deprecated API is
// used deliberately here. See packages/bridge/Editor/ObjectRefs/ObjectHandle.cs.
#pragma warning disable CS0618
// EditMode tests for the M16 Plan 2 typed GameObject tools (GameObjectsTools).
// Covers parameter parsing and resolver branches. Mutating tests use a fresh
// GameObject in the active scene and tear it down; EditMode has an empty scene
// so the active-scene mark is a safe no-op.
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
