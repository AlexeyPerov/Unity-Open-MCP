using NUnit.Framework;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    public class PackagesToolsTests
    {
        // ----------------------- List -----------------------------------

        [Test]
        public void List_NoParameters_ReturnsOkEnvelope()
        {
            // Default offline list — every EditMode project has at least the
            // built-in packages (modules) so the envelope should succeed.
            var result = PackagesTools.List("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"packages\":[", result.Output);
            StringAssert.Contains("\"count\":", result.Output);
            StringAssert.Contains("\"total\":", result.Output);
            StringAssert.Contains("\"truncated\":", result.Output);
            StringAssert.Contains("\"offline\":true", result.Output);
        }

        [Test]
        public void List_BoundedMaxResults_ReportsTruncation()
        {
            var result = PackagesTools.List("{\"max_results\":1}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"count\":1", result.Output);
            // Truncated only fires when total > 1; every project has >1 pkg.
            StringAssert.Contains("\"truncated\":", result.Output);
        }

        // ----------------------- Search ---------------------------------

        [Test]
        public void Search_MissingQuery_ReturnsMissingParameter()
        {
            var result = PackagesTools.Search("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'query'", result.ErrorMessage);
        }

        [Test]
        public void Search_EmptyQuery_ReturnsMissingParameter()
        {
            var result = PackagesTools.Search("{\"query\":\"   \"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void Search_OfflineNonMatching_ReturnsEmptyResults()
        {
            // Offline cached search for a package id that cannot exist —
            // expects an ok envelope with an empty results array (the
            // registry never has this name).
            var result = PackagesTools.Search(
                "{\"query\":\"__mcp_test_no_such_pkg_xyz\",\"offline\":true,\"max_results\":5}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"results\":[", result.Output);
            // No package matches → results stays empty.
            StringAssert.Contains("\"count\":0", result.Output);
        }

        // ----------------------- GetInfo --------------------------------

        [Test]
        public void GetInfo_MissingName_ReturnsMissingParameter()
        {
            var result = PackagesTools.GetInfo("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'name'", result.ErrorMessage);
        }

        [Test]
        public void GetInfo_EmptyName_ReturnsMissingParameter()
        {
            var result = PackagesTools.GetInfo("{\"name\":\"\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void GetInfo_OfflineNotInstalled_ReturnsPackageNotFound()
        {
            var result = PackagesTools.GetInfo(
                "{\"name\":\"__mcp_test_no_such_pkg\",\"offline\":true}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("package_not_found", result.ErrorCode);
            StringAssert.Contains("offline:false", result.ErrorMessage);
        }

        // ----------------------- Add ------------------------------------

        [Test]
        public void Add_MissingPackageId_ReturnsMissingParameter()
        {
            var result = PackagesTools.Add("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'package_id'", result.ErrorMessage);
        }

        [Test]
        public void Add_EmptyPackageId_ReturnsMissingParameter()
        {
            var result = PackagesTools.Add("{\"package_id\":\"  \"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        // ----------------------- Remove ---------------------------------

        [Test]
        public void Remove_MissingPackageId_ReturnsMissingParameter()
        {
            var result = PackagesTools.Remove("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'package_id'", result.ErrorMessage);
        }

        [Test]
        public void Remove_NotInstalled_ReturnsPackageNotFound()
        {
            // The bridge project obviously doesn't have this package.
            var result = PackagesTools.Remove(
                "{\"package_id\":\"__mcp.test.no.such.pkg\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("package_not_found", result.ErrorCode);
            StringAssert.Contains("package_list", result.ErrorMessage);
        }

        // ----------------------- Check ----------------------------------

        [Test]
        public void Check_MissingPackageId_ReturnsMissingParameter()
        {
            var result = PackagesTools.Check("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'package_id'", result.ErrorMessage);
        }

        [Test]
        public void Check_NotInManifest_ReturnsInstalledFalse()
        {
            var result = PackagesTools.Check(
                "{\"package_id\":\"__mcp.test.no.such.pkg\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"installed\":false", result.Output);
            StringAssert.Contains("\"reference\":null", result.Output);
        }

        [Test]
        public void Check_VersionedInput_StripsAtSuffix()
        {
            // A versioned id is accepted; the check runs on the name half.
            var result = PackagesTools.Check(
                "{\"package_id\":\"__mcp.test.no.such.pkg@1.2.3\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"name\":\"__mcp.test.no.such.pkg\"", result.Output);
            StringAssert.Contains("\"installed\":false", result.Output);
        }

        // ----------------------- GetDependencies ------------------------

        [Test]
        public void GetDependencies_ReturnsManifestEntries()
        {
            var result = PackagesTools.GetDependencies("{}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"manifestPath\":\"Packages/manifest.json\"", result.Output);
            StringAssert.Contains("\"count\":", result.Output);
            StringAssert.Contains("\"dependencies\":[", result.Output);
            // Every EditMode test project ships com.unity.* packages in its
            // manifest (the bridge package itself depends on UPM modules).
            // We don't assert a specific name to avoid coupling to the
            // fixture's manifest; we just assert the envelope shape.
        }

        // ------------------------- ExtractDependencies ------------------
        //
        // Pure helper used by both get_dependencies and check. Drives the
        // hand-rolled manifest parser directly so the contract is pinned
        // without spinning up file IO or UPM.

        [Test]
        public void ExtractDependencies_ParsesSimpleManifest()
        {
            const string manifest = @"{
  ""dependencies"": {
    ""com.unity.textmeshpro"": ""3.0.6"",
    ""com.unity.cinemachine"": ""https://github.com/Unity-Technologies/com.unity.cinemachine.git"",
    ""com.alexeyperov.unity-open-mcp-bridge"": ""file:../packages/bridge""
  }
}";
            var deps = PackagesTools.ExtractDependencies(manifest);
            Assert.AreEqual(3, deps.Count);
            Assert.AreEqual("3.0.6", deps["com.unity.textmeshpro"]);
            Assert.AreEqual("https://github.com/Unity-Technologies/com.unity.cinemachine.git",
                deps["com.unity.cinemachine"]);
            Assert.AreEqual("file:../packages/bridge",
                deps["com.alexeyperov.unity-open-mcp-bridge"]);
        }

        [Test]
        public void ExtractDependencies_EmptyManifest_ReturnsEmptyDict()
        {
            var deps = PackagesTools.ExtractDependencies("{}");
            Assert.AreEqual(0, deps.Count);
        }

        [Test]
        public void ExtractDependencies_MissingDependenciesBlock_ReturnsEmptyDict()
        {
            var deps = PackagesTools.ExtractDependencies(
                "{\"dependencies\": {}}");
            Assert.AreEqual(0, deps.Count);
        }

        [Test]
        public void ExtractDependencies_HandlesEscapedStrings()
        {
            // A reference with an escaped quote — the parser must decode it.
            const string manifest =
                "{\"dependencies\":{\"com.foo.bar\":\"1.0\\\\\\\"0\"}}";
            var deps = PackagesTools.ExtractDependencies(manifest);
            Assert.AreEqual(1, deps.Count);
            Assert.AreEqual("1.0\\\"0", deps["com.foo.bar"]);
        }

        [Test]
        public void ExtractDependencies_HandlesNestedObjectsInOtherFields()
        {
            // scopedRegistries is a nested object — must not confuse the
            // dependency-block extractor.
            const string manifest = @"{
  ""scopedRegistries"": {
    ""example"": { ""url"": ""https://example.com"", ""scopes"": [""com.example""] }
  },
  ""dependencies"": {
    ""com.example.foo"": ""1.0.0""
  }
}";
            var deps = PackagesTools.ExtractDependencies(manifest);
            Assert.AreEqual(1, deps.Count);
            Assert.AreEqual("1.0.0", deps["com.example.foo"]);
        }

        // ----------------------- Dispatch wiring ------------------------
        //
        // KnownTools / DirectResponseTools / MutatingTools membership is
        // the contract that lets the dispatcher route a package tool. We
        // assert the lifecycle + dirty-guard contracts so a future edit
        // that forgets to wire a new package tool fails loudly here.

        [Test]
        public void Lifecycle_PackageAddRemoveAreRestartThenSettle_ReadsNone()
        {
            Assert.AreEqual(LifecyclePolicy.RestartThenSettle,
                ToolLifecycle.Resolve("unity_open_mcp_package_add"));
            Assert.AreEqual(LifecyclePolicy.RestartThenSettle,
                ToolLifecycle.Resolve("unity_open_mcp_package_remove"));
            // Read-only package tools default to None (safe / no settle).
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_package_list"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_package_search"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_package_get_info"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_package_get_dependencies"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_package_check"));
        }

        [Test]
        public void DirtyGuard_PreflightsPackageAddRemove()
        {
            // add / remove can domain-reload, so they get the dirty guard.
            Assert.IsTrue(SceneDirtyGuard.AppliesTo("unity_open_mcp_package_add", "{}"),
                "package_add must be guarded (RestartThenSettle lifecycle)");
            Assert.IsTrue(SceneDirtyGuard.AppliesTo("unity_open_mcp_package_remove", "{}"),
                "package_remove must be guarded (RestartThenSettle lifecycle)");
            // Read-only package tools are never guarded.
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_package_list", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_package_search", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_package_get_info", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_package_get_dependencies", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_package_check", "{}"));
        }

        [Test]
        public void DirtyGuard_PackageAddRemove_IgnoreSceneDirtyOptOut()
        {
            Assert.IsFalse(SceneDirtyGuard.AppliesTo(
                "unity_open_mcp_package_add", "{\"ignore_scene_dirty\":true}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo(
                "unity_open_mcp_package_remove", "{\"ignore_scene_dirty\":true}"));
        }

        // ----------------------- ReimportPackage ------------------------

        [Test]
        public void ReimportPackage_MissingPackageId_ReturnsMissingParameter()
        {
            var result = PackagesTools.ReimportPackage("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'package_id'", result.ErrorMessage);
        }

        [Test]
        public void ReimportPackage_EmptyPackageId_ReturnsMissingParameter()
        {
            var result = PackagesTools.ReimportPackage("{\"package_id\":\"   \"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void ReimportPackage_UnknownPackage_ReturnsPackageNotFound()
        {
            // A package id that cannot exist — CollectInstalled succeeds but
            // MatchesPackage finds nothing.
            var result = PackagesTools.ReimportPackage(
                "{\"package_id\":\"com.nonexistent.fake.reimport.target\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("package_not_found", result.ErrorCode);
        }

        [Test]
        public void ReimportPackage_RegistryPackage_ReturnsNotLocalPackageEnvelope()
        {
            // Every EditMode project has com.unity.modules.ui installed as a
            // built-in/registry package — exercising the not_local_package
            // branch. Returns Success with a structured redirect to
            // assets_refresh (non-local packages have nothing outside Assets/).
            var result = PackagesTools.ReimportPackage(
                "{\"package_id\":\"com.unity.modules.ui\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"not_local_package\"", result.Output);
            StringAssert.Contains("\"source\":", result.Output);
            // The recovery branch must surface structured guidance.
            StringAssert.Contains("\"agentNextSteps\":", result.Output);
            StringAssert.Contains("assets_refresh", result.Output);
        }

        [Test]
        public void DirtyGuard_ReimportPackage_IsGuarded_RestartThenSettleLifecycle()
        {
            // reimport_package forces a reimport + RequestScriptCompilation
            // (RestartThenSettle) and so must be guarded like package_add /
            // package_remove. The ignore_scene_dirty opt-out mirrors them.
            Assert.IsTrue(SceneDirtyGuard.AppliesTo(
                "unity_open_mcp_reimport_package", "{\"package_id\":\"com.x.y\"}"),
                "reimport_package must be guarded (RestartThenSettle lifecycle)");
            Assert.IsFalse(SceneDirtyGuard.AppliesTo(
                "unity_open_mcp_reimport_package",
                "{\"package_id\":\"com.x.y\",\"ignore_scene_dirty\":true}"));
        }
    }
}
