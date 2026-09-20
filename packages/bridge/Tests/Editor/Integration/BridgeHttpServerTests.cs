using System;
using System.Collections;
using System.Net;
using System.Net.Http;
using System.Text;
using NUnit.Framework;
using UnityOpenMcpBridge;
using UnityEngine.TestTools;

namespace UnityOpenMcpBridge.Tests
{
    public class BridgeHttpServerTests
    {
        // The bridge listens on a per-project port (InstancePortResolver), not
        // a fixed 19120. The Editor's [InitializeOnLoad] static ctor starts
        // the listener before any EditMode test runs, so by the time these
        // tests execute the bridge is live on BridgeHttpServer.Port — read it
        // dynamically instead of pinning a port that only matches when an env
        // var happens to be set. If the listener isn't up (port collision,
        // auth refusal, etc.) Assert.Ignore keeps the suite green instead of
        // burning a 10s HttpClient timeout per test.
        private static string BaseUrl =>
            $"http://127.0.0.1:{BridgeHttpServer.Port}";
        private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        [SetUp]
        public void EnsureBridgeRunning()
        {
            if (!BridgeHttpServer.IsRunning)
                Assert.Ignore("Bridge HTTP listener is not running — skipping HTTP integration tests.");
        }

        // Ping / 404 / method-routing endpoints are served directly by the
        // HTTP listener thread (no MainThreadDispatcher hop), so they work as
        // plain synchronous [Test]s even while the editor is running NUnit.

