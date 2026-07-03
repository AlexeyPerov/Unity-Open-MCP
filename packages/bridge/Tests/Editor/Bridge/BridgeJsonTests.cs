using System;
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

        // specs/feedback.md 2026-07-03 — main_thread_blocked envelope. When a
        // Unity modal wedges the main thread, MainThreadDispatcher raises
        // MainThreadBlockedException (distinct from TimeoutException), and the
        // dispatcher builds this envelope so the agent gets a structured signal
        // + recovery hints instead of a generic timeout. Assert the shape +
        // that the error code is the documented `main_thread_blocked`.
        [Test]
        public static void BuildMainThreadBlockedEnvelope_CarriesStructuredErrorCode()
        {
            var json = BridgeJson.BuildMainThreadBlockedEnvelope(
                "unity_open_mcp_validate_edit", "off", 30000);

            // The whole point: a distinct error code so the agent can branch on
            // "modal likely open" vs "tool ran long".
            StringAssert.Contains("\"code\":\"main_thread_blocked\"", json);
            StringAssert.Contains("\"success\":false", json);
            // Must include the tool name + timeout for triage.
            StringAssert.Contains("unity_open_mcp_validate_edit", json);
            StringAssert.Contains("30000ms", json);
            // The recovery hints are the actionable payload — assert at least
            // the scene_save + restart pointers are present.
            StringAssert.Contains("scene_save", json);
            StringAssert.Contains("restart", json);
            // And it must NOT suggest raising timeout_ms (the wrong reflex).
            StringAssert.DoesNotContain("increase timeout_ms", json);
            StringAssert.DoesNotContain("Consider increasing", json);
        }

        [Test]
        public static void MainThreadBlockedException_CarriesTimeoutAndMessage()
        {
            var ex = new MainThreadBlockedException(30000);
            Assert.AreEqual(30000, ex.TimeoutMs);
            // Message must mention the modal / main-thread cause so it surfaces
            // usefully in logs even if the envelope builder is bypassed.
            StringAssert.Contains("main thread", ex.Message.ToLowerInvariant());
            StringAssert.Contains("modal", ex.Message.ToLowerInvariant());
            // And it must NOT be a TimeoutException (the whole reason it exists
            // is to be distinguishable from TimeoutException in catch blocks).
            // Use a typeof check rather than `ex is TimeoutException` so the
            // assertion doesn't trip CS0184 (the compiler statically proves the
            // `is` is always false for the var-typed local — which is the very
            // property under test, but the warning is noise).
            Assert.IsFalse(
                typeof(TimeoutException).IsAssignableFrom(typeof(MainThreadBlockedException)),
                "MainThreadBlockedException must NOT be a TimeoutException subclass " +
                "(the dispatcher's catch branches on the distinction).");
        }
    }
}
