using System;
using System.Net;
using System.Net.Http;
using System.Text;
using NUnit.Framework;

namespace UnityOpenMcpBridge.Tests
{
    public class TypedToolDispatchTests
    {
        static readonly string BaseUrl = $"http://127.0.0.1:19120";
        static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        [Test]
        public static void EditorStatus_DispatchesViaRegistry()
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_editor_status", content).Result;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"success\":true"), $"Expected success in: {body}");
            Assert.IsTrue(body.Contains("\"isPlaying\""), $"Expected isPlaying field in: {body}");
            Assert.IsTrue(body.Contains("\"unityVersion\""), $"Expected unityVersion field in: {body}");
        }

        [Test]
        public static void EditorStatus_NonMutating_GateSkipped()
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_editor_status", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"skipped\":true"), $"Non-mutating tool should have gate skipped: {body}");
        }

        [Test]
        public static void EditorStatus_NoPathsHintRequired()
        {
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_editor_status", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsFalse(body.Contains("paths_hint_required"), $"Read-only tool should not require paths_hint: {body}");
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
        static readonly string BaseUrl = $"http://127.0.0.1:19120";
        static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

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

        [Test]
        public static void GetResourceByRoute_DispatchesCorrectly()
        {
            var response = HttpClient.GetAsync($"{BaseUrl}/resources/unity-open-mcp://test/resource").Result;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"test\":true"), $"Expected test resource content: {body}");
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
        static readonly string BaseUrl = $"http://127.0.0.1:19120";
        static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

        [Test]
        public static void MetaTools_Unaffected_ExecuteCsharpStillWorks()
        {
            var json = "{\"code\":\"return 1;\",\"paths_hint\":[\"Assets/Foo.cs\"]}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_execute_csharp", content).Result;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"success\":true"), $"Meta-tool should still work: {body}");
        }

        [Test]
        public static void MetaTools_Unaffected_FindMembersNoPathsHint()
        {
            var json = "{\"query\":\"Transform\",\"kind\":\"type\",\"max_results\":5}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_find_members", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsFalse(body.Contains("paths_hint_required"),
                $"find_members should not require paths_hint: {body}");
        }

        [Test]
        public static void MetaTools_Unaffected_MutatingPathsHintRequired()
        {
            var json = "{\"code\":\"return 1;\",\"paths_hint\":[]}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/unity_open_mcp_execute_csharp", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("paths_hint_required"),
                $"execute_csharp should still require paths_hint: {body}");
        }

        [Test]
        public static void MutatingTypedTool_WithoutPathsHint_ReturnsPathsHintRequired()
        {
            var json = "{\"paths_hint\":[]}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/test_mutating_tool", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("paths_hint_required"),
                $"Mutating typed tool should require paths_hint: {body}");
        }

        [Test]
        public static void MutatingTypedTool_WithPathsHint_Dispatches()
        {
            var json = "{\"paths_hint\":[\"Assets/Test.cs\"]}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/test_mutating_tool", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"success\":true"),
                $"Mutating typed tool with paths_hint should dispatch: {body}");
        }

        [Test]
        public static void MutatingTypedTool_GateWarn_DefaultFromAttribute()
        {
            var json = "{\"paths_hint\":[\"Assets/Test.cs\"]}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/test_mutating_warn_tool", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"mode\":\"warn\""),
                $"Gate mode should come from attribute default (warn): {body}");
        }

        [Test]
        public static void MutatingTypedTool_RuntimeGateOverride()
        {
            var json = "{\"paths_hint\":[\"Assets/Test.cs\"],\"gate\":\"off\"}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = HttpClient.PostAsync($"{BaseUrl}/tools/test_mutating_tool", content).Result;

            var body = response.Content.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("\"mode\":\"off\""),
                $"Runtime gate=off should override attribute default: {body}");
        }
    }
}
