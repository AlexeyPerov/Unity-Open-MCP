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
