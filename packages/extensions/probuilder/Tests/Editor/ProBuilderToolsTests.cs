// Deliberate use of deprecated GetInstanceID() / EditorUtility.InstanceIDToObject() — see docs/code-conventions.md §Instance IDs.
#pragma warning disable CS0618
// EditMode tests for the ProBuilder extension pack.
//
// Covers the deterministic contracts that protect the agent surface:
//
//   1. All 5 catalog tools are discovered by BridgeToolRegistry (no core
//      bridge edits — proves the [BridgeToolType] assembly scan works for
//      packs).
//   2. Mutating tools refuse to run without paths_hint (the gate contract).
//   3. delete_faces is flagged DestructiveHint (MCP clients can prompt).
//   4. Shape create → get_mesh_info → extrude by direction round-trip on a
//      Cube primitive.
//   5. Face-index errors return structured JSON errors (no thrown exceptions).
//
// These tests run in EditMode against the live demo project (ProBuilder
// package must be installed). They clean up after themselves.
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityOpenMcpBridge;
using UnityOpenMcpExtensions.ProBuilder;

namespace UnityOpenMcpExtensions.ProBuilder.Tests
{
    public class ProBuilderToolsTests
    {
        // The 5 catalog tool ids this pack must register.
        static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_probuilder_create_shape",
            "unity_open_mcp_probuilder_get_mesh_info",
            "unity_open_mcp_probuilder_extrude",
            "unity_open_mcp_probuilder_delete_faces",
            "unity_open_mcp_probuilder_set_face_material",
        };

