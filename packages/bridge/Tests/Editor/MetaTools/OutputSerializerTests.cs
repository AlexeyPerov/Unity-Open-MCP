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
        public static void Serialize_Null_ReturnsNull()
        {
            Assert.IsNull(OutputSerializer.Serialize(null));
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
    }
}
