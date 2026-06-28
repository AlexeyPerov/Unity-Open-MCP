using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    public class BridgeToolRegistryTests
    {
        [Test]
        public static void Scan_DiscoveredEditorStatus()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("unity_open_mcp_editor_status"),
                "unity_open_mcp_editor_status should be registered");
        }

        [Test]
        public static void Scan_EditorStatusIsReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_open_mcp_editor_status", out var entry));
            Assert.IsFalse(entry.IsMutating);
            Assert.IsTrue(entry.ReadOnlyHint);
        }

        [Test]
        public static void Scan_EditorStatusHasNoParameters()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_open_mcp_editor_status", out var entry));
            Assert.AreEqual(0, entry.Parameters.Length);
        }

        [Test]
        public static void TryDispatch_UnknownTool_ReturnsNull()
        {
            var result = BridgeToolRegistry.TryDispatch("unity_open_mcp_nonexistent_tool", "{}");
            Assert.IsNull(result);
        }
    }

    public class BridgeResourceRegistryTests
    {
        [Test]
        public static void All_ReturnsList()
        {
            var all = BridgeResourceRegistry.All();
            Assert.IsNotNull(all);
        }

        [Test]
        public static void FindByRoute_UnknownRoute_ReturnsNull()
        {
            var entry = BridgeResourceRegistry.FindByRoute("unity-open-mcp://nonexistent");
            Assert.IsNull(entry);
        }
    }

    /// <summary>
    /// Coercion tests for <see cref="BridgeToolRegistry.ConvertValue"/> — the
    /// JSON-arg-to-CLR conversion every dispatched tool relies on. A bad coercion
    /// silently feeds a mutating tool the wrong argument.
    /// </summary>
    public class BridgeToolRegistryConvertValueTests
    {
        // --- string ---

        [Test]
        public static void ConvertValue_QuotedString_Unquotes()
        {
            Assert.AreEqual("hello", BridgeToolRegistry.ConvertValue("\"hello\"", typeof(string)));
        }

        [Test]
        public static void ConvertValue_BareString_PassedThrough()
        {
            Assert.AreEqual("hello", BridgeToolRegistry.ConvertValue("hello", typeof(string)));
        }

        // --- int ---

        [TestCase("42", ExpectedResult = 42)]
        [TestCase("0", ExpectedResult = 0)]
        [TestCase("-7", ExpectedResult = -7)]
        public static object ConvertValue_Int_Parses(string raw)
        {
            return BridgeToolRegistry.ConvertValue(raw, typeof(int));
        }

        [Test]
        public static void ConvertValue_Int_NotANumber_DefaultsToZero()
        {
            Assert.AreEqual(0, BridgeToolRegistry.ConvertValue("abc", typeof(int)));
        }

        // --- float ---

        [TestCase("3.14", ExpectedResult = 3.14f)]
        [TestCase("0", ExpectedResult = 0f)]
        [TestCase("-2.5", ExpectedResult = -2.5f)]
        public static object ConvertValue_Float_Parses(string raw)
        {
            return BridgeToolRegistry.ConvertValue(raw, typeof(float));
        }

        [Test]
        public static void ConvertValue_Float_NotANumber_DefaultsToZero()
        {
            Assert.AreEqual(0f, BridgeToolRegistry.ConvertValue("abc", typeof(float)));
        }

        // --- bool ---

        [TestCase("true", ExpectedResult = true)]
        [TestCase("false", ExpectedResult = false)]
        public static bool ConvertValue_Bool_Parses(string raw)
        {
            return (bool)BridgeToolRegistry.ConvertValue(raw, typeof(bool));
        }

        [Test]
        public static void ConvertValue_Bool_Garbage_DefaultsToFalse()
        {
            Assert.AreEqual(false, BridgeToolRegistry.ConvertValue("maybe", typeof(bool)));
        }

        // --- string[] ---

        [Test]
        public static void ConvertValue_StringArray_ParsesJsonArray()
        {
            var result = (string[])BridgeToolRegistry.ConvertValue("[\"a\",\"b\",\"c\"]", typeof(string[]));
            CollectionAssert.AreEquivalent(new[] { "a", "b", "c" }, result);
        }

        [Test]
        public static void ConvertValue_StringArray_Malformed_ReturnsEmpty()
        {
            var result = (string[])BridgeToolRegistry.ConvertValue("not-an-array", typeof(string[]));
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Length);
        }

        // --- enum ---

        [Test]
        public static void ConvertValue_Enum_ByQuotedName()
        {
            Assert.AreEqual(TestColor.Red,
                BridgeToolRegistry.ConvertValue("\"Red\"", typeof(TestColor)));
        }

        [Test]
        public static void ConvertValue_Enum_ByInt()
        {
            // Green = 1
            Assert.AreEqual(TestColor.Green,
                BridgeToolRegistry.ConvertValue("1", typeof(TestColor)));
        }

        [Test]
        public static void ConvertValue_Enum_UnknownValue_DefaultsToFirstMember()
        {
            Assert.AreEqual(TestColor.Red,
                BridgeToolRegistry.ConvertValue("\"Purple\"", typeof(TestColor)));
        }

        // --- null ---

        [Test]
        public static void ConvertValue_NullLiteral_ReturnsNull()
        {
            Assert.IsNull(BridgeToolRegistry.ConvertValue("null", typeof(string)));
        }

        // --- nullable value types (M20 Plan 2 — light_set uses float?/int?) ---
        // A nullable value-type parameter unwraps through its underlying type so
        // the typed checks (int / float / bool / enum) still apply. The result
        // boxes back into Nullable<T>.

        [Test]
        public static void ConvertValue_NullableFloat_ParsesThroughUnderlying()
        {
            var result = BridgeToolRegistry.ConvertValue("3.5", typeof(float?));
            Assert.AreEqual(3.5f, result);
        }

        [Test]
        public static void ConvertValue_NullableInt_ParsesThroughUnderlying()
        {
            var result = BridgeToolRegistry.ConvertValue("42", typeof(int?));
            Assert.AreEqual(42, result);
        }

        [Test]
        public static void ConvertValue_NullableBool_ParsesThroughUnderlying()
        {
            var result = BridgeToolRegistry.ConvertValue("true", typeof(bool?));
            Assert.AreEqual(true, result);
        }

        enum TestColor
        {
            Red,
            Green,
            Blue
        }
    }
}
