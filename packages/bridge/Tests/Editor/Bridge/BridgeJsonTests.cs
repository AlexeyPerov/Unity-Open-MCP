using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // IsValidJsonObject is the defense-in-depth guard ExecuteCSharpTool uses
    // to refuse OutputSerializer output that was corrupted by a mid-walk
    // exception (e.g. a TypeLoadException on a field referencing a missing
    // assembly). Without it, the malformed output is interpolated raw into the
    // gate envelope at `result.Mutation.Output`, corrupting the whole response
    // body — see specs/feedback.md entry 2026-07-03-c.
    public static class BridgeJsonTests
    {
        // ---- Accepted: balanced JSON objects ----

        [Test]
        public static void IsValidJsonObject_EmptyObject_ReturnsTrue()
        {
            Assert.IsTrue(BridgeJson.IsValidJsonObject("{}"));
        }

        [Test]
        public static void IsValidJsonObject_SimpleObject_ReturnsTrue()
        {
            Assert.IsTrue(BridgeJson.IsValidJsonObject("{\"a\":1}"));
        }

        [Test]
        public static void IsValidJsonObject_NestedObject_ReturnsTrue()
        {
            Assert.IsTrue(BridgeJson.IsValidJsonObject("{\"a\":{\"b\":{\"c\":1}}}"));
        }

        [Test]
        public static void IsValidJsonObject_BracesInsideStringLiteral_DoNotAffectBalance()
        {
            // A string value containing '{' or '}' must not fool the walker.
            Assert.IsTrue(BridgeJson.IsValidJsonObject("{\"a\":\"}{\",\"b\":1}"));
        }

        [Test]
        public static void IsValidJsonObject_EscapedQuoteInsideString_DoesNotTerminateString()
        {
            // An escaped quote inside a string literal must not end the string,
            // so a brace after it is still treated as inside-string.
            Assert.IsTrue(BridgeJson.IsValidJsonObject("{\"a\":\"he said \\\"hi\\\" {x}\"}"));
        }

        // ---- Accepted: bare scalars / arrays / keywords ----
        //
        // OutputSerializer legitimately emits bare values for primitive and
        // array returns (e.g. `return 42;` → "42", `return "hi";` → "\"hi\"",
        // `return new[]{1,2,3};` → "[1,2,3]"). These interpolate into the gate
        // envelope as `"output":<value>` — valid JSON. The guard validates
        // BALANCE, not "is an object specifically", so these must pass.

        [Test]
        public static void IsValidJsonObject_BareNumber_ReturnsTrue()
        {
            Assert.IsTrue(BridgeJson.IsValidJsonObject("42"));
        }

        [Test]
        public static void IsValidJsonObject_BareBool_ReturnsTrue()
        {
            Assert.IsTrue(BridgeJson.IsValidJsonObject("true"));
            Assert.IsTrue(BridgeJson.IsValidJsonObject("false"));
        }

        [Test]
        public static void IsValidJsonObject_BareNull_ReturnsTrue()
        {
            Assert.IsTrue(BridgeJson.IsValidJsonObject("null"));
        }

        [Test]
        public static void IsValidJsonObject_BareString_ReturnsTrue()
        {
            Assert.IsTrue(BridgeJson.IsValidJsonObject("\"hello\""));
        }

        [Test]
        public static void IsValidJsonObject_BareArray_ReturnsTrue()
        {
            Assert.IsTrue(BridgeJson.IsValidJsonObject("[1,2,3]"));
            Assert.IsTrue(BridgeJson.IsValidJsonObject("[]"));
        }

        // ---- Rejected: malformed / truncated JSON ----

        [Test]
        public static void IsValidJsonObject_Null_ReturnsFalse()
        {
            Assert.IsFalse(BridgeJson.IsValidJsonObject(null));
        }

        [Test]
        public static void IsValidJsonObject_EmptyString_ReturnsFalse()
        {
            Assert.IsFalse(BridgeJson.IsValidJsonObject(""));
        }

        [Test]
        public static void IsValidJsonObject_UnbalancedOpen_ReturnsFalse()
        {
            // The exact shape of the bug: OutputSerializer truncating mid-walk
            // leaves an unbalanced opening brace. This is what a TypeLoadException
            // escaping the per-member try/catch produces.
            Assert.IsFalse(BridgeJson.IsValidJsonObject("{\"mutation\":{\"success\":true,\"output\":{\"broken\":"));
        }

        [Test]
        public static void IsValidJsonObject_UnbalancedClose_ReturnsFalse()
        {
            Assert.IsFalse(BridgeJson.IsValidJsonObject("{\"a\":1}}"));
        }

        [Test]
        public static void IsValidJsonObject_UnbalancedArrayOpen_ReturnsFalse()
        {
            // Truncation mid-array (another mid-walk failure shape).
            Assert.IsFalse(BridgeJson.IsValidJsonObject("[1,2,"));
        }

        [Test]
        public static void IsValidJsonObject_DanglingString_ReturnsFalse()
        {
            // Unterminated string at EOF — walker ends with inStr:true.
            Assert.IsFalse(BridgeJson.IsValidJsonObject("{\"a\":\"unclosed"));
        }

        [Test]
        public static void IsValidJsonObject_TruncatedBareString_ReturnsFalse()
        {
            // A top-level string that never closes — OutputSerializer's quoted-
            // string emission cut off mid-value by an exception.
            Assert.IsFalse(BridgeJson.IsValidJsonObject("\"never closed"));
        }
    }
}
