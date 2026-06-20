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
    public class TypedToolDispatchTests
    {
        // Bridge listens on the per-project port resolved at Editor load time
        // (BridgeHttpServer.Port), not a fixed 19120. Read it dynamically so
        // the tests work whether or not UNITY_OPEN_MCP_BRIDGE_PORT is set.
        // [SetUp] skips cleanly when the listener isn't up instead of
        // exhausting the suite budget on 10s-per-test HttpClient timeouts.
        private static string BaseUrl =>
            $"http://127.0.0.1:{BridgeHttpServer.Port}";
        private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        [SetUp]
        public void EnsureBridgeRunning()
        {
            if (!BridgeHttpServer.IsRunning)
                Assert.Ignore("Bridge HTTP listener is not running — skipping HTTP integration tests.");
        }

        // Tool dispatch routes through MainThreadDispatcher (pumps on
        // EditorApplication.update), so these run as [UnityTest] coroutines:
        // the HTTP call runs on a ThreadPool thread while the coroutine
        // yields, letting update pump the dispatch queue. A synchronous
        // [Test] would block the main thread and time out.

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
        public static IEnumerator EditorStatus_DispatchesViaRegistry()
        {
            return PostAndWait("/tools/unity_open_mcp_editor_status", "{}", body =>
            {
                Assert.IsTrue(body.Contains("\"success\":true"), $"Expected success in: {body}");
                Assert.IsTrue(body.Contains("\"isPlaying\""), $"Expected isPlaying field in: {body}");
                Assert.IsTrue(body.Contains("\"unityVersion\""), $"Expected unityVersion field in: {body}");
            });
        }

        [UnityTest]
        public static IEnumerator EditorStatus_NonMutating_GateSkipped()
        {
            return PostAndWait("/tools/unity_open_mcp_editor_status", "{}",
                body => Assert.IsTrue(body.Contains("\"skipped\":true"), $"Non-mutating tool should have gate skipped: {body}"));
        }

        [UnityTest]
        public static IEnumerator EditorStatus_NoPathsHintRequired()
        {
            return PostAndWait("/tools/unity_open_mcp_editor_status", "{}",
                body => Assert.IsFalse(body.Contains("paths_hint_required"), $"Read-only tool should not require paths_hint: {body}"));
        }

        [Test]
        public static void UnknownTypedTool_ReturnsToolNotFound()
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_nonexistent_typed", content).Result;
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"tool_not_found\""));
        }
    }

    public class ResourceEndpointTests
    {
        private static string BaseUrl =>
            $"http://127.0.0.1:{BridgeHttpServer.Port}";
        private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        [SetUp]
        public void EnsureBridgeRunning()
        {
            if (!BridgeHttpServer.IsRunning)
                Assert.Ignore("Bridge HTTP listener is not running — skipping HTTP integration tests.");
        }

        [Test]
        public static void GetResources_ReturnsJsonArray()
        {
            var response = HttpClient.GetAsync($"{BaseUrl}/resources").Result;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.StartsWith("["), $"Expected JSON array: {body}");
        }

        [Test]
        public static void GetResources_ContainsTestResource()
        {
            var response = HttpClient.GetAsync($"{BaseUrl}/resources").Result;
            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("unity-open-mcp://test/resource"),
                $"Resource list should contain test resource: {body}");
        }

        // Resource dispatch routes through MainThreadDispatcher — must be a
        // [UnityTest] coroutine so update can pump while the HTTP call runs.
        private static IEnumerator GetAndWait(string path, Action<string> assertBody)
        {
            var task = HttpClient.GetAsync($"{BaseUrl}{path}");
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
                Assert.Fail($"HTTP request faulted: {task.Exception?.GetBaseException()?.Message}");
            var response = task.Result;
            var body = response.Content.ReadAsStringAsync().Result;
            assertBody(body);
        }

        [UnityTest]
        public static IEnumerator GetResourceByRoute_DispatchesCorrectly()
        {
            return GetAndWait("/resources/unity-open-mcp://test/resource",
                body => Assert.IsTrue(body.Contains("\"test\":true"), $"Expected test resource content: {body}"));
        }

        [Test]
        public static void GetResourceByRoute_UnknownRoute_Returns404()
        {
            var response = HttpClient.GetAsync($"{BaseUrl}/resources/unity-open-mcp://nonexistent").Result;
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"resource_not_found\""), $"Expected resource_not_found: {body}");
        }
    }

    public class GateIntegrationTypedToolTests
    {
        private static string BaseUrl =>
            $"http://127.0.0.1:{BridgeHttpServer.Port}";
        private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        [SetUp]
        public void EnsureBridgeRunning()
        {
            if (!BridgeHttpServer.IsRunning)
                Assert.Ignore("Bridge HTTP listener is not running — skipping HTTP integration tests.");
        }

        // All of these dispatch tools through MainThreadDispatcher, so they
        // run as [UnityTest] coroutines (see TypedToolDispatchTests for why).
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
        public static IEnumerator MetaTools_Unaffected_ExecuteCsharpStillWorks()
        {
            return PostAndWait("/tools/unity_open_mcp_execute_csharp",
                "{\"code\":\"return 1;\",\"paths_hint\":[\"Assets/Foo.cs\"]}",
                body => Assert.IsTrue(body.Contains("\"success\":true"), $"Meta-tool should still work: {body}"));
        }

        [UnityTest]
        public static IEnumerator MetaTools_Unaffected_FindMembersNoPathsHint()
        {
            return PostAndWait("/tools/unity_open_mcp_find_members",
                "{\"query\":\"Transform\",\"kind\":\"type\",\"max_results\":5}",
                body => Assert.IsFalse(body.Contains("paths_hint_required"),
                    $"find_members should not require paths_hint: {body}"));
        }

        [UnityTest]
        public static IEnumerator MetaTools_Unaffected_MutatingPathsHintRequired()
        {
            return PostAndWait("/tools/unity_open_mcp_execute_csharp",
                "{\"code\":\"return 1;\",\"paths_hint\":[]}",
                body => Assert.IsTrue(body.Contains("paths_hint_required"),
                    $"execute_csharp should still require paths_hint: {body}"));
        }

        [UnityTest]
        public static IEnumerator MutatingTypedTool_WithoutPathsHint_ReturnsPathsHintRequired()
        {
            return PostAndWait("/tools/test_mutating_tool",
                "{\"paths_hint\":[]}",
                body => Assert.IsTrue(body.Contains("paths_hint_required"),
                    $"Mutating typed tool should require paths_hint: {body}"));
        }

        [UnityTest]
        public static IEnumerator MutatingTypedTool_WithPathsHint_Dispatches()
        {
            return PostAndWait("/tools/test_mutating_tool",
                "{\"paths_hint\":[\"Assets/Test.cs\"]}",
                body => Assert.IsTrue(body.Contains("\"success\":true"),
                    $"Mutating typed tool with paths_hint should dispatch: {body}"));
        }

        [UnityTest]
        public static IEnumerator MutatingTypedTool_GateWarn_DefaultFromAttribute()
        {
            return PostAndWait("/tools/test_mutating_warn_tool",
                "{\"paths_hint\":[\"Assets/Test.cs\"]}",
                body => Assert.IsTrue(body.Contains("\"mode\":\"warn\""),
                    $"Gate mode should come from attribute default (warn): {body}"));
        }

        [UnityTest]
        public static IEnumerator MutatingTypedTool_RuntimeGateOverride()
        {
            return PostAndWait("/tools/test_mutating_tool",
                "{\"paths_hint\":[\"Assets/Test.cs\"],\"gate\":\"off\"}",
                body => Assert.IsTrue(body.Contains("\"mode\":\"off\""),
                    $"Runtime gate=off should override attribute default: {body}"));
        }
    }
}
