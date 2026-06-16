using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace UnityOpenMcpBridge.Tests
{
    // TEMPORARILY DISABLED — re-enable as part of T2.5 (EditMode test-suite
    // speed-up, specs/execution/M12/execution-plan-3-rules-wave2-fixes.md).
    // These tests hard-require a live bridge listening on :19120; when the
    // bridge is down each fails after a 10s HttpClient timeout, burning the
    // entire 30s suite budget. [Explicit] keeps them runnable by name but
    // excludes them from suite runs until bridge lifecycle is fixed in T2.5.
    [Explicit]
    public class BridgeHttpServerTests
    {
        static readonly string BaseUrl = $"http://127.0.0.1:19120";
        static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

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
            Assert.IsTrue(body.Contains("\"bridgeVersion\":\"0.1.0\""), $"Unexpected version in: {body}");
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

        [Test]
        public static void MutatingTool_EmptyPathsHint_ReturnsPathsHintRequired()
        {
            var json = "{\"code\":\"return 1;\",\"paths_hint\":[]}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_execute_csharp", content).Result;
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"paths_hint_required\""), $"Expected paths_hint_required error in: {body}");
            Assert.IsTrue(body.Contains("\"success\":false"), $"Expected success:false in: {body}");
            Assert.IsTrue(body.Contains("\"skipped\":true"), $"Expected gate skipped in: {body}");
        }

        [Test]
        public static void MutatingTool_MissingPathsHint_ReturnsPathsHintRequired()
        {
            var json = "{\"code\":\"return 1;\"}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_execute_csharp", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"paths_hint_required\""), $"Expected paths_hint_required in: {body}");
        }

        [Test]
        public static void InvokeMethod_EmptyPathsHint_ReturnsPathsHintRequired()
        {
            var json = "{\"type_name\":\"System.Environment\",\"method_name\":\"get_TickCount\",\"is_static\":true,\"paths_hint\":[]}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_invoke_method", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"paths_hint_required\""), $"Expected paths_hint_required in: {body}");
        }

        [Test]
        public static void ExecuteMenu_EmptyPathsHint_NonAllowlisted_ReturnsPathsHintRequired()
        {
            var json = "{\"menu_path\":\"File/Save Project\",\"paths_hint\":[]}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_execute_menu", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"paths_hint_required\""), $"Expected paths_hint_required for non-allowlisted menu in: {body}");
        }

        [Test]
        public static void ExecuteMenu_EmptyPathsHint_Allowlisted_Proceeds()
        {
            var json = "{\"menu_path\":\"Assets/Refresh\",\"paths_hint\":[]}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_execute_menu", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsFalse(body.Contains("\"paths_hint_required\""), $"Allowlisted menu should not return paths_hint_required: {body}");
        }

        [Test]
        public static void FindMembers_DoesNotRequirePathsHint()
        {
            var json = "{\"query\":\"Transform\",\"kind\":\"type\",\"max_results\":5}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_find_members", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsFalse(body.Contains("\"paths_hint_required\""), $"find_members should not require paths_hint: {body}");
        }

        [Test]
        public static void PathsHintRequired_EnvelopeHasAgentNextSteps()
        {
            var json = "{\"code\":\"return 1;\",\"paths_hint\":[]}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_execute_csharp", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"agentNextSteps\":["), $"Missing agentNextSteps in: {body}");
            Assert.IsTrue(body.Contains("paths_hint"), $"agentNextSteps should mention paths_hint: {body}");
        }

        [Test]
        public static void PathsHintRequired_EnvelopeContainsGateSection()
        {
            var json = "{\"code\":\"return 1;\",\"paths_hint\":[]}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_execute_csharp", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"gate\":{"), $"Missing gate section in: {body}");
            Assert.IsTrue(body.Contains("\"mode\":\"enforce\""), $"Default gate mode should be enforce in: {body}");
            Assert.IsTrue(body.Contains("\"skipped\":true"), $"Gate should be skipped on paths_hint error: {body}");
        }
    }
}
