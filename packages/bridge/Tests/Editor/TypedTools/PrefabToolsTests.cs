// Deliberate use of deprecated GetInstanceID() — see docs/code-conventions.md §Instance IDs.
#pragma warning disable CS0618
// EditMode tests for the M16 Plan 1 typed prefab tools (PrefabTools).
// Covers parameter parsing and resolver branches that do NOT drive Unity
// prefab mutating APIs (no scene is loaded in EditMode by default).
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    public class PrefabToolsTests
    {
        [Test]
        public void Instantiate_MissingPath_ReturnsMissingParameter()
        {
            var result = PrefabTools.Instantiate("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'prefab_asset_path'", result.ErrorMessage);
        }

        [Test]
        public void Instantiate_NonExistentPrefab_ReturnsNotFound()
        {
            var result = PrefabTools.Instantiate(
                "{\"prefab_asset_path\":\"Assets/__Nope.prefab\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("prefab_not_found", result.ErrorCode);
        }

        [Test]
        public void Create_MissingPath_ReturnsMissingParameter()
        {
            var result = PrefabTools.Create("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Create_BadPathFormat_ReturnsInvalidPaths()
        {
            var result = PrefabTools.Create(
                "{\"prefab_asset_path\":\"Materials/Foo.mat\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_paths", result.ErrorCode);
        }

        [Test]
        public void Create_NoTargetFound_ReturnsGameObjectNotFound()
        {
            var result = PrefabTools.Create(
                "{\"prefab_asset_path\":\"Assets/__MCPTest.prefab\",\"name\":\"__Nope__\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("gameobject_not_found", result.ErrorCode);
        }

        [Test]
        public void Open_MissingPath_ReturnsMissingParameter()
        {
            var result = PrefabTools.Open("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Close_NoStageOpen_ReturnsNoop()
        {
            // No prefab stage is open in EditMode.
            var result = PrefabTools.Close("{}");
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"status\":\"noop\"", result.Output);
        }

        [Test]
        public void Save_NoStageOpen_ReturnsNoop()
        {
            var result = PrefabTools.Save("{}");
            Assert.IsTrue(result.Success);
            StringAssert.Contains("\"status\":\"noop\"", result.Output);
        }

        [Test]
        public void ResolveInstance_NotFound_ReturnsGameObjectNotFound()
        {
            var r = PrefabTools.ResolveInstance(
                "{\"path\":\"__Nope__\"}");
            Assert.IsFalse(r.Ok);
            Assert.AreEqual("gameobject_not_found", r.Result.ErrorCode);
        }

        [Test]
        public void ResolveInstance_FindsNonPrefabGameObject_ReturnsNotAPrefab()
        {
            // A fresh GameObject in the active scene is not a prefab.
            var go = new GameObject("__MCPTest_Plain");
            try
            {
                var r = PrefabTools.ResolveInstance(
                    "{\"instance_id\":" + go.GetInstanceID() + "}");
                Assert.IsFalse(r.Ok);
                Assert.AreEqual("not_a_prefab", r.Result.ErrorCode);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ParseVector_Valid_Parses()
        {
            Assert.AreEqual(new Vector3(1, 2, 3), PrefabTools.ParseVector("1,2,3", Vector3.zero));
            Assert.AreEqual(new Vector3(-1.5f, 0, 2), PrefabTools.ParseVector("-1.5,0,2", Vector3.zero));
        }

        [Test]
        public void ParseVector_Invalid_ReturnsFallback()
        {
            Assert.AreEqual(Vector3.one, PrefabTools.ParseVector(null, Vector3.one));
            Assert.AreEqual(Vector3.one, PrefabTools.ParseVector("not-a-vector", Vector3.one));
            Assert.AreEqual(Vector3.one, PrefabTools.ParseVector("1,2", Vector3.one));
        }
    }
}
