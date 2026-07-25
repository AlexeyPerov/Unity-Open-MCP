using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
    // ---- Helper types for reflective serialization tests ----

    public class SamplePoco
    {
        public int Number = 7;
        public string Label = "hi";
        public string Name { get; set; } = "prop";
    }

    public class Nest
    {
        public string V;
        public Nest Child;

        public override string ToString() => V;
    }

    public class Node
    {
        public string Id;
        public Node Next;
    }

    public class Throwing
    {
        public int Good = 5;
        public int Bad
        {
            get { throw new System.InvalidOperationException("boom"); }
        }
    }

    public class OnlyProps
    {
        public int Alpha { get; set; } = 1;
        public int Beta { get; set; } = 2;
    }

    public static class OutputSerializerTests
    {
        [Test]
        public static void Serialize_Null_ReturnsJsonNullLiteral()
        {
            // B3 — SerializeInternal must return the JSON token "null", not a
            // C# null reference. Composite emitters concatenate the return
            // value verbatim (`"key:" + val`), so a C# null produces `{"k":}`.
            Assert.AreEqual("null", OutputSerializer.Serialize(null));
        }

        // Regression: code-review finding B3 — a null nested inside a composite
        // (object field, dictionary value, or array element) used to emit a C#
        // null reference, which the composite emitters concatenated verbatim,
        // producing malformed JSON like `{"Name":}` or `[,1]`. The fix returns
        // the literal JSON token "null" from both null branches so the composite
        // stays valid. These cases round-trip through JsonUtility.
        public class WithNullField
        {
            public string Present = "x";
            public string Absent = null;
        }

        [Test]
        public static void Serialize_NullField_EmitsJsonNullToken()
        {
            var result = OutputSerializer.Serialize(new WithNullField(),
                new SerializeOptions { IncludeProperties = false });
            // The null field must serialize as the JSON literal `null`, not as
            // a missing value (`"Absent":}`) — the pre-fix malformed shape.
            StringAssert.Contains("\"Absent\":null", result);
            StringAssert.Contains("\"Present\":\"x\"", result);
            // The whole object must be valid JSON (no stray `:` before `}`).
            var parsed = JsonUtility.FromJson<WithNullField>(result);
            Assert.AreEqual("x", parsed.Present);
            Assert.IsNull(parsed.Absent);
        }

        [Test]
        public static void Serialize_DictionaryWithNullValue_EmitsJsonNullToken()
        {
            var dict = new Dictionary<string, object>
            {
                { "k", null },
                { "n", 1 },
            };
            var result = OutputSerializer.Serialize(dict, new SerializeOptions());
            // The null value must render as `null`, not as `"k":` (the pre-fix
            // malformed shape that broke the whole object).
            StringAssert.Contains("\"k\":null", result);
            StringAssert.Contains("\"n\":1", result);
        }

        [Test]
        public static void Serialize_ArrayWithNullElement_EmitsJsonNullToken()
        {
            var list = new List<object> { null, 1 };
            var result = OutputSerializer.Serialize(list, new SerializeOptions());
            // `[null,1]` — the null element must render as the literal token,
            // not as `[,1]` (the pre-fix malformed shape).
            StringAssert.Contains("null,1", result);
            Assert.IsFalse(result.Contains("[,"),
                "Array with a null element must not emit a leading comma: " + result);
        }

        [Test]
        public static void Serialize_String_ReturnsQuoted()
        {
            Assert.AreEqual("\"hello\"", OutputSerializer.Serialize("hello"));
        }

        [Test]
        public static void Serialize_Bool_ReturnsJsonBool()
        {
            Assert.AreEqual("true", OutputSerializer.Serialize(true));
            Assert.AreEqual("false", OutputSerializer.Serialize(false));
        }

        [Test]
        public static void Serialize_Int_ReturnsNumber()
        {
            Assert.AreEqual("42", OutputSerializer.Serialize(42));
        }

        [Test]
        public static void Serialize_Float_ReturnsInvariant()
        {
            var result = OutputSerializer.Serialize(3.14f);
            Assert.IsTrue(result.Contains("3.14"), $"Expected 3.14 in: {result}");
        }

        [Test]
        public static void Serialize_List_ReturnsArray()
        {
            var list = new List<int> { 1, 2, 3 };
            var result = OutputSerializer.Serialize(list);
            Assert.AreEqual("[1,2,3]", result);
        }

        [Test]
        public static void Serialize_Dictionary_ReturnsObject()
        {
            var dict = new Dictionary<string, int> { { "a", 1 } };
            var result = OutputSerializer.Serialize(dict);
            Assert.IsTrue(result.Contains("\"a\""));
            Assert.IsTrue(result.Contains("1"));
        }

        [Test]
        public static void EscapeJsonString_EscapesSpecialCharacters()
        {
            Assert.AreEqual("line1\\nline2", OutputSerializer.EscapeJsonString("line1\nline2"));
            Assert.AreEqual("tab\\there", OutputSerializer.EscapeJsonString("tab\there"));
            Assert.AreEqual("quote\\\"here", OutputSerializer.EscapeJsonString("quote\"here"));
            Assert.AreEqual("back\\\\slash", OutputSerializer.EscapeJsonString("back\\slash"));
        }

        [Test]
        public static void EscapeJsonString_Null_ReturnsEmpty()
        {
            Assert.AreEqual("", OutputSerializer.EscapeJsonString(null));
        }

        // ---- Depth-limited reflective walker (T1.6) ----

        [Test]
        public static void Serialize_Vector3_ReflectsFields()
        {
            var result = OutputSerializer.Serialize(new Vector3(1, 2, 3),
                new SerializeOptions { IncludeProperties = false });
            Assert.AreEqual("{\"$type\":\"Vector3\",\"x\":1,\"y\":2,\"z\":3}", result);
        }

        [Test]
        public static void Serialize_Poco_WalksFieldsAndProperties()
        {
            var result = OutputSerializer.Serialize(new SamplePoco(), new SerializeOptions());
            Assert.IsTrue(result.Contains("\"$type\":\"SamplePoco\""), result);
            Assert.IsTrue(result.Contains("\"Number\":7"), result);
            Assert.IsTrue(result.Contains("\"Label\":\"hi\""), result);
            Assert.IsTrue(result.Contains("\"Name\":\"prop\""), result);
        }

        [Test]
        public static void Serialize_PropertiesOnly_NoFields()
        {
            var result = OutputSerializer.Serialize(new OnlyProps(),
                new SerializeOptions { IncludeFields = false });
            Assert.IsTrue(result.Contains("\"Alpha\":1"), result);
            Assert.IsTrue(result.Contains("\"Beta\":2"), result);
            // A POCO with only auto-properties has no public instance fields.
            Assert.IsFalse(result.Contains("<"), result);
        }

        [Test]
        public static void Serialize_DepthLimit_StringifiesBeyondMax()
        {
            var root = new Nest
            {
                V = "root",
                Child = new Nest
                {
                    V = "mid",
                    Child = new Nest { V = "leaf" }
                }
            };
            var result = OutputSerializer.Serialize(root,
                new SerializeOptions { MaxDepth = 1 });
            // root (d0) and mid (d1) are walked; leaf (d2) exceeds the depth cap and
            // is stringified via ToString().
            Assert.IsTrue(result.Contains("\"V\":\"root\""), result);
            Assert.IsTrue(result.Contains("\"V\":\"mid\""), result);
            Assert.IsTrue(result.Contains("\"leaf\""), result);
        }

        [Test]
        public static void Serialize_Cycle_EmitsReferenceMarker()
        {
            var a = new Node { Id = "a" };
            a.Next = a; // self-reference
            var result = OutputSerializer.Serialize(a, new SerializeOptions());
            Assert.IsTrue(result.Contains("\"Id\":\"a\""), result);
            Assert.IsTrue(result.Contains("\"$ref\":\"Node\""), result);
        }

        [Test]
        public static void Serialize_ListTruncation_EmitsTruncatedCount()
        {
            var list = new List<int>();
            for (var i = 0; i < 105; i++) list.Add(i);
            var result = OutputSerializer.Serialize(list,
                new SerializeOptions { MaxListItems = 10 });
            Assert.IsTrue(result.StartsWith("{\"items\":"), result);
            Assert.IsTrue(result.Contains("\"truncated\":95"), result);
            // The first 10 items are present; the elided 95 are not serialized.
            Assert.IsTrue(result.Contains("0,1"), result);
            Assert.IsFalse(result.Contains(",100"), result);
        }

        [Test]
        public static void Serialize_ListUnderCap_ReturnsPlainArray()
        {
            var list = new List<int> { 1, 2, 3 };
            var result = OutputSerializer.Serialize(list,
                new SerializeOptions { MaxListItems = 100 });
            Assert.AreEqual("[1,2,3]", result);
        }

        [Test]
        public static void Serialize_ThrowingProperty_EmitsErrorMarker()
        {
            var result = OutputSerializer.Serialize(new Throwing(), new SerializeOptions());
            Assert.IsTrue(result.Contains("\"Good\":5"), result);
            Assert.IsTrue(result.Contains("<error:"), result);
            // The error must not abort serialization of sibling members.
            Assert.IsTrue(result.EndsWith("}"), result);
        }

        [Test]
        public static void Serialize_NestedListInPoco_RespectsDepthAndTruncation()
        {
            var poco = new SamplePoco();
            var opts = new SerializeOptions { MaxDepth = 2, MaxListItems = 3 };
            var result = OutputSerializer.Serialize(
                new Dictionary<string, object> { { "k", new List<int> { 1, 2, 3, 4, 5 } } },
                opts);
            Assert.IsTrue(result.Contains("\"items\":[1,2,3]"), result);
            Assert.IsTrue(result.Contains("\"truncated\":2"), result);
        }

        [Test]
        public static void Serialize_SameValueRepeated_NoFalseCycle()
        {
            // The same struct value repeated must NOT trip cycle detection
            // (value types are excluded from the visited set).
            var result = OutputSerializer.Serialize(
                new List<Vector3> { Vector3.zero, Vector3.zero, Vector3.zero },
                new SerializeOptions { IncludeProperties = false });
            Assert.IsFalse(result.Contains("$ref"), result);
        }

        // -------------------------------------------------------------------
        // Regression: code-review finding B7 — object_get_data resolves a
        // UnityEngine.Object and asks for a depth-limited reflective walk
        // (the documented contract for ScriptableObjects, Materials, etc.).
        // The regular Serialize entry point short-circuits every
        // UnityEngine.Object to a compact {objectId,type,name,assetPath}
        // handle before any reflection runs, so the four options were dead
        // at the call root. SerializeReflectiveRoot skips that short-circuit
        // for the top-level object only; nested UnityEngine.Object references
        // still collapse to handles.
        // -------------------------------------------------------------------

        [Test]
        public static void SerializeReflectiveRoot_UnityEngineObject_WalksFields()
        {
            // A ScriptableObject is a UnityEngine.Object with public fields
            // (if any are declared on the subclass). We use a temporary
            // Material because it has well-known public properties (name,
            // color, etc.) that the reflective walk must surface — the
            // pre-fix Serialize() returned only a compact handle.
            var mat = new Material(Shader.Find("Hidden/InternalErrorShader"));
            try
            {
                mat.name = "__MCPTest_ReflectiveRoot";
                var opts = new SerializeOptions { MaxDepth = 2, MaxListItems = 5 };
                var result = OutputSerializer.SerializeReflectiveRoot(mat, opts);
                // The reflective root must start the composite shape ($type),
                // NOT the compact ObjectHandle shape (which starts with objectId).
                StringAssert.Contains("\"$type\":\"Material\"", result);
                // Material.name is a public property — the walk must surface it.
                StringAssert.Contains("\"name\":\"__MCPTest_ReflectiveRoot\"", result);
                // The compact handle keys must NOT dominate the root (they
                // appear only on nested UnityEngine.Object references, if any).
                // The pre-fix bug returned a handle whose first key was objectId.
                Assert.IsFalse(result.StartsWith("{\"objectId\":"),
                    "SerializeReflectiveRoot must not emit a compact handle at the root: " + result);
            }
            finally
            {
                Object.DestroyImmediate(mat);
            }
        }

        [Test]
        public static void SerializeReflectiveRoot_Null_ReturnsJsonNull()
        {
            Assert.AreEqual("null", OutputSerializer.SerializeReflectiveRoot(null, new SerializeOptions()));
        }

        [Test]
        public static void SerializeReflectiveRoot_Poco_StillReflects()
        {
            // A non-UnityEngine.Object root must still go through the normal
            // reflective walk (no regression for plain POCOs).
            var result = OutputSerializer.SerializeReflectiveRoot(new SamplePoco(), new SerializeOptions());
            StringAssert.Contains("\"$type\":\"SamplePoco\"", result);
            StringAssert.Contains("\"Number\":7", result);
        }

        [Test]
        public static void Serialize_UnityEngineObject_StillEmitsCompactHandle()
        {
            // Regression guard: the regular Serialize entry point must STILL
            // short-circuit UnityEngine.Object to a compact handle (this is
            // the correct behaviour for invoke_method/execute_csharp return
            // values, where unbounded GameObject/Component graphs must not be
            // walked). Only SerializeReflectiveRoot bypasses the short-circuit.
            var mat = new Material(Shader.Find("Hidden/InternalErrorShader"));
            try
            {
                var result = OutputSerializer.Serialize(mat);
                Assert.IsTrue(result.StartsWith("{\"objectId\":"),
                    "Serialize must still emit a compact handle for UnityEngine.Object: " + result);
                Assert.IsFalse(result.Contains("\"$type\":\"Material\""),
                    "Serialize must not reflect into a UnityEngine.Object: " + result);
            }
            finally
            {
                Object.DestroyImmediate(mat);
            }
        }
    }
}
