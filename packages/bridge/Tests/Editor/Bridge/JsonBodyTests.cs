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

        // ----- HasKey / TryGetString: absent vs explicit-null distinction -----
        //
        // GetString collapses "key absent" and `"key": null` into one null
        // return. Callers with a precedence chain (e.g. name_target → name)
        // need to tell them apart so an explicit null does not fall through
        // to the secondary key. HasKey / TryGetString expose the distinction.

        [Test]
        public static void HasKey_PresentValue_ReturnsTrue()
        {
            var json = "{\"code\":\"x\",\"timeout_ms\":5}";
            Assert.IsTrue(JsonBody.HasKey(json, "code"));
            Assert.IsTrue(JsonBody.HasKey(json, "timeout_ms"));
        }

        [Test]
        public static void HasKey_ExplicitNull_ReturnsTrue()
        {
            // The load-bearing case: "key": null is present even though
            // GetString returns null for it.
            Assert.IsTrue(JsonBody.HasKey("{\"code\":null}", "code"));
        }

        [Test]
        public static void HasKey_MissingKey_ReturnsFalse()
        {
            Assert.IsFalse(JsonBody.HasKey("{\"code\":\"x\"}", "usings"));
            Assert.IsFalse(JsonBody.HasKey("{}", "code"));
            Assert.IsFalse(JsonBody.HasKey("", "code"));
            Assert.IsFalse(JsonBody.HasKey(null, "code"));
        }

        // ----- HasKeyAndNotNull (B-N23) -----
        //
        // The "field provided with a real value" predicate: false for both a
        // missing key and an explicit null, true only when a non-null value is
        // present. Used by gameobject_set_parent so `"parent_path": null` (the
        // common LLM shape for "not specified") does not trigger a detach.

        [Test]
        public static void HasKeyAndNotNull_PresentValue_ReturnsTrue()
        {
            Assert.IsTrue(JsonBody.HasKeyAndNotNull("{\"code\":\"x\"}", "code"));
            // An empty string IS a real value (the explicit-detach form for
            // parent_path), so it must report true.
            Assert.IsTrue(JsonBody.HasKeyAndNotNull("{\"parent_path\":\"\"}", "parent_path"));
        }

        [Test]
        public static void HasKeyAndNotNull_ExplicitNull_ReturnsFalse()
        {
            Assert.IsFalse(JsonBody.HasKeyAndNotNull("{\"parent_path\":null}", "parent_path"));
        }

        [Test]
        public static void HasKeyAndNotNull_MissingKey_ReturnsFalse()
        {
            Assert.IsFalse(JsonBody.HasKeyAndNotNull("{\"code\":\"x\"}", "parent_path"));
            Assert.IsFalse(JsonBody.HasKeyAndNotNull("{}", "parent_path"));
            Assert.IsFalse(JsonBody.HasKeyAndNotNull("", "parent_path"));
            Assert.IsFalse(JsonBody.HasKeyAndNotNull(null, "parent_path"));
        }

        [Test]
        public static void TryGetString_PresentString_ReturnsValueAndPresentTrue()
        {
            var value = JsonBody.TryGetString("{\"name_target\":\"Cube\"}", "name_target", out var present);
            Assert.IsTrue(present);
            Assert.AreEqual("Cube", value);
        }

        [Test]
        public static void TryGetString_ExplicitNull_ReturnsNullAndPresentTrue()
        {
            // name_target: null — present is true so the caller does NOT fall
            // through to the secondary `name` key.
            var value = JsonBody.TryGetString("{\"name_target\":null}", "name_target", out var present);
            Assert.IsTrue(present);
            Assert.IsNull(value);
        }

        [Test]
        public static void TryGetString_AbsentKey_ReturnsNullAndPresentFalse()
        {
            // name_target omitted — present is false so the caller DOES fall
            // through to the secondary `name` key.
            var value = JsonBody.TryGetString("{\"name\":\"Cube\"}", "name_target", out var present);
            Assert.IsFalse(present);
            Assert.IsNull(value);
        }

        [Test]
        public static void GetLong_ParsesLargeValue()
        {
            var json = "{\"startedAtMs\":1718000000000}";
            Assert.AreEqual(1718000000000L, JsonBody.GetLong(json, "startedAtMs"));
        }

        [Test]
        public static void GetLong_ReturnsDefault_WhenMissing()
        {
            Assert.AreEqual(300_000L, JsonBody.GetLong("{}", "startedAtMs", 300_000L));
        }

        [Test]
        public static void GetObjectArray_ReturnsEachObjectElement()
        {
            var json = "{\"errors\":[{\"code\":\"CS0246\",\"line\":10},{\"code\":\"CS0103\",\"line\":20}]}";
            var items = JsonBody.GetObjectArray(json, "errors");
            Assert.IsNotNull(items);
            Assert.AreEqual(2, items.Length);
            // Each returned string is the inner object text — fields parse back.
            Assert.AreEqual("CS0246", JsonBody.GetString(items[0], "code"));
            Assert.AreEqual(10, JsonBody.GetInt(items[0], "line"));
            Assert.AreEqual("CS0103", JsonBody.GetString(items[1], "code"));
            Assert.AreEqual(20, JsonBody.GetInt(items[1], "line"));
        }

        [Test]
        public static void GetObjectArray_HandlesNestedBracesInStrings()
        {
            // A brace inside a string value must not confuse the brace-depth
            // scanner. The message below contains a literal '}' inside quotes.
            var json = "{\"errors\":[{\"message\":\"unexpected } token\"}]}";
            var items = JsonBody.GetObjectArray(json, "errors");
            Assert.IsNotNull(items);
            Assert.AreEqual(1, items.Length);
            Assert.AreEqual("unexpected } token", JsonBody.GetString(items[0], "message"));
        }

        [Test]
        public static void GetObjectArray_ReturnsNull_WhenNotArray()
        {
            Assert.IsNull(JsonBody.GetObjectArray("{\"errors\":42}", "errors"));
            Assert.IsNull(JsonBody.GetObjectArray("{\"errors\":null}", "errors"));
            Assert.IsNull(JsonBody.GetObjectArray("{}", "errors"));
        }

        // feedback-fable-31-07 §2 — SelectorScope trims the body at the first
        // patch-array key (fields/entries/patches/...) so selector reads never
        // see the same key names nested inside a patch value.
        [Test]
        public static void SelectorScope_TrimsBeforeFieldsArray()
        {
            // A top-level instance_id of 111 and a NESTED fields[].value
            // .instance_id of 999. SelectorScope must cut before "fields" so a
            // GetLongFlexible on the scope reads 111, not 999.
            var body = "{\"instance_id\":111,\"type_name\":\"Foo\","
                + "\"fields\":[{\"name\":\"a\",\"value\":{\"instance_id\":999}}]}";
            var scope = JsonBody.SelectorScope(body);
            Assert.AreEqual(111L, JsonBody.GetLongFlexible(scope, "instance_id", 0),
                "scoped selector must read the TOP-LEVEL instance_id, not the nested one");
            Assert.AreEqual("Foo", JsonBody.GetString(scope, "type_name"));
        }

        [Test]
        public static void SelectorScope_ReturnsBodyUnchanged_WhenNoPatchArray()
        {
            // Read-only bodies (no fields/entries/patches/...) come back whole.
            var body = "{\"instance_id\":111,\"path\":\"Root/Child\"}";
            Assert.AreEqual(body, JsonBody.SelectorScope(body));
        }

        [Test]
        public static void SelectorScope_TrimmedAtEarliestPatchArrayKey()
        {
            // Multiple reserved patch keys present — the earliest wins, so the
            // selector prefix never crosses ANY patch array.
            var body = "{\"path\":\"X\",\"patches\":[{}],\"fields\":[{}]}";
            var scope = JsonBody.SelectorScope(body);
            Assert.AreEqual("X", JsonBody.GetString(scope, "path"));
            Assert.IsNull(JsonBody.GetString(scope, "fields"));
        }
    }
}
