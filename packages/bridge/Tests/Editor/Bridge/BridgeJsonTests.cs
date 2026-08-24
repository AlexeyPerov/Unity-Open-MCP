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
        // ---- feedback-fable-04-08 §9 — compilePending gate envelope advisory ----

        // Helper: build a minimal GateDispatchResult for envelope-shape tests.
        private static GateDispatchResult MakeResult(bool compilePending)
        {
            return new GateDispatchResult
            {
                Mutation = ToolDispatchResult.Ok("{}"),
                GateRan = true,
                Outcome = GateOutcome.Passed,
                CheckpointId = "test-cp",
                CategoriesRun = new[] { "missing_references" },
                Delta = new DeltaData(),
                AgentNextSteps = new[] { "Gate passed — no new issues detected." },
                CompilePending = compilePending,
            };
        }

        [Test]
        public static void BuildGateEnvelope_EmitsCompilePending_WhenFlagSet()
        {
            var json = BridgeJson.BuildGateEnvelope(
                MakeResult(compilePending: true), "enforce", LifecyclePolicy.EditorSettle);
            StringAssert.Contains("\"compilePending\":true", json,
                $"envelope must surface compilePending when the editor is still compiling post-settle: {json}");
            // The advisory must also reach agentNextSteps so an agent branching
            // on prose (not just the flag) is steered to poll isCompiling.
            StringAssert.Contains("PRE-compile state", json,
                $"envelope agentNextSteps must carry the compilePending advisory: {json}");
        }

        [Test]
        public static void BuildGateEnvelope_OmitsCompilePending_WhenFlagClear()
        {
            // The common (clean) path must not change shape: compilePending is
            // omitted entirely, not emitted as false.
            var json = BridgeJson.BuildGateEnvelope(
                MakeResult(compilePending: false), "enforce", LifecyclePolicy.EditorSettle);
            Assert.IsFalse(json.Contains("compilePending"),
                $"clean path must NOT emit compilePending (additive only): {json}");
        }

        // A gate run where no delta was computed (checkpoint failure, mutation
        // failure, validate_scan_failed) must emit delta:null — a zeroed delta
        // object read as "computed and clean" and masked the withheld delta.
        [Test]
        public static void BuildGateEnvelope_NullDelta_EmitsJsonNull_NotZeroedObject()
        {
            var result = new GateDispatchResult
            {
                Mutation = ToolDispatchResult.Ok("{}"),
                GateRan = true,
                Outcome = GateOutcome.ValidateScanFailed,
                CheckpointId = "test-cp",
                CategoriesRun = new[] { "missing_references" },
                Delta = null,
                RulesFailed = new[] { "missing_references" },
                GateFailed = true,
                AgentNextSteps = new[] { "Mutation committed, but verify rule(s) threw…" },
            };

            var json = BridgeJson.BuildGateEnvelope(result, "enforce", LifecyclePolicy.EditorSettle);
            StringAssert.Contains("\"delta\":null", json,
                $"a withheld delta must be an explicit null: {json}");
            Assert.IsFalse(json.Contains("\"newErrors\":"),
                $"no delta counters may accompany a withheld delta: {json}");
            StringAssert.Contains("\"outcome\":\"validate_scan_failed\"", json);
            StringAssert.Contains("\"rulesFailed\":[\"missing_references\"]", json,
                $"the failing rule ids must ride on the gate block: {json}");
        }

        // The clean path keeps the historical shape: a computed delta emits the
        // counters, and rulesFailed is omitted entirely (additive-only field).
        [Test]
        public static void BuildGateEnvelope_ComputedDelta_OmitsRulesFailed()
        {
            var json = BridgeJson.BuildGateEnvelope(
                MakeResult(compilePending: false), "enforce", LifecyclePolicy.EditorSettle);
            StringAssert.Contains("\"newErrors\":0", json);
            Assert.IsFalse(json.Contains("rulesFailed"),
                $"rulesFailed is additive-only — a clean run must not carry it: {json}");
        }

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

        // ============ feedback.md issue 2 — paths_hint_required + read_only ============

        // execute_csharp exposes read_only; when the tool has it, the
        // paths_hint_required envelope must offer the read_only exit so agents
        // stop inventing fabricated scopes for read probes.
        [Test]
        public static void BuildPathsHintErrorEnvelope_ExposesReadOnly_ForExecuteCsharp()
        {
            var json = BridgeJson.BuildPathsHintErrorEnvelope(
                "unity_open_mcp_execute_csharp", "enforce", toolExposesReadOnly: true);
            StringAssert.Contains("read_only: true", json,
                "The envelope must mention read_only: true for execute_csharp.");
            // agentNextSteps has BOTH the paths_hint step and the read_only step.
            StringAssert.Contains("paths_hint", json);
            StringAssert.Contains("read-only probes", json,
                "The second agentNextSteps entry must name the read-only probe exit.");
        }

        // specs/feedback.md 2026-08-14 — with gate: "off" the envelope used to
        // read as a contradiction: the gate was reported SKIPPED and the reason
        // given was the missing argument the gate would have consumed. The
        // requirement IS intentional beyond the gate (paths_hint is the declared
        // mutation scope recorded in the audit trail), so the message must say
        // so, and the skip reason must describe what actually happened — the
        // request was refused before dispatch, so no gate ever ran.
        [Test]
        public static void BuildPathsHintErrorEnvelope_ExplainsTheGateOffRequirement()
        {
            var json = BridgeJson.BuildPathsHintErrorEnvelope(
                "unity_open_mcp_execute_menu", "off");
            StringAssert.Contains("even with gate", json,
                "The message must say paths_hint is required even with gate: \"off\".");
            StringAssert.Contains("audit trail", json,
                "The message must name the consumer that survives gate: \"off\".");
            StringAssert.Contains("refused before dispatch", json,
                "The message must say nothing ran and no gate was evaluated.");
        }

        [Test]
        public static void BuildPathsHintErrorEnvelope_SkippedReasonIsNotSelfReferential()
        {
            var json = BridgeJson.BuildPathsHintErrorEnvelope(
                "unity_open_mcp_execute_menu", "off");
            StringAssert.Contains("\"skippedReason\":\"request_rejected\"", json,
                "The gate did not skip *because* paths_hint was missing — it never ran.");
            Assert.IsFalse(json.Contains("\"skippedReason\":\"paths_hint_required\""),
                "The self-referential skip reason must be gone.");
            // Shape parity with the main gate envelope is preserved.
            StringAssert.Contains("\"skipped\":true", json);
            StringAssert.Contains("\"outcome\":\"skipped\"", json);
            StringAssert.Contains("\"code\":\"paths_hint_required\"", json,
                "The error CODE is unchanged — only the reasoning is corrected.");
        }

        // ---- specs/feedback.md 2026-08-24 — /ping wire-contract revision ----

        [Test]
        public static void BuildPingJson_ReportsWireContractRevision()
        {
            // The revision is what lets a client tell "your installed bridge
            // predates the fix" from "this regressed" when the package semver
            // has not moved (the 2026-08-17 editor_status fix recurring in the
            // field against a stale 1.0.0 install). It must be on /ping — that
            // is the only body bridge_status reads it from.
            var json = BridgeJson.BuildPingJson();
            StringAssert.Contains(
                "\"wireContract\":" + BridgeSession.WireContract, json,
                $"/ping must report the wire-contract revision: {json}");
            // bridgeVersion stays put — the two are separate axes on purpose.
            StringAssert.Contains("\"bridgeVersion\":", json);
        }

        [Test]
        public static void WireContract_IsPositive_SoAbsenceReadsAsStale()
        {
            // A caller treats a MISSING wireContract as "older than revision 1".
            // That inference only holds while the first shipped revision is >= 1;
            // a 0 would make a fixed bridge indistinguishable from an ancient one.
            Assert.GreaterOrEqual(BridgeSession.WireContract, 1,
                "The first wire-contract revision must be >= 1 so a missing field reads as stale.");
        }

        [Test]
        public static void BuildPathsHintErrorEnvelope_NoReadOnlyClause_ForToolsWithoutIt()
        {
            // invoke_method / execute_menu / etc. do NOT expose read_only — the
            // envelope must stay unchanged (no misleading read_only suggestion).
            var json = BridgeJson.BuildPathsHintErrorEnvelope(
                "unity_open_mcp_invoke_method", "enforce", toolExposesReadOnly: false);
            Assert.IsFalse(json.Contains("read_only"),
                "Tools without a read_only parameter must not advertise it.");
            Assert.IsFalse(json.Contains("read-only probes"),
                "No read-only agentNextSteps entry for tools without the flag.");
        }
    }
}