        [Test]
        public void Registry_AllFiveToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected probuilder tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_CreateShapeIsMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_probuilder_create_shape", out var create));
            Assert.IsTrue(create.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, create.Lifecycle);
        }

        [Test]
        public void Registry_GetMeshInfoIsReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_probuilder_get_mesh_info", out var get));
            Assert.IsFalse(get.IsMutating);
            Assert.IsTrue(get.ReadOnlyHint);
        }

        [Test]
        public void Registry_DeleteFacesIsDestructive()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_probuilder_delete_faces", out var del));
            Assert.IsTrue(del.IsMutating);
            Assert.IsTrue(del.DestructiveHint,
                "delete_faces is destructive — DestructiveHint must be true so MCP clients can prompt.");
        }

        // -----------------------------------------------------------------
        // paths_hint contract — every mutating tool refuses empty scope.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_CreateShape_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_probuilder_create_shape",
                "{\"shape_type\":\"Cube\"}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("paths_hint_required", result.ErrorCode);
        }

        [Test]
        public void Dispatch_Extrude_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_probuilder_extrude",
                "{\"face_direction\":\"up\"}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("paths_hint_required", result.ErrorCode);
        }

        // -----------------------------------------------------------------
        // Validation branches (no scene mutation — pure validation).
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_CreateShape_InvalidShapeType_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_probuilder_create_shape",
                "{\"shape_type\":\"NotAShape\",\"paths_hint\":[\"Assets/Foo.unity\"]}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_shape_type", result.ErrorCode);
        }

        // -----------------------------------------------------------------
        // Shape create + get_mesh_info round-trip on a Cube.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_CreateCube_GetMeshInfo_ReportsCounts()
        {
            GameObject created = null;
            try
            {
                var createBody = "{\"shape_type\":\"Cube\",\"name\":\"PBTestCube\"," +
                                 "\"paths_hint\":[\"Assets/PBTest.unity\"]}";
                var create = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_probuilder_create_shape", createBody);
                Assert.IsNotNull(create);
                Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);
                StringAssert.Contains("\"shapeType\":\"Cube\"", create.Output);
                StringAssert.Contains("\"faceCount\":6", create.Output);

                // Pull the instance id out of the response and get_mesh_info.
                var id = ExtractInt(create.Output, "instanceId");
                created = EditorUtility.InstanceIDToObject(id) as GameObject;
                Assert.IsNotNull(created, "Created GameObject must resolve by instance id.");
                Assert.IsNotNull(created.GetComponent<ProBuilderMesh>());

                var getBody = "{\"instance_id\":" + id + "}";
                var info = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_probuilder_get_mesh_info", getBody);
                Assert.IsTrue(info.Success, info.ErrorMessage ?? info.Output);
                StringAssert.Contains("\"faceCount\":6", info.Output);
                StringAssert.Contains("\"faceDirections\":{", info.Output);
                StringAssert.Contains("\"up\":[", info.Output);
            }
            finally
            {
                if (created != null) Object.DestroyImmediate(created);
            }
        }

        [Test]
        public void RoundTrip_ExtrudeTopFaceByDirection_IncreasesFaceCount()
        {
            GameObject created = null;
            try
            {
                var create = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_probuilder_create_shape",
                    "{\"shape_type\":\"Cube\",\"paths_hint\":[\"Assets/PBTest.unity\"]}");
                Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);
                var id = ExtractInt(create.Output, "instanceId");
                created = EditorUtility.InstanceIDToObject(id) as GameObject;
                Assert.IsNotNull(created);
                var mesh = created.GetComponent<ProBuilderMesh>();
                int originalFaceCount = mesh.faceCount;

                var extrudeBody = "{\"instance_id\":" + id + "," +
                                  "\"face_direction\":\"up\",\"distance\":1.0," +
                                  "\"paths_hint\":[\"Assets/PBTest.unity\"]}";
                var extrude = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_probuilder_extrude", extrudeBody);
                Assert.IsTrue(extrude.Success, extrude.ErrorMessage ?? extrude.Output);
                StringAssert.Contains("\"selectionMethod\":\"by direction 'up'\"", extrude.Output);
                StringAssert.Contains("\"newFacesCreated\":", extrude.Output);
                Assert.Greater(mesh.faceCount, originalFaceCount,
                    "Extrude must produce new faces.");
            }
            finally
            {
                if (created != null) Object.DestroyImmediate(created);
            }
        }

        [Test]
        public void RoundTrip_DeleteFacesByDirection_RemovesFaces()
        {
            GameObject created = null;
            try
            {
                var create = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_probuilder_create_shape",
                    "{\"shape_type\":\"Cube\",\"paths_hint\":[\"Assets/PBTest.unity\"]}");
                Assert.IsTrue(create.Success);
                var id = ExtractInt(create.Output, "instanceId");
                created = EditorUtility.InstanceIDToObject(id) as GameObject;
                Assert.IsNotNull(created);
                var mesh = created.GetComponent<ProBuilderMesh>();
                int originalFaceCount = mesh.faceCount; // 6 for a Cube

                var deleteBody = "{\"instance_id\":" + id + "," +
                                 "\"face_direction\":\"down\"," +
                                 "\"paths_hint\":[\"Assets/PBTest.unity\"]}";
                var del = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_probuilder_delete_faces", deleteBody);
                Assert.IsTrue(del.Success, del.ErrorMessage ?? del.Output);
                StringAssert.Contains("\"selectionMethod\":\"by direction 'down'\"", del.Output);
                Assert.Less(mesh.faceCount, originalFaceCount,
                    "Delete must remove at least one face.");
            }
            finally
            {
                if (created != null) Object.DestroyImmediate(created);
            }
        }

        // -----------------------------------------------------------------
        // Structured errors (no thrown exceptions to MCP).
        // -----------------------------------------------------------------

        [Test]
        public void Extrude_InvalidFaceIndex_ReturnsStructuredError()
        {
            GameObject created = null;
            try
            {
                var create = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_probuilder_create_shape",
                    "{\"shape_type\":\"Cube\",\"paths_hint\":[\"Assets/PBTest.unity\"]}");
                Assert.IsTrue(create.Success);
                var id = ExtractInt(create.Output, "instanceId");
                created = EditorUtility.InstanceIDToObject(id) as GameObject;

                var extrudeBody = "{\"instance_id\":" + id + "," +
                                  "\"face_indices\":[999]," +
                                  "\"paths_hint\":[\"Assets/PBTest.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_probuilder_extrude", extrudeBody);
                Assert.IsFalse(result.Success);
                Assert.AreEqual("invalid_face_indices", result.ErrorCode);
                StringAssert.Contains("999", result.ErrorMessage ?? "");
            }
            finally
            {
                if (created != null) Object.DestroyImmediate(created);
            }
        }

        [Test]
        public void Extrude_NeitherIndicesNorDirection_ReturnsMissingParameter()
        {
            GameObject created = null;
            try
            {
                var create = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_probuilder_create_shape",
                    "{\"shape_type\":\"Cube\",\"paths_hint\":[\"Assets/PBTest.unity\"]}");
                Assert.IsTrue(create.Success);
                var id = ExtractInt(create.Output, "instanceId");
                created = EditorUtility.InstanceIDToObject(id) as GameObject;

                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_probuilder_extrude",
                    "{\"instance_id\":" + id + ",\"paths_hint\":[\"Assets/PBTest.unity\"]}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("missing_parameter", result.ErrorCode);
            }
            finally
            {
                if (created != null) Object.DestroyImmediate(created);
            }
        }

        [Test]
        public void Extrude_BothIndicesAndDirection_ReturnsConflictingSelection()
        {
            GameObject created = null;
            try
            {
                var create = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_probuilder_create_shape",
                    "{\"shape_type\":\"Cube\",\"paths_hint\":[\"Assets/PBTest.unity\"]}");
                Assert.IsTrue(create.Success);
                var id = ExtractInt(create.Output, "instanceId");
                created = EditorUtility.InstanceIDToObject(id) as GameObject;

                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_probuilder_extrude",
                    "{\"instance_id\":" + id + ",\"face_indices\":[0]," +
                    "\"face_direction\":\"up\",\"paths_hint\":[\"Assets/PBTest.unity\"]}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("conflicting_selection", result.ErrorCode);
            }
            finally
            {
                if (created != null) Object.DestroyImmediate(created);
            }
        }

        [Test]
        public void GetMeshInfo_OnTargetWithoutProBuilder_ReturnsComponentNotFound()
        {
            var go = new GameObject("NoMeshHost");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_probuilder_get_mesh_info",
                    "{\"instance_id\":" + go.GetInstanceID() + "}");
                Assert.IsFalse(result.Success);
                Assert.AreEqual("component_not_found", result.ErrorCode);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void GetMeshInfo_OnUnknownTarget_ReturnsTargetNotFound()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_probuilder_get_mesh_info",
                "{\"name\":\"__nonexistent_probuilder_target__\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("target_not_found", result.ErrorCode);
        }

        // -----------------------------------------------------------------
        // Minimal JSON int extractor — pulls "instanceId":<int> out of the
        // hand-rolled JSON envelopes the tools return.
        // -----------------------------------------------------------------
        static int ExtractInt(string json, string key)
        {
            var pattern = "\"" + key + "\":";
            var idx = json.IndexOf(pattern, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(idx, 0, $"Expected key '{key}' in response: {json}");
            var start = idx + pattern.Length;
            var end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            return int.Parse(json.Substring(start, end - start));
        }
    }
}
