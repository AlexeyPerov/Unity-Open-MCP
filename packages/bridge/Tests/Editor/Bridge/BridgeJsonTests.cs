using System;
using System.Text;
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

        // ---- T30.5 shared JSON value appenders ---------------------------------
        //
        // AppendJsonString / AppendJsonBool / AppendJsonNumber* are the bridge-
        // wide primitives typed tools MUST use for hand-rolled JSON (see the
        // contributor note in packages/bridge/AGENTS.md §Transport). The two
        // failure modes they exist to prevent:
        //   1. split strings closed across Append calls → unparsable bodies
        //      (the profiler `note` bug from M30 Plan 1);
        //   2. `sb.Append(bool)` emitting C# True/False instead of JSON
        //      true/false (the historical asmdef_list autoReferenced bug).

        [Test]
        public static void AppendJsonString_PlainString_WrappedInQuotes()
        {
            var sb = new StringBuilder();
            BridgeJson.AppendJsonString(sb, "hello");
            Assert.AreEqual("\"hello\"", sb.ToString());
        }

        [Test]
        public static void AppendJsonString_Null_EmitsJsonNullKeyword()
        {
            // null ⇒ bare `null` keyword (NOT "null" the string). Callers that
            // want "" for null pass an empty string.
            var sb = new StringBuilder();
            BridgeJson.AppendJsonString(sb, null);
            Assert.AreEqual("null", sb.ToString());
        }

        [Test]
        public static void AppendJsonString_EmptyString_EmitsEmptyQuotedString()
        {
            var sb = new StringBuilder();
            BridgeJson.AppendJsonString(sb, "");
            Assert.AreEqual("\"\"", sb.ToString());
        }

        [Test]
        public static void AppendJsonString_EscapesEmbeddedQuotes()
        {
            // The exact shape that broke the profiler `note`: an unescaped quote
            // inside the value terminates the string early. The helper must
            // escape it so the whole value stays one JSON string token.
            var sb = new StringBuilder();
            BridgeJson.AppendJsonString(sb, "he said \"hi\"");
            // Round-trip via a strict JSON parse: the emitted token must parse
            // back to the original string.
            var emitted = sb.ToString();
            Assert.AreEqual("\"he said \\\"hi\\\"\"", emitted);
            Assert.AreEqual("he said \"hi\"", SimpleJsonUnescape(emitted));
        }

        [Test]
        public static void AppendJsonString_EscapesNewlinesAndTabs()
        {
            var sb = new StringBuilder();
            BridgeJson.AppendJsonString(sb, "line1\nline2\ttab\rreturn");
            var emitted = sb.ToString();
            // No raw control characters may survive in the emitted JSON.
            foreach (var ch in emitted)
                Assert.IsFalse(ch == '\n' || ch == '\r' || ch == '\t',
                    "raw control char survived escape: " + emitted);
            Assert.AreEqual("line1\nline2\ttab\rreturn", SimpleJsonUnescape(emitted));
        }

        [Test]
        public static void AppendJsonString_EscapesBackslash()
        {
            var sb = new StringBuilder();
            BridgeJson.AppendJsonString(sb, "C:\\Program Files");
            Assert.AreEqual("\"C:\\\\Program Files\"", sb.ToString());
        }

        [Test]
        public static void AppendJsonString_ControlChars_EmitUnicodeEscape()
        {
            // C0 control chars (< 0x20) other than the named ones above must be
            // emitted as \uXXXX so the output is valid JSON.
            var sb = new StringBuilder();
            BridgeJson.AppendJsonString(sb, "\x01\x02");
            var emitted = sb.ToString();
            StringAssert.StartsWith("\"\\u0001\\u0002\"", emitted);
        }

        [Test]
        public static void AppendJsonString_ProducesValidJsonObjectAroundIt()
        {
            // The end-to-end shape: a whole object built with the helper must
            // pass the same validity guard the dispatcher relies on. This is the
            // regression line for "helper emits a value that breaks the envelope".
            var sb = new StringBuilder();
            sb.Append("{\"note\":");
            BridgeJson.AppendJsonString(sb, "value with \"quotes\" and \n newlines");
            sb.Append('}');
            Assert.IsTrue(BridgeJson.IsValidJsonObject(sb.ToString()));
        }

        [Test]
        public static void AppendJsonBool_TrueAndFalse_EmitLowercaseJsonKeywords()
        {
            // sb.Append(bool) would emit C# True/False — the helper must emit
            // JSON true/false. This is the asmdef_list autoReferenced regression
            // line.
            var sbT = new StringBuilder();
            BridgeJson.AppendJsonBool(sbT, true);
            Assert.AreEqual("true", sbT.ToString());

            var sbF = new StringBuilder();
            BridgeJson.AppendJsonBool(sbF, false);
            Assert.AreEqual("false", sbF.ToString());
        }

        [Test]
        public static void AppendJsonNumber_Long_EmitsInvariantInteger()
        {
            var sb = new StringBuilder();
            BridgeJson.AppendJsonNumber(sb, 123456789L);
            Assert.AreEqual("123456789", sb.ToString());
        }

        [Test]
        public static void AppendJsonNumber_Double_EmitsInvariantDecimalNoComma()
        {
            // Locale safety: in de-DE a naive ToString would emit "1,5" — the
            // helper must always emit "1.5" (JSON radix point).
            var sb = new StringBuilder();
            BridgeJson.AppendJsonNumber(sb, 1.5);
            Assert.AreEqual("1.5", sb.ToString());
        }

        [Test]
        public static void AppendJsonStringField_EmitsCompleteKeyColonValue()
        {
            // The field helper keeps key + escaped value in one call so neither
            // half can dangle — the direct fix for the split-string anti-pattern.
            var sb = new StringBuilder();
            sb.Append('{');
            BridgeJson.AppendJsonStringField(sb, "note", "recording starts next frame");
            sb.Append('}');
            var json = sb.ToString();
            Assert.AreEqual("{\"note\":\"recording starts next frame\"}", json);
            Assert.IsTrue(BridgeJson.IsValidJsonObject(json));
        }

        [Test]
        public static void AppendJsonBoolField_EmitsCompleteKeyColonBool()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            BridgeJson.AppendJsonBoolField(sb, "enabled", true);
            sb.Append('}');
            Assert.AreEqual("{\"enabled\":true}", sb.ToString());
        }

        [Test]
        public static void AppendJsonNumberField_EmitsCompleteKeyColonNumber()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            BridgeJson.AppendJsonNumberField(sb, "bytes", 4096L);
            sb.Append('}');
            Assert.AreEqual("{\"bytes\":4096}", sb.ToString());
        }

        // Minimal JSON string unescape for round-trip assertions above. Only
        // handles the escapes BridgeJson.AppendJsonString emits (\", \\, \n,
        // \r, \t, \uXXXX) — not a general parser.
        private static string SimpleJsonUnescape(string quoted)
        {
            // Strip surrounding quotes.
            var inner = quoted.Substring(1, quoted.Length - 2);
            var sb = new StringBuilder(inner.Length);
            for (int i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '\\' && i + 1 < inner.Length)
                {
                    var n = inner[i + 1];
                    switch (n)
                    {
                        case '"': sb.Append('"'); i++; break;
                        case '\\': sb.Append('\\'); i++; break;
                        case 'n': sb.Append('\n'); i++; break;
                        case 'r': sb.Append('\r'); i++; break;
                        case 't': sb.Append('\t'); i++; break;
                        case 'u':
                            sb.Append((char)Convert.ToInt32(inner.Substring(i + 2, 4), 16));
                            i += 5;
                            break;
                        default: sb.Append(inner[i]); break;
                    }
                }
                else sb.Append(inner[i]);
            }
            return sb.ToString();
        }
    }
}
