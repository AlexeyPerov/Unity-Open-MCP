using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    public static class JsonBodyTests
    {
        [Test]
        public static void GetString_ExtractsValue()
        {
            var json = "{\"code\":\"return 1;\",\"timeout_ms\":5000}";
            Assert.AreEqual("return 1;", JsonBody.GetString(json, "code"));
        }

        [Test]
        public static void GetString_MissingKey_ReturnsNull()
        {
            var json = "{\"code\":\"return 1;\"}";
            Assert.IsNull(JsonBody.GetString(json, "usings"));
        }

        [Test]
        public static void GetString_NullValue_ReturnsNull()
        {
            var json = "{\"code\":null}";
            Assert.IsNull(JsonBody.GetString(json, "code"));
        }

        [Test]
        public static void GetString_EscapedCharacters()
        {
            var json = "{\"path\":\"Assets/My\\\"File.prefab\"}";
            Assert.AreEqual("Assets/My\"File.prefab", JsonBody.GetString(json, "path"));
        }

        [Test]
        public static void GetStringArray_ExtractsArray()
        {
            var json = "{\"usings\":[\"System\",\"System.IO\"],\"code\":\"x\"}";
            var arr = JsonBody.GetStringArray(json, "usings");
            Assert.IsNotNull(arr);
            Assert.AreEqual(2, arr.Length);
            Assert.AreEqual("System", arr[0]);
            Assert.AreEqual("System.IO", arr[1]);
        }

        [Test]
        public static void GetStringArray_EmptyArray_ReturnsEmpty()
        {
            var json = "{\"paths_hint\":[],\"code\":\"x\"}";
            var arr = JsonBody.GetStringArray(json, "paths_hint");
            Assert.IsNotNull(arr);
            Assert.AreEqual(0, arr.Length);
        }

        [Test]
        public static void GetStringArray_MissingKey_ReturnsNull()
        {
            var json = "{\"code\":\"x\"}";
            Assert.IsNull(JsonBody.GetStringArray(json, "paths_hint"));
        }

        [Test]
        public static void GetBool_True()
        {
            var json = "{\"is_static\":true}";
            Assert.IsTrue(JsonBody.GetBool(json, "is_static", false));
        }

        [Test]
        public static void GetBool_False()
        {
            var json = "{\"is_static\":false}";
            Assert.IsFalse(JsonBody.GetBool(json, "is_static", true));
        }

        [Test]
        public static void GetBool_MissingKey_ReturnsDefault()
        {
            var json = "{}";
            Assert.IsTrue(JsonBody.GetBool(json, "is_static", true));
        }

        [Test]
        public static void GetInt_ExtractsValue()
        {
            var json = "{\"timeout_ms\":5000}";
            Assert.AreEqual(5000, JsonBody.GetInt(json, "timeout_ms", 0));
        }

        [Test]
        public static void GetInt_MissingKey_ReturnsDefault()
        {
            var json = "{}";
            Assert.AreEqual(30000, JsonBody.GetInt(json, "timeout_ms", 30000));
        }

        [Test]
        public static void GetRawValue_Object()
        {
            var json = "{\"gate\":{\"mode\":\"enforce\"}}";
            var raw = JsonBody.GetRawValue(json, "gate");
            Assert.IsNotNull(raw);
            Assert.IsTrue(raw.Contains("\"mode\""));
        }

        [Test]
        public static void GetStringArray_SingleElement()
        {
            var json = "{\"paths_hint\":[\"Assets/Test.prefab\"]}";
            var arr = JsonBody.GetStringArray(json, "paths_hint");
            Assert.AreEqual(1, arr.Length);
            Assert.AreEqual("Assets/Test.prefab", arr[0]);
        }

        [Test]
        public static void GetString_EmptyBody_ReturnsNull()
        {
            Assert.IsNull(JsonBody.GetString("", "code"));
            Assert.IsNull(JsonBody.GetString(null, "code"));
        }
    }
}
