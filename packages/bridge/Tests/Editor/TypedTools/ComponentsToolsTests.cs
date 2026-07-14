#pragma warning disable CS0618
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.TypedTools;
using UnityOpenMcpBridge.ObjectRefs;

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
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
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
                    "{\"instance_id\":" +InstanceId.Of(go) +
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
                    "{\"instance_id\":" +InstanceId.Of(go) +
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
                    "{\"instance_id\":" +InstanceId.Of(go) +
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
                    "{\"instance_id\":" +InstanceId.Of(go) +
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
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
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
                    "{\"instance_id\":" +InstanceId.Of(go) +
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
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"type_name\":\"UnityEngine.Transform\",\"max_fields\":1,\"include_properties\":false}");
                Assert.IsTrue(result.Success);
                // Only one field should be emitted (count = 1).
                StringAssert.Contains("\"count\":1", result.Output);
                StringAssert.Contains("\"truncated\":", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Get_RigidbodyCompact_IsParseableAndUnderSizeBudget()
        {
            var go = new GameObject("__MCPTest_Comp_Get_Rigidbody");
            go.AddComponent<Rigidbody>();
            try
            {
                var result = ComponentsTools.Get(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"type_name\":\"UnityEngine.Rigidbody\"}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.IsTrue(BridgeJson.IsValidJsonObject(result.Output), "Output must be valid JSON: " + result.Output);
                Assert.Less(result.Output.Length, 16 * 1024, "Compact Rigidbody read must stay under 16 KiB.");
                StringAssert.Contains("\"profile\":\"compact\"", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Get_FullProfile_IsParseable()
        {
            var go = new GameObject("__MCPTest_Comp_Get_Full");
            go.AddComponent<Rigidbody>();
            try
            {
                var result = ComponentsTools.Get(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"type_name\":\"UnityEngine.Rigidbody\",\"profile\":\"full\"}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.IsTrue(BridgeJson.IsValidJsonObject(result.Output));
                StringAssert.Contains("\"profile\":\"full\"", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Get_MaxFieldsCap_ReportsTruncation()
        {
            var go = new GameObject("__MCPTest_Comp_Get_Truncated");
            try
            {
                var result = ComponentsTools.Get(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"type_name\":\"UnityEngine.Transform\",\"max_fields\":2,\"include_properties\":false}");
                Assert.IsTrue(result.Success);
                StringAssert.Contains("\"count\":2", result.Output);
                StringAssert.Contains("\"truncated\":", result.Output);
                StringAssert.DoesNotContain("\"truncated\":0", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Get_Paging_ReturnsNextCursorAndResumes()
        {
            var go = new GameObject("__MCPTest_Comp_Get_Paging");
            try
            {
                var page1 = ComponentsTools.Get(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"type_name\":\"UnityEngine.Transform\",\"page_size\":1,\"include_properties\":false}");
                Assert.IsTrue(page1.Success, page1.ErrorMessage);
                StringAssert.Contains("\"pagination\":", page1.Output);
                StringAssert.Contains("\"next_cursor\":\"component_get:1\"", page1.Output);

                var page2 = ComponentsTools.Get(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"type_name\":\"UnityEngine.Transform\",\"page_size\":1,\"cursor\":\"component_get:1\",\"include_properties\":false}");
                Assert.IsTrue(page2.Success, page2.ErrorMessage);
                Assert.AreNotEqual(page1.Output, page2.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        // The truncation split (M30 review follow-up): top-level `truncated`
        // counts fields hidden by the max_fields cap; pagination.truncated
        // counts fields after the current page window. These must NOT be
        // summed — an agent uses them to pick different remedies (raise
        // max_fields vs page on).
        [Test]
        public void Get_PagingSmallerThanMaxFields_PageWithinMaxFieldsWindow()
        {
            var go = new GameObject("__MCPTest_Comp_Get_PageWithinMaxFields");
            try
            {
                // max_fields:6 caps the collected entries at 6; page_size:2
                // walks the 6-entry window in 3 pages. Top-level truncated
                // reflects fields beyond max_fields (whatever Transform hides
                // past 6); pagination.truncated reflects entries after the
                // current page within the 6-entry window.
                var page1 = ComponentsTools.Get(
                    "{\"instance_id\":" + InstanceId.Of(go) +
                    ",\"type_name\":\"UnityEngine.Transform\",\"max_fields\":6,\"page_size\":2,\"include_properties\":false}");
                Assert.IsTrue(page1.Success, page1.ErrorMessage);
                StringAssert.Contains("\"count\":2", page1.Output);
                StringAssert.Contains("\"pagination\":", page1.Output);
                StringAssert.Contains("\"next_cursor\":\"component_get:2\"", page1.Output);
                // pagination.truncated is the remaining-after-page count (4
                // entries left in the window after taking 2), NOT the summed
                // value with the max_fields cap.
                StringAssert.Contains("\"truncated\":4", page1.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Get_PagingPastLastPage_NullNextCursorAndZeroPaginationTruncated()
        {
            var go = new GameObject("__MCPTest_Comp_Get_LastPage");
            try
            {
                // Walk to the end: page_size large enough to exhaust the
                // max_fields window in one page.
                var last = ComponentsTools.Get(
                    "{\"instance_id\":" + InstanceId.Of(go) +
                    ",\"type_name\":\"UnityEngine.Transform\",\"max_fields\":2,\"page_size\":10,\"include_properties\":false}");
                Assert.IsTrue(last.Success, last.ErrorMessage);
                StringAssert.Contains("\"next_cursor\":null", last.Output);
                // pagination.truncated is 0 (no entries after the page), even
                // though top-level truncated may be >0 (fields hidden by cap).
                StringAssert.Contains("\"pagination\":", last.Output);
                // The pagination block's truncated must be 0, distinct from
                // the top-level truncated which reflects the max_fields cap.
                Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(
                    last.Output, "\"pagination\":\\{[^}]*\"truncated\":0[^}]*\\}"),
                    "pagination.truncated must be 0 at the last page. Output: " + last.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Get_PropertyPath_ReturnsScopedSlice()
        {
            var go = new GameObject("__MCPTest_Comp_Get_PropertyPath");
            try
            {
                var result = ComponentsTools.Get(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"type_name\":\"UnityEngine.Transform\",\"property_path\":\"m_LocalPosition\"}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"property_path\":\"m_LocalPosition\"", result.Output);
                StringAssert.Contains("\"m_LocalPosition\"", result.Output);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Get_PropertyPath_NotFound_ReturnsError()
        {
            var go = new GameObject("__MCPTest_Comp_Get_PropertyPathMissing");
            try
            {
                var result = ComponentsTools.Get(
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"type_name\":\"UnityEngine.Transform\",\"property_path\":\"__NoSuchPath__\"}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("property_not_found", result.ErrorCode);
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
                    "{\"instance_id\":" +InstanceId.Of(go) +
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
                    "{\"instance_id\":" +InstanceId.Of(go) +
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
                    "{\"instance_id\":" +InstanceId.Of(go) +
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
                    "{\"instance_id\":" +InstanceId.Of(go) +
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

        // Regression for the discovery gap where query:"Rigidbody" / "Collider"
        // returned zero types even though those components ARE attachable.
        // Rigidbody + the built-in Colliders live in UnityEngine.PhysicsModule,
        // which the old 2-assembly scan never reached. The scan now walks every
        // loaded UnityEngine.* module assembly, so a query must surface them.
        [Test]
        public void ListAll_Query_RigidbodyDiscoverable()
        {
            var result = ComponentsTools.ListAll(
                "{\"query\":\"Rigidbody\",\"max_results\":200}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"UnityEngine.Rigidbody\"", result.Output);
        }

        [Test]
        public void ListAll_Query_BuiltinCollidersDiscoverable()
        {
            var result = ComponentsTools.ListAll(
                "{\"query\":\"Collider\",\"max_results\":200}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"UnityEngine.BoxCollider\"", result.Output);
            StringAssert.Contains("\"UnityEngine.SphereCollider\"", result.Output);
            StringAssert.Contains("\"UnityEngine.MeshCollider\"", result.Output);
        }

        [Test]
        public void ListAll_BuiltinOnly_ClassifiesAsBuiltin()
        {
            var result = ComponentsTools.ListAll(
                "{\"query\":\"Rigidbody\",\"include_project\":false}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            // Rigidbody lives in UnityEngine.PhysicsModule, a UnityEngine.*
            // module assembly — its entry must carry builtin:true. (Fields are
            // not adjacent in the object, so assert each fact independently.)
            StringAssert.Contains("\"fullName\":\"UnityEngine.Rigidbody\"", result.Output);
            StringAssert.Contains("\"assembly\":\"UnityEngine.PhysicsModule\"", result.Output);
            StringAssert.Contains("\"builtin\":true", result.Output);
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

        // ===================== M30-polish Plan 4 — Undo / lifecycle =====================

        // T4.1 — component_modify must write via ApplyModifiedProperties (Undo-
        // backed) so a single Ctrl+Z reverts the patch. Before the fix it used
        // ApplyModifiedPropertiesWithoutUndo — the only typed mutation surface
        // that skipped Undo.
        [Test]
        public void Modify_IsUndoable_PerformUndoRevertsPatch()
        {
            var go = new GameObject("__MCPTest_Comp_Modify_Undo");
            try
            {
                var result = ComponentsTools.Modify(
                    "{\"instance_id\":" + InstanceId.Of(go) +
                    ",\"type_name\":\"UnityEngine.Transform\"," +
                    "\"fields\":[{\"path\":\"m_LocalPosition\",\"value\":[5,6,7]}]}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual(new Vector3(5, 6, 7), go.transform.localPosition);

                Undo.PerformUndo();

                // One undo step reverts the component_modify patch to the
                // pre-patch value (Vector3.zero for a fresh GameObject).
                Assert.AreEqual(Vector3.zero, go.transform.localPosition,
                    "Undo.PerformUndo must revert the component_modify patch.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // T4.2 — heavy component_get loop must complete without error. The
        // native SerializedFile handle is freed via `using` on each call; before
        // the fix it leaked, and a long session could exhaust serialization
        // slots. This regression test catches disposal regressions indirectly
        // (a leaked/disposed handle surfaces as an exception or malformed output).
        [Test]
        public void Get_HeavyLoop_CompletesWithoutError()
        {
            var go = new GameObject("__MCPTest_Comp_Get_Heavy");
            go.AddComponent<Rigidbody>();
            try
            {
                for (int i = 0; i < 50; i++)
                {
                    var result = ComponentsTools.Get(
                        "{\"instance_id\":" + InstanceId.Of(go) +
                        ",\"type_name\":\"UnityEngine.Rigidbody\"}");
                    Assert.IsTrue(result.Success, result.ErrorMessage);
                    Assert.IsTrue(BridgeJson.IsValidJsonObject(result.Output),
                        $"Iteration {i} produced malformed JSON — likely a leaked/disposed SerializedObject handle.");
                }
            }
            finally { Object.DestroyImmediate(go); }
        }

        // T4.6 — component_modify enum-by-index must reject out-of-range ints.
        // Before the fix Unity's setter accepted any int silently, producing
        // garbage on the next serialization.
        [Test]
        public void Modify_EnumIndexOutOfRange_ReportsErrorNoMutation()
        {
            var go = new GameObject("__MCPTest_Comp_Modify_EnumRange");
            var rb = go.AddComponent<Rigidbody>();
            // Rigidbody.interpolation is an enum (RigidbodyInterpolation):
            // None=0, Interpolate=1, Extrapolate=2. Set a known-good value first.
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            try
            {
                // 42 is out of range for a 3-value enum.
                var result = ComponentsTools.Modify(
                    "{\"instance_id\":" + InstanceId.Of(go) +
                    ",\"type_name\":\"UnityEngine.Rigidbody\"," +
                    "\"fields\":[{\"path\":\"m_Interpolation\",\"value\":42}]}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"errors\":", result.Output);
                // The invalid index must NOT have mutated the field.
                Assert.AreEqual(RigidbodyInterpolation.Interpolate, rb.interpolation,
                    "Out-of-range enum index must not mutate the field.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // T4.6 — component_modify enum-by-name (type:"name") already validated;
        // this asserts the name path still works and the index path accepts a
        // valid index.
        [Test]
        public void Modify_EnumValidIndex_SetsValue()
        {
            var go = new GameObject("__MCPTest_Comp_Modify_EnumValid");
            var rb = go.AddComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.None;
            try
            {
                // Index 2 == Extrapolate.
                var result = ComponentsTools.Modify(
                    "{\"instance_id\":" + InstanceId.Of(go) +
                    ",\"type_name\":\"UnityEngine.Rigidbody\"," +
                    "\"fields\":[{\"path\":\"m_Interpolation\",\"value\":2}]}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual(RigidbodyInterpolation.Extrapolate, rb.interpolation);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
