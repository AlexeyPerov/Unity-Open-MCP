// EditMode tests for the M16 Plan 2 typed component tools (ComponentsTools).
// Covers parameter parsing, resolver branches, and core add/get/modify flows
// on a fresh GameObject in the active scene.
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    public class ComponentsToolsTests
    {
        [Test]
        public void Add_MissingTypes_ReturnsMissingParameter()
        {
            var go = new GameObject("__MCPTest_Comp_Add_Missing");
            try
            {
                var result = ComponentsTools.Add(
                    "{\"instance_id\":" + go.GetInstanceID() + "}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("missing_parameter", result.ErrorCode);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Add_UnknownType_ReportsError()
        {
            var go = new GameObject("__MCPTest_Comp_Add_Unknown");
            try
            {
                var result = ComponentsTools.Add(
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"component_types\":[\"__DefinitelyNotAType__\"]}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"errors\":", result.Output);
                StringAssert.Contains("__DefinitelyNotAType__", result.Output);
                Assert.IsNull(go.GetComponent<Rigidbody>()); // sanity
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Add_KnownType_Attaches()
        {
            var go = new GameObject("__MCPTest_Comp_Add_Rigidbody");
            try
            {
                var result = ComponentsTools.Add(
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"component_types\":[\"UnityEngine.Rigidbody\"]}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.IsNotNull(go.GetComponent<Rigidbody>());
                StringAssert.Contains("\"added\":[", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Add_ClassNameFallback_Attaches()
        {
            var go = new GameObject("__MCPTest_Comp_Add_ClassFallback");
            try
            {
                var result = ComponentsTools.Add(
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"component_types\":[\"BoxCollider\"]}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.IsNotNull(go.GetComponent<BoxCollider>());
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Destroy_RemovesComponent()
        {
            var go = new GameObject("__MCPTest_Comp_Destroy");
            go.AddComponent<BoxCollider>();
            try
            {
                var result = ComponentsTools.Destroy(
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"component_types\":[\"UnityEngine.BoxCollider\"]}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.IsNull(go.GetComponent<BoxCollider>());
                StringAssert.Contains("\"destroyed\":[", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Get_MissingTypeName_ReturnsMissingParameter()
        {
            var go = new GameObject("__MCPTest_Comp_Get_Missing");
            try
            {
                var result = ComponentsTools.Get(
                    "{\"instance_id\":" + go.GetInstanceID() + "}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("missing_parameter", result.ErrorCode);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Get_ReturnsTransformFields()
        {
            var go = new GameObject("__MCPTest_Comp_Get_Transform");
            try
            {
                var result = ComponentsTools.Get(
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"type_name\":\"UnityEngine.Transform\"}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"type\":\"UnityEngine.Transform\"", result.Output);
                StringAssert.Contains("\"fields\":[", result.Output);
                // Local position is a serialized field.
                StringAssert.Contains("\"localPosition\"", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Get_BoundedByMaxFields()
        {
            var go = new GameObject("__MCPTest_Comp_Get_Bounded");
            try
            {
                var result = ComponentsTools.Get(
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"type_name\":\"UnityEngine.Transform\",\"max_fields\":1,\"include_properties\":false}");
                Assert.IsTrue(result.Success);
                // Only one field should be emitted (count = 1).
                StringAssert.Contains("\"count\":1", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Get_UnknownType_ReturnsTypeNotFound()
        {
            var go = new GameObject("__MCPTest_Comp_Get_Unknown");
            try
            {
                var result = ComponentsTools.Get(
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"type_name\":\"__Nope__\"}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("type_not_found", result.ErrorCode);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Modify_MissingFields_ReturnsMissingParameter()
        {
            var go = new GameObject("__MCPTest_Comp_Modify_Missing");
            try
            {
                var result = ComponentsTools.Modify(
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"type_name\":\"UnityEngine.Transform\"}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("missing_parameter", result.ErrorCode);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Modify_BadPath_ReportsError()
        {
            var go = new GameObject("__MCPTest_Comp_Modify_BadPath");
            try
            {
                var result = ComponentsTools.Modify(
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"type_name\":\"UnityEngine.Transform\"," +
                    "\"fields\":[{\"path\":\"__NoSuchField__\",\"value\":1}]}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"errors\":", result.Output);
                StringAssert.Contains("__NoSuchField__", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Modify_WritesLocalPosition()
        {
            var go = new GameObject("__MCPTest_Comp_Modify_Pos");
            try
            {
                var result = ComponentsTools.Modify(
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"type_name\":\"UnityEngine.Transform\"," +
                    "\"fields\":[{\"path\":\"m_LocalPosition\",\"value\":[5,6,7]}]}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual(new Vector3(5, 6, 7), go.transform.localPosition);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ListAll_Default_ReturnsTypes()
        {
            var result = ComponentsTools.ListAll("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"types\":[", result.Output);
            StringAssert.Contains("\"count\":", result.Output);
            // Rigidbody is a canonical built-in; should always be present.
            StringAssert.Contains("UnityEngine.Rigidbody", result.Output);
        }

        [Test]
        public void ListAll_WithQuery_FiltersResults()
        {
            var result = ComponentsTools.ListAll(
                "{\"query\":\"Rigidbody\",\"max_results\":50}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("UnityEngine.Rigidbody", result.Output);
            // A query for "Rigidbody" should not surface Collider.
            StringAssert.DoesNotContain("UnityEngine.BoxCollider", result.Output);
        }

        [Test]
        public void ResolveComponentType_FullName()
        {
            var t = ComponentsTools.ResolveComponentType("UnityEngine.Rigidbody");
            Assert.IsNotNull(t);
            Assert.AreEqual(typeof(Rigidbody), t);
        }

        [Test]
        public void ResolveComponentType_ClassNameFallback()
        {
            var t = ComponentsTools.ResolveComponentType("Rigidbody");
            Assert.IsNotNull(t);
            Assert.AreEqual(typeof(Rigidbody), t);
        }

        [Test]
        public void ResolveComponentType_Unknown_ReturnsNull()
        {
            var t = ComponentsTools.ResolveComponentType("__DefinitelyNotAType__");
            Assert.IsNull(t);
        }
    }
}
