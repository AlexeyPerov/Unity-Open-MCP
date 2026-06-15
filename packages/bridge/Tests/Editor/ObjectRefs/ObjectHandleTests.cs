using NUnit.Framework;
using UnityOpenMcpBridge.ObjectRefs;
using UnityEngine;

namespace UnityOpenMcpBridge.Tests
{
    public class ObjectHandleTests
    {
        [Test]
        public static void Serialize_GameObject_IncludesObjectIdTypeNameAndPath()
        {
            var go = new GameObject("TestHandle_GO_Root");
            go.name = "TestHandle_GO_Root";

            try
            {
                var json = ObjectHandle.Serialize(go);

                Assert.That(json, Does.Contain("\"objectId\""), "Must include objectId");
                Assert.That(json, Does.Contain("\"type\":\"UnityEngine.GameObject\""), "Must include type");
                Assert.That(json, Does.Contain("\"name\":\"TestHandle_GO_Root\""), "Must include name");
                Assert.That(json, Does.Contain("\"path\""), "GameObject must include hierarchy path");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public static void Serialize_Component_IncludesGameObjectLocators()
        {
            var go = new GameObject("TestHandle_Comp_GO");
            var cam = go.AddComponent<Camera>();

            try
            {
                var json = ObjectHandle.Serialize(cam);

                Assert.That(json, Does.Contain("\"objectId\""), "Must include objectId");
                Assert.That(json, Does.Contain("\"type\":\"UnityEngine.Camera\""), "Must include component type");
                Assert.That(json, Does.Contain("\"gameObjectPath\""), "Component must include parent GameObject path");
                Assert.That(json, Does.Contain("\"gameObjectId\""), "Component must include parent GameObject instance ID");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public static void Serialize_Null_ReturnsNullLiteral()
        {
            Assert.AreEqual("null", ObjectHandle.Serialize(null));
        }

        [Test]
        public static void Resolve_ByInstanceId_ReturnsLiveObject()
        {
            var go = new GameObject("TestHandle_Resolve_GO");
            try
            {
                var instanceId = go.GetInstanceID();
                var resolved = ObjectHandle.Resolve(
                    instanceId, "UnityEngine.GameObject", go.name, null, null, null,
                    null, 0, out var error);

                Assert.IsNotNull(resolved, "Should resolve by instance ID");
                Assert.IsNull(error, "No error expected on successful resolution");
                Assert.AreSame(go, resolved, "Should return the same live object");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public static void Resolve_StaleInstanceId_ReturnsErrorGuidance()
        {
            // Use an instance ID that does not exist.
            var resolved = ObjectHandle.Resolve(
                999999999, "UnityEngine.GameObject", null, null, null, null,
                null, 0, out var error);

            Assert.IsNull(resolved, "Should not resolve a non-existent instance ID");
            Assert.IsNotNull(error, "Must provide error guidance for stale handles");
            StringAssert.Contains("stale", error.ToLower(), "Error should mention staleness");
            StringAssert.Contains("domain reload", error, "Error should explain domain reload");
            StringAssert.Contains("re-acquire", error.ToLower(), "Error should guide re-acquisition");
        }

        [Test]
        public static void Resolve_FallsBackToName_WhenInstanceIdStale()
        {
            var go = new GameObject("TestHandle_Fallback_GO");
            try
            {
                // Use a stale instance ID but provide the name as fallback.
                var resolved = ObjectHandle.Resolve(
                    999999999, "UnityEngine.GameObject", "TestHandle_Fallback_GO", null, null, null,
                    null, 0, out var error);

                Assert.IsNotNull(resolved, "Should fall back to name lookup");
                Assert.IsNull(error, "No error when a fallback succeeds");
                Assert.AreSame(go, resolved);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public static void ResolveJson_BareInteger_TreatsAsInstanceId()
        {
            var go = new GameObject("TestHandle_BareInt_GO");
            try
            {
                var id = go.GetInstanceID().ToString();
                var resolved = ObjectHandle.ResolveJson(id, out var error);

                Assert.IsNotNull(resolved, "Bare integer should be treated as instance ID");
                Assert.IsNull(error);
                Assert.AreSame(go, resolved);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public static void ResolveJson_FullHandle_RoundTrips()
        {
            var go = new GameObject("TestHandle_RoundTrip_GO");
            try
            {
                var json = ObjectHandle.Serialize(go);
                var resolved = ObjectHandle.ResolveJson(json, out var error);

                Assert.IsNotNull(resolved, "Full handle should round-trip back to live object");
                Assert.IsNull(error);
                Assert.AreSame(go, resolved);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public static void ResolveJson_NullString_ReturnsError()
        {
            var resolved = ObjectHandle.ResolveJson("null", out var error);
            Assert.IsNull(resolved);
            Assert.IsNotNull(error);
        }

        [Test]
        public static void LooksLikeHandle_DetectsHandleJson()
        {
            Assert.IsTrue(ObjectHandle.LooksLikeHandle("{\"objectId\":12345,\"type\":\"UnityEngine.GameObject\"}"));
            Assert.IsFalse(ObjectHandle.LooksLikeHandle("12345"));
            Assert.IsFalse(ObjectHandle.LooksLikeHandle("hello"));
            Assert.IsFalse(ObjectHandle.LooksLikeHandle(null));
        }

        [Test]
        public static void Resolve_FallsBackToPath_WhenInstanceIdStale()
        {
            var parent = new GameObject("TestHandle_Path_Root");
            var child = new GameObject("TestHandle_Path_Child");
            child.transform.SetParent(parent.transform);

            try
            {
                var resolved = ObjectHandle.Resolve(
                    999999999, "UnityEngine.GameObject", null,
                    "TestHandle_Path_Root/TestHandle_Path_Child",
                    null, null, null, 0, out var error);

                Assert.IsNotNull(resolved, "Should fall back to path lookup");
                Assert.IsNull(error);
                Assert.AreSame(child, resolved);
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public static void Resolve_ComponentFallback_FindsComponentOnParent()
        {
            var go = new GameObject("TestHandle_CompFallback_GO");
            var cam = go.AddComponent<Camera>();

            try
            {
                // Stale component instance ID, but provide the parent GameObject path.
                var resolved = ObjectHandle.Resolve(
                    999999999, "UnityEngine.Camera", null, null, null, null,
                    "TestHandle_CompFallback_GO", 0, out var error);

                Assert.IsNotNull(resolved, "Should find component via parent path fallback");
                Assert.IsNull(error);
                Assert.AreSame(cam, resolved);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