        [Test]
        public static void Ping_ReturnsExpectedShape()
        {
            var response = HttpClient.GetAsync($"{BaseUrl}/ping").Result;
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"connected\""), "Missing connected field");
            Assert.IsTrue(body.Contains("\"projectPath\""), "Missing projectPath field");
            Assert.IsTrue(body.Contains("\"unityVersion\""), "Missing unityVersion field");
            Assert.IsTrue(body.Contains("\"bridgeVersion\""), "Missing bridgeVersion field");
            Assert.IsTrue(body.Contains("\"mode\""), "Missing mode field");
            Assert.IsTrue(body.Contains("\"compiling\""), "Missing compiling field");
            Assert.IsTrue(body.Contains("\"isPlaying\""), "Missing isPlaying field");
        }

        [Test]
        public static void Ping_BridgeVersion_IsExpected()
        {
            var response = HttpClient.GetAsync($"{BaseUrl}/ping").Result;
            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"bridgeVersion\":\"" + UnityOpenMcpBridge.BridgeSession.BridgeVersion + "\""),
                $"Unexpected version in: {body}");
        }

        [Test]
        public static void Ping_Mode_IsLive()
        {
            var response = HttpClient.GetAsync($"{BaseUrl}/ping").Result;
            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"mode\":\"live\""), $"Expected live mode in: {body}");
        }

        [Test]
        public static void UnknownEndpoint_Returns404()
        {
            var response = HttpClient.GetAsync($"{BaseUrl}/unknown").Result;
            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"not_found\""));
        }

        [Test]
        public static void UnknownTool_Returns404()
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_nonexistent", content).Result;
            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"tool_not_found\""));
        }

        [Test]
        public static void ToolsEndpoint_GetMethod_Returns405()
        {
            var response = HttpClient.GetAsync($"{BaseUrl}/tools/unity_open_mcp_ping").Result;
            Assert.AreEqual(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"method_not_allowed\""));
        }

        // Tool-dispatch endpoints route through MainThreadDispatcher, which
        // only pumps on EditorApplication.update. A synchronous [Test] blocks
        // the main thread, so the queued dispatch never runs and the HTTP
        // call hangs until its 10s timeout. These run as [UnityTest]
        // coroutines instead: SendAsync runs on a ThreadPool thread while the
        // coroutine yields, letting update pump the dispatch queue.

        [UnityTest]
        public IEnumerator TimeoutThenSuccess_HasIndependentCorrelatedEnvelopes()
        {
            using var first = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/tools/unity_open_mcp_execute_csharp");
            first.Headers.Add("X-Request-Id", "timeout-request");
            first.Content = new StringContent("{\"code\":\"System.Threading.Thread.Sleep(1500); return 41;\",\"read_only\":true,\"timeout_ms\":1000,\"gate\":\"off\"}", Encoding.UTF8, "application/json");
            var pending = HttpClient.SendAsync(first);
            while (!pending.IsCompleted) yield return null;
            using var response = pending.Result;
            var body = response.Content.ReadAsStringAsync().Result;
            StringAssert.Contains("timeout", body);
            Assert.AreEqual("timeout-request", System.Linq.Enumerable.First(response.Headers.GetValues("X-Request-Id")));
            Assert.IsTrue(BridgeJson.IsValidJsonObject(body));
            using var second = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/ping");
            second.Headers.Add("X-Request-Id", "success-request");
            using var success = HttpClient.SendAsync(second).Result;
            var successBody = success.Content.ReadAsStringAsync().Result;
            Assert.AreEqual("success-request", System.Linq.Enumerable.First(success.Headers.GetValues("X-Request-Id")));
            Assert.IsTrue(BridgeJson.IsValidJsonObject(successBody));
            StringAssert.Contains("\"connected\"", successBody);
            StringAssert.DoesNotContain("timeout", successBody);
        }

        private static IEnumerator PostAndWait(string path, string json, Action<string> assertBody)
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var task = HttpClient.PostAsync($"{BaseUrl}{path}", content);
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
                Assert.Fail($"HTTP request faulted: {task.Exception?.GetBaseException()?.Message}");
            var response = task.Result;
            var body = response.Content.ReadAsStringAsync().Result;
            assertBody(body);
        }

        [UnityTest]
        public static IEnumerator MutatingTool_EmptyPathsHint_ReturnsPathsHintRequired()
        {
            return PostAndWait("/tools/unity_open_mcp_execute_csharp",
                "{\"code\":\"return 1;\",\"paths_hint\":[]}",
                body =>
                {
                    Assert.IsTrue(body.Contains("\"paths_hint_required\""), $"Expected paths_hint_required error in: {body}");
                    Assert.IsTrue(body.Contains("\"success\":false"), $"Expected success:false in: {body}");
                    Assert.IsTrue(body.Contains("\"skipped\":true"), $"Expected gate skipped in: {body}");
                });
        }

        [UnityTest]
        public static IEnumerator MutatingTool_MissingPathsHint_ReturnsPathsHintRequired()
        {
            return PostAndWait("/tools/unity_open_mcp_execute_csharp",
                "{\"code\":\"return 1;\"}",
                body => Assert.IsTrue(body.Contains("\"paths_hint_required\""), $"Expected paths_hint_required in: {body}"));
        }

        [UnityTest]
        public static IEnumerator InvokeMethod_EmptyPathsHint_ReturnsPathsHintRequired()
        {
            return PostAndWait("/tools/unity_open_mcp_invoke_method",
                "{\"type_name\":\"System.Environment\",\"method_name\":\"get_TickCount\",\"is_static\":true,\"paths_hint\":[]}",
                body => Assert.IsTrue(body.Contains("\"paths_hint_required\""), $"Expected paths_hint_required in: {body}"));
        }

        [UnityTest]
        public static IEnumerator ExecuteMenu_EmptyPathsHint_NonAllowlisted_ReturnsPathsHintRequired()
        {
            return PostAndWait("/tools/unity_open_mcp_execute_menu",
                "{\"menu_path\":\"File/Save Project\",\"paths_hint\":[]}",
                body => Assert.IsTrue(body.Contains("\"paths_hint_required\""), $"Expected paths_hint_required for non-allowlisted menu in: {body}"));
        }

        [UnityTest]
        public static IEnumerator ExecuteMenu_EmptyPathsHint_Allowlisted_Proceeds()
        {
            return PostAndWait("/tools/unity_open_mcp_execute_menu",
                "{\"menu_path\":\"Assets/Refresh\",\"paths_hint\":[]}",
                body => Assert.IsFalse(body.Contains("\"paths_hint_required\""), $"Allowlisted menu should not return paths_hint_required: {body}"));
        }

        [UnityTest]
        public static IEnumerator FindMembers_DoesNotRequirePathsHint()
        {
            return PostAndWait("/tools/unity_open_mcp_find_members",
                "{\"query\":\"Transform\",\"kind\":\"type\",\"max_results\":5}",
                body => Assert.IsFalse(body.Contains("\"paths_hint_required\""), $"find_members should not require paths_hint: {body}"));
        }

        // execute_csharp runs the snippet synchronously on Unity's main thread;
        // if it blocks, the worker-thread timeout cannot unwind it and the
        // editor stays wedged. The timeout envelope must steer the agent toward
        // diagnosis (editor_status / bridge_status) + the async test path, NOT
        // toward the harmful "raise timeout_ms" reflex. Pure builder unit test
        // — we never drive a real deadlock, which would wedge the test runner.
        // See specs/feedback.md entry 1.
        [Test]
        public static void TimeoutEnvelope_ExecuteCSharp_WarnsAboutMainThreadBlock()
        {
            var json = BridgeJson.BuildTimeoutEnvelope(
                "unity_open_mcp_execute_csharp", "enforce", 30000);
            Assert.IsTrue(json.Contains("\"agentNextSteps\":["), $"Missing agentNextSteps: {json}");
            StringAssert.Contains("main thread", json, $"execute_csharp timeout must mention main thread: {json}");
            StringAssert.Contains("wedged", json, $"execute_csharp timeout must warn the editor may be wedged: {json}");
            StringAssert.Contains("unity_senses_run_tests", json,
                $"execute_csharp timeout must redirect tests to run_tests: {json}");
            // The harmful generic advice ("Consider increasing timeout_ms")
            // must NOT appear for execute_csharp — it makes the agent loop
            // against an already-dead editor.
            Assert.IsFalse(json.Contains("Consider increasing timeout_ms"),
                $"execute_csharp timeout must not suggest raising timeout_ms: {json}");
        }

        // specs/feedback.md 2026-08-12 — the code-level blockers named above are
        // not the only way a snippet wedges the main thread. An ExecuteMenuItem
        // that opens a MODAL dialog does it too, and that case behaves
        // differently: every subsequent call times out as well, /ping keeps
        // answering, and bridge_status reported a false dead_bridge / Safe Mode.
        // The envelope must name the modal cause so the agent stops chasing a
        // compile failure that does not exist.
        [Test]
        public static void TimeoutEnvelope_ExecuteCSharp_NamesTheModalDialogCause()
        {
            var json = BridgeJson.BuildTimeoutEnvelope(
                "unity_open_mcp_execute_csharp", "enforce", 30000);
            StringAssert.Contains("ExecuteMenuItem", json,
                $"execute_csharp timeout must name the ExecuteMenuItem/modal path: {json}");
            StringAssert.Contains("modal", json,
                $"execute_csharp timeout must name the modal-dialog cause: {json}");
            StringAssert.Contains("operator", json,
                $"a modal cannot self-heal — the envelope must say an operator has to dismiss it: {json}");
        }

        // specs/feedback.md 2026-08-14 — `Assets/Refresh` timed out twice while
        // in fact completing both times. A blind retry of an authoring menu can
        // double-write assets, so the envelope must say "verify, do not retry".
        [Test]
        public static void TimeoutEnvelope_ExecuteMenu_SaysVerifyRatherThanRetry()
        {
            var json = BridgeJson.BuildTimeoutEnvelope(
                "unity_open_mcp_execute_menu", "enforce", 30000);
            Assert.IsTrue(json.Contains("\"agentNextSteps\":["), $"Missing agentNextSteps: {json}");
            StringAssert.Contains("still be RUNNING", json,
                $"a menu timeout is a wait that elapsed, not a failure: {json}");
            StringAssert.Contains("editor_status", json,
                $"the envelope must point at editor_status to confirm the outcome: {json}");
            StringAssert.Contains("double-write", json,
                $"the envelope must warn that retrying an authoring menu can double-write: {json}");
            StringAssert.Contains("timeout_ms", json,
                $"execute_menu now accepts timeout_ms — the envelope must mention it: {json}");
            // The generic copy is replaced by the menu-specific guidance.
            Assert.IsFalse(json.Contains("Consider increasing timeout_ms"),
                $"execute_menu must use its own guidance, not the generic copy: {json}");
        }

        [Test]
        public static void TimeoutEnvelope_GenericTool_KeepsRaiseTimeoutAdvice()
        {
            // Other tools (compile-reload, big scans) genuinely may need more
            // time — keep the generic copy for them.
            var json = BridgeJson.BuildTimeoutEnvelope(
                "unity_open_mcp_reserialize", "enforce", 30000);
            StringAssert.Contains("Consider increasing timeout_ms", json,
                $"Generic tools should keep the raise-timeout advice: {json}");
        }

        [UnityTest]
        public static IEnumerator PathsHintRequired_EnvelopeHasAgentNextSteps()
        {
            return PostAndWait("/tools/unity_open_mcp_execute_csharp",
                "{\"code\":\"return 1;\",\"paths_hint\":[]}",
                body =>
                {
                    Assert.IsTrue(body.Contains("\"agentNextSteps\":["), $"Missing agentNextSteps in: {body}");
                    Assert.IsTrue(body.Contains("paths_hint"), $"agentNextSteps should mention paths_hint: {body}");
                });
        }

        [UnityTest]
        public static IEnumerator PathsHintRequired_EnvelopeContainsGateSection()
        {
            return PostAndWait("/tools/unity_open_mcp_execute_csharp",
                "{\"code\":\"return 1;\",\"paths_hint\":[]}",
                body =>
                {
                    Assert.IsTrue(body.Contains("\"gate\":{"), $"Missing gate section in: {body}");
                    Assert.IsTrue(body.Contains("\"mode\":\"enforce\""), $"Default gate mode should be enforce in: {body}");
                    Assert.IsTrue(body.Contains("\"skipped\":true"), $"Gate should be skipped on paths_hint error: {body}");
                });
        }

        // M22 T22.1.3 — per-call `logs` field. Every gate envelope carries a
        // `logs` array (empty [] when nothing was emitted). This is the shape
        // contract; the populated case is covered by the logs-acceptance test.
        [UnityTest]
        public static IEnumerator GateEnvelope_AlwaysCarriesLogsField()
        {
            return PostAndWait("/tools/unity_open_mcp_execute_csharp",
                "{\"code\":\"return 1;\",\"paths_hint\":[]}",
                body =>
                {
                    Assert.IsTrue(body.Contains("\"logs\":"),
                        $"Every gate envelope must carry a `logs` field. Got: {body}");
                });
        }

        // T22.1.3 acceptance: a mutation that emits a Unity warning surfaces it
        // inline in `logs` with the right severity + message. Uses gate:off so
        // the snippet runs without checkpoint/validation overhead; the warning
        // is captured regardless of gate mode (capture wraps the whole dispatch).
        [UnityTest]
        public static IEnumerator Logs_PopulatedWhenMutationEmitsWarning()
        {
            return PostAndWait("/tools/unity_open_mcp_execute_csharp",
                "{\"code\":\"Debug.LogWarning(\\\"MCP_TEST_WARNING_MARKER\\\"); return 1;\",\"paths_hint\":[\"Assets\"],\"gate\":\"off\"}",
                body =>
                {
                    Assert.IsTrue(body.Contains("\"logs\":"),
                        $"Missing logs field in: {body}");
                    Assert.IsTrue(body.Contains("\"severity\":\"warning\""),
                        $"Warning severity should appear in logs. Got: {body}");
                    Assert.IsTrue(body.Contains("MCP_TEST_WARNING_MARKER"),
                        $"The warning marker should appear in logs. Got: {body}");
                });
        }
    }
}
