using System.Collections.Generic;
using NUnit.Framework;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge.Tests
{
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
    }
}
