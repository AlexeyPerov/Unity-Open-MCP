using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Fixes;

namespace UnityOpenMcpVerify.Tests
{
    // T2.4 fix-provider tests for relink_broken_guid.
    //
    // The pure CanFix/Describe/Safe cases are plain [Test]s (fast, no fixtures).
    // The end-to-end rewrite scenario is a [UnityTest] that builds a prefab with
    // a broken GUID and verifies Apply() rewires it onto a chosen target. The
    // fixture folder is created/torn down once per fixture run.
    [TestFixture]
    public class RelinkBrokenGuidFixTests
    {
        const string FixtureRoot = "Assets/Tests/VerifyFixtures/RelinkBrokenGuid";

        RelinkBrokenGuidFix fix;

        [SetUp]
        public void SetUp()
        {
            fix = new RelinkBrokenGuidFix();
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

        // -------------------------------------------------------------------
        // FixId / CanFix — pure, fast
        // -------------------------------------------------------------------

        [Test]
        public void FixId_IsRelinkBrokenGuid()
        {
            Assert.AreEqual("relink_broken_guid", fix.FixId);
        }

        [Test]
        public void CanFix_MissingGuidIssue_ReturnsTrue()
        {
            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/A.prefab", "missing_guid");

            Assert.IsTrue(fix.CanFix(issueId));
        }

        [Test]
        public void CanFix_BrokenDependencyIssue_ReturnsTrue()
        {
            var issueId = IssueKey.Build(
                "dependencies", VerifySeverity.Error,
                "Assets/A.prefab", "broken_dependency");

            Assert.IsTrue(fix.CanFix(issueId));
        }

        [Test]
        public void CanFix_MissingScriptIssue_ReturnsFalse()
        {
            // missing_script belongs to remove_missing_script — never relink.
            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/A.prefab", "missing_script");

            Assert.IsFalse(fix.CanFix(issueId));
        }

        [Test]
        public void CanFix_UnrelatedRule_ReturnsFalse()
        {
            var issueId = IssueKey.Build(
                "scene_prefab_health", VerifySeverity.Warning,
                "Assets/A.unity", "deep_nesting");

            Assert.IsFalse(fix.CanFix(issueId));
        }

        [Test]
        public void CanFix_MalformedIssueId_ReturnsFalse()
        {
            Assert.IsFalse(fix.CanFix("garbage"));
            Assert.IsFalse(fix.CanFix(null));
            Assert.IsFalse(fix.CanFix(""));
        }

        // -------------------------------------------------------------------
        // Describe — Safe=false always, candidates advertised
        // -------------------------------------------------------------------

        [Test]
        public void Describe_IsNeverSafe()
        {
            // Relinking rewires the asset graph based on an agent's choice —
            // a wrong pick silently corrupts references. Safe must always be
            // false regardless of asset extension.
            var prefabIssue = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/A.prefab", "missing_guid");
            var sceneIssue = IssueKey.Build(
                "dependencies", VerifySeverity.Error,
                "Assets/S.unity", "broken_dependency");

            Assert.IsFalse(fix.Describe(prefabIssue).Safe,
                "prefab relink must be Safe=false");
            Assert.IsFalse(fix.Describe(sceneIssue).Safe,
                "scene relink must be Safe=false");
        }

        [Test]
        public void Describe_NonexistentAsset_ExplainsNoCandidates()
        {
            // A broken GUID on an asset that does not exist on disk should
            // not crash; Describe returns guidance pointing at find_references.
            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/__DoesNotExist__.prefab", "missing_guid");

            var desc = fix.Describe(issueId);

            Assert.AreEqual("relink_broken_guid", desc.FixId);
            StringAssert.Contains("find_references", desc.Description);
        }

        // -------------------------------------------------------------------
        // Apply — argument validation paths (no fixtures needed)
        // -------------------------------------------------------------------

        [Test]
        public void Apply_MalformedIssueId_Fails()
        {
            var result = fix.Apply("garbage");

            Assert.IsFalse(result.Success);
            StringAssert.Contains("Cannot parse", result.Description);
        }

        [Test]
        public void Apply_WithoutTargetGuid_ExplainsRequirement()
        {
            // Apply with no chosen target must refuse, never guess.
            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/__DoesNotExist__.prefab", "missing_guid");

            var result = fix.Apply(issueId);

            Assert.IsFalse(result.Success);
            StringAssert.Contains("target_guid", result.Description);
        }

        [Test]
        public void Apply_MalformedTargetGuid_Fails()
        {
            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/__DoesNotExist__.prefab", "missing_guid");

            var result = fix.Apply(issueId, "not-a-guid");

            Assert.IsFalse(result.Success);
            StringAssert.Contains("not a valid 32-hex", result.Description);
        }

        // -------------------------------------------------------------------
        // Registry wiring — provider registers and advertises correctly
        // -------------------------------------------------------------------

        [Test]
        public void Registry_AdvertisesRelinkBrokenGuid()
        {
            CollectionAssert.Contains(
                FixProviderRegistry.AvailableFixIds(),
                "relink_broken_guid");
        }

        [Test]
        public void Registry_TryGetFixInfo_MissingGuid_ReturnsUnsafeFix()
        {
            // Critical: previously TryGetFixInfo hardwired safe=true, which
            // would have advertised relink_broken_guid as auto-applyable.
            // It must now surface the real Safe flag (false).
            var ok = FixProviderRegistry.TryGetFixInfo(
                "missing_references", "missing_guid",
                out var fixId, out var safe);

            // remove_missing_script registers first, but it does not match
            // missing_guid — only relink_broken_guid does.
            Assert.IsTrue(ok, "expected a fix for missing_references/missing_guid");
            Assert.AreEqual("relink_broken_guid", fixId);
            Assert.IsFalse(safe, "relink_broken_guid must surface as Safe=false");
        }

        [Test]
        public void Registry_TryGetFixInfo_BrokenDependency_ReturnsUnsafeFix()
        {
            var ok = FixProviderRegistry.TryGetFixInfo(
                "dependencies", "broken_dependency",
                out var fixId, out var safe);

            Assert.IsTrue(ok);
            Assert.AreEqual("relink_broken_guid", fixId);
            Assert.IsFalse(safe);
        }

        [Test]
        public void Registry_TryGetFixInfo_MissingScript_StillSafe()
        {
            // Regression guard: remove_missing_script must keep surfacing as
            // safe on .prefab (the SyntheticKey path uses __test__.prefab).
            var ok = FixProviderRegistry.TryGetFixInfo(
                "missing_references", "missing_script",
                out var fixId, out var safe);

            Assert.IsTrue(ok);
            Assert.AreEqual("remove_missing_script", fixId);
            Assert.IsTrue(safe, "remove_missing_script on .prefab must remain Safe=true");
        }

        [Test]
        public void Registry_FixesForIssue_ReturnsMatchingProviders()
        {
            var missingGuidIssue = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/A.prefab", "missing_guid");
            var brokenDepIssue = IssueKey.Build(
                "dependencies", VerifySeverity.Error,
                "Assets/A.prefab", "broken_dependency");
            var missingScriptIssue = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/A.prefab", "missing_script");
            var unrelatedIssue = IssueKey.Build(
                "scene_prefab_health", VerifySeverity.Warning,
                "Assets/A.unity", "deep_nesting");

            CollectionAssert.AreEquivalent(
                new[] { "relink_broken_guid" },
                FixProviderRegistry.FixesForIssue(missingGuidIssue));
            CollectionAssert.AreEquivalent(
                new[] { "relink_broken_guid" },
                FixProviderRegistry.FixesForIssue(brokenDepIssue));
            CollectionAssert.AreEquivalent(
                new[] { "remove_missing_script" },
                FixProviderRegistry.FixesForIssue(missingScriptIssue));
            Assert.IsEmpty(FixProviderRegistry.FixesForIssue(unrelatedIssue));
        }

        // -------------------------------------------------------------------
        // End-to-end fixture — build a prefab with a broken GUID, rewrite it
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator Apply_WithValidTargetGuid_RewritesAndReimports()
        {
            // Build a real mesh asset (gives us a valid GUID to relink onto),
            // a prefab with a valid mesh reference, then corrupt the prefab's
            // YAML so the mesh reference points at a fake GUID. Apply() with
            // the real mesh GUID should restore the reference.
            var meshPath = FixtureRoot + "/TargetMesh.asset";
            var prefabPath = FixtureRoot + "/BrokenPrefab.prefab";
            yield return CreatePrefabWithMeshReference(prefabPath, meshPath);

            Assume.That(File.Exists(prefabPath), Is.True, "prefab must exist");
            Assume.That(File.Exists(meshPath), Is.True, "mesh must exist");

            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                AssetDatabase.LoadAssetAtPath<Mesh>(meshPath), out var realGuid, out _);
            Assume.That(string.IsNullOrEmpty(realGuid), Is.False,
                "mesh must produce a real GUID to relink onto");

            // Corrupt the prefab: replace the real mesh GUID with a fake one.
            InjectBrokenGuid(prefabPath, realGuid, "deadbeefdeadbeefdeadbeefdeadbeef");
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
            yield return null;

            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                prefabPath, "missing_guid");

