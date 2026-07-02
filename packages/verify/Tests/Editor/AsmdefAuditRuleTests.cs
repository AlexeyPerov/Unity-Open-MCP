using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Rules;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class AsmdefAuditRuleTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/AsmdefAudit";

        private AsmdefAuditRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new AsmdefAuditRule();
        }

        [TearDown]
        public void TearDown()
        {
            // Each test writes its own .asmdef into the shared FixtureRoot but
            // never deletes it. A leftover .asmdef from the previous test makes
            // the folder contain multiple assembly definition files, which Unity
            // flags as an error on the next import. Clear the folder between
            // tests so every test starts from a clean single-asmdef state.
            ClearFolder(FixtureRoot);
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            EnsureDirectory(FixtureRoot);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (AssetDatabase.IsValidFolder(FixtureRoot))
            {
                AssetDatabase.DeleteAsset(FixtureRoot);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void Id_IsCorrect()
        {
            Assert.AreEqual("asmdef_audit", rule.Id);
        }

        [Test]
        public void Scan_EmptyPaths_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new string[0]);
            rule.Scan(scope, VerifyRunMode.Full, sink);
            Assert.AreEqual(0, sink.Count);
        }

        [Test]
        public void Scan_NonAsmdefPath_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Assets/SomeScript.cs" });
            rule.Scan(scope, VerifyRunMode.Full, sink);
            Assert.AreEqual(0, sink.Count);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_HealthyAsmdef_ProducesNoIssues()
        {
            var path = FixtureRoot + "/Healthy.asmdef";
            // includePlatforms narrows the build so the asmdef is not flagged as
            // asmdef_platform_filter_broad (a healthy asmdef scopes its platforms).
            File.WriteAllText(path, "{\n    \"name\": \"Healthy.Asmdef\",\n    \"rootNamespace\": \"Healthy\",\n    \"references\": [\"UnityEngine\"],\n    \"includePlatforms\": [\"Editor\"]\n}\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            Assert.AreEqual(0, sink.Count,
                $"Healthy asmdef must produce no issues. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_MissingName_ReportsMissingName()
        {
            var path = FixtureRoot + "/NoName.asmdef";
            File.WriteAllText(path, "{\n    \"references\": []\n}\n");
            // Unity logs an error for the missing required 'name' property. Register
            // the expectation BEFORE the import so LogAssert does not fail on it.
            LogAssert.Expect(LogType.Error, new Regex("."));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var missingName = sink.FirstOrDefault(i => i.IssueCode == "asmdef_missing_name");
            Assert.IsNotNull(missingName, $"Expected asmdef_missing_name. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Error, missingName.Severity);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_BrokenReference_ReportsBrokenReference()
        {
            var path = FixtureRoot + "/BrokenRef.asmdef";
            File.WriteAllText(path, "{\n    \"name\": \"BrokenRef.Asmdef\",\n    \"references\": [\"This.Assembly.Does.Not.Exist\"]\n}\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var broken = sink.FirstOrDefault(i => i.IssueCode == "broken_asmdef_reference");
            Assert.IsNotNull(broken, $"Expected broken_asmdef_reference. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Error, broken.Severity);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_BrokenGuidReference_ReportsBrokenReference()
        {
            var path = FixtureRoot + "/BrokenGuid.asmdef";
            File.WriteAllText(path, "{\n    \"name\": \"BrokenGuid.Asmdef\",\n    \"references\": [\"GUID:1234567890abcdef1234567890abcdef\"]\n}\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var broken = sink.FirstOrDefault(i => i.IssueCode == "broken_asmdef_reference");
            Assert.IsNotNull(broken, $"Expected broken_asmdef_reference for unresolved GUID. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_MalformedJson_ReportsMalformed()
        {
            var path = FixtureRoot + "/Malformed.asmdef";
            File.WriteAllText(path, "{ this is not valid json ");
            // Unity logs a JSON parse error for the malformed asmdef.
            LogAssert.Expect(LogType.Error, new Regex("."));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            Assert.GreaterOrEqual(sink.Count, 1, "Malformed asmdef must produce at least one issue.");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_DuplicateNames_ReportsDuplicateName()
        {
            var pathA = FixtureRoot + "/DupA.asmdef";
            var pathB = FixtureRoot + "/DupB.asmdef";
            File.WriteAllText(pathA, "{\n    \"name\": \"Same.Name\",\n    \"references\": []\n}\n");
            File.WriteAllText(pathB, "{\n    \"name\": \"Same.Name\",\n    \"references\": []\n}\n");
            // Unity logs errors: one for the duplicate assembly name, and one for
            // the folder containing multiple asmdef files. Expect both.
            LogAssert.Expect(LogType.Error, new Regex("."));
            LogAssert.Expect(LogType.Error, new Regex("."));
            AssetDatabase.ImportAsset(pathA, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(pathB, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { pathA, pathB });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var dup = sink.FirstOrDefault(i => i.IssueCode == "asmdef_duplicate_name");
            Assert.IsNotNull(dup, $"Expected asmdef_duplicate_name. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Error, dup.Severity);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_CircularReference_ReportsCycle()
        {
            // A -> B -> A cycle (name-based references).
            var pathA = FixtureRoot + "/CycleA.asmdef";
            var pathB = FixtureRoot + "/CycleB.asmdef";
            File.WriteAllText(pathA, "{\n    \"name\": \"CycleA.Asmdef\",\n    \"references\": [\"CycleB.Asmdef\"]\n}\n");
            File.WriteAllText(pathB, "{\n    \"name\": \"CycleB.Asmdef\",\n    \"references\": [\"CycleA.Asmdef\"]\n}\n");
            // Two asmdefs in one folder + a circular ref each log an error.
            LogAssert.Expect(LogType.Error, new Regex("."));
            LogAssert.Expect(LogType.Error, new Regex("."));
            AssetDatabase.ImportAsset(pathA, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(pathB, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { pathA, pathB });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var cycle = sink.FirstOrDefault(i => i.IssueCode == "asmdef_circular_reference");
            Assert.IsNotNull(cycle, $"Expected asmdef_circular_reference. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Error, cycle.Severity);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_EditorInRuntime_ReportsEditorRef()
        {
            // A runtime assembly (no Editor in includePlatforms) referencing an editor assembly.
            var path = FixtureRoot + "/EditorInRuntime.asmdef";
            File.WriteAllText(path, "{\n    \"name\": \"EditorInRuntime.Asmdef\",\n    \"references\": [\"Some.Editor.Plugin\"],\n    \"includePlatforms\": []\n}\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var editor = sink.FirstOrDefault(i => i.IssueCode == "asmdef_editor_in_runtime");
            Assert.IsNotNull(editor, $"Expected asmdef_editor_in_runtime. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            Assert.AreEqual(VerifySeverity.Warning, editor.Severity);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_PlatformFilterContradict_ReportsContradiction()
        {
            var path = FixtureRoot + "/Contradict.asmdef";
            File.WriteAllText(path, "{\n    \"name\": \"Contradict.Asmdef\",\n    \"references\": [],\n    \"includePlatforms\": [\"Standalone\"],\n    \"excludePlatforms\": [\"iOS\"]\n}\n");
            // Unity logs an error when both includePlatforms and excludePlatforms are set.
            LogAssert.Expect(LogType.Error, new Regex("."));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var contradict = sink.FirstOrDefault(i => i.IssueCode == "asmdef_platform_filter_contradict");
            Assert.IsNotNull(contradict, $"Expected asmdef_platform_filter_contradict. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_PlatformFilterBroad_ReportsBroadFilter()
        {
            var path = FixtureRoot + "/Broad.asmdef";
            // No platform filters at all + anyPlatform true.
            File.WriteAllText(path, "{\n    \"name\": \"Broad.Asmdef\",\n    \"references\": [],\n    \"includePlatforms\": [],\n    \"excludePlatforms\": []\n}\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var broad = sink.FirstOrDefault(i => i.IssueCode == "asmdef_platform_filter_broad");
            Assert.IsNotNull(broad, $"Expected asmdef_platform_filter_broad. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_AutoReferencedOrphan_ReportsOrphan()
        {
            // autoReferenced=false and no other scoped assembly references it.
            var path = FixtureRoot + "/Orphan.asmdef";
            File.WriteAllText(path, "{\n    \"name\": \"Orphan.Asmdef\",\n    \"references\": [],\n    \"autoReferenced\": false\n}\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var orphan = sink.FirstOrDefault(i => i.IssueCode == "asmdef_auto_referenced_orphan");
            Assert.IsNotNull(orphan, $"Expected asmdef_auto_referenced_orphan. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_VersionDefineInvalid_ReportsInvalidPackage()
        {
            var path = FixtureRoot + "/VersionDefine.asmdef";
            File.WriteAllText(path, "{\n    \"name\": \"VD.Asmdef\",\n    \"references\": [],\n    \"versionDefines\": [{\"name\":\"com.unity.somepackage\",\"expression\":\"1.0.0\",\"define\":\"FOO\"}]\n}\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var vd = sink.FirstOrDefault(i => i.IssueCode == "asmdef_version_define_invalid");
            Assert.IsNotNull(vd, $"Expected asmdef_version_define_invalid. Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_IssuesProduceValidKeys()
        {
            var path = FixtureRoot + "/Keys.asmdef";
            File.WriteAllText(path, "{\n    \"name\": \"Keys.Asmdef\",\n    \"references\": [\"This.DoesNotExist\"]\n}\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { path });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            foreach (var issue in sink)
            {
                var key = IssueKey.Build(issue);
                Assert.IsTrue(IssueKey.TryParse(key, out _, out _, out _, out _),
                    $"Issue key '{key}' must be valid");
            }
        }

        private static void EnsureDirectory(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureDirectory(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        private static void ClearFolder(string path)
        {
            if (!AssetDatabase.IsValidFolder(path)) return;
            foreach (var file in Directory.GetFiles(path, "*.asmdef"))
            {
                AssetDatabase.DeleteAsset(file);
            }
            AssetDatabase.Refresh();
        }
    }
}