            var result = fix.Apply(issueId, realGuid);

            Assert.IsTrue(result.Success,
                $"Apply should succeed. Got: {result.Description}");
            Assert.That(result.TouchedPaths, Does.Contain(prefabPath));

            // The rewritten prefab must now reference the real mesh GUID, not the
            // fake one. Read the file and confirm.
            var rewritten = File.ReadAllText(prefabPath);
            Assert.IsTrue(rewritten.Contains($"guid: {realGuid}"),
                "rewritten prefab must carry the real mesh GUID");
            Assert.IsFalse(rewritten.Contains("guid: deadbeefdeadbeefdeadbeefdeadbeef"),
                "fake GUID must be gone after rewrite");
        }

        // -------------------------------------------------------------------
        // Fixture helpers (mirrors MissingReferencesRuleTests patterns)
        // -------------------------------------------------------------------

        static System.Collections.IEnumerator CreatePrefabWithMeshReference(
            string prefabPath, string meshPath)
        {
            EnsureDirectory(Path.GetDirectoryName(prefabPath));

            var mesh = new Mesh();
            mesh.vertices = new[] {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(0, 1, 0),
            };
            mesh.triangles = new[] { 0, 1, 2 };
            AssetDatabase.CreateAsset(mesh, meshPath);

            var go = new GameObject("RelinkFixture");
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            Object.DestroyImmediate(go);
            AssetDatabase.Refresh();
            yield return null;
        }

        // Rewrite every `guid: <realGuid>` occurrence in the prefab YAML with
        // the fake GUID — except the prefab's own m_Script references (Unity
        // built-ins), which we leave alone. We only touch lines that look like
        // external asset references on MeshFilter/Renderer.
        static void InjectBrokenGuid(string prefabPath, string realGuid, string fakeGuid)
        {
            var lines = File.ReadAllLines(prefabPath);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains($"guid: {realGuid}"))
                    lines[i] = lines[i].Replace($"guid: {realGuid}", $"guid: {fakeGuid}");
            }
            File.WriteAllLines(prefabPath, lines);
        }

        static void EnsureDirectory(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                var parent = Path.GetDirectoryName(path);
                var name = Path.GetFileName(path);
                if (!AssetDatabase.IsValidFolder(parent))
                    EnsureDirectory(parent);
                AssetDatabase.CreateFolder(parent, name);
            }
        }
    }
}
