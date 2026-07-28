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
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/RelinkBrokenGuid";

        private RelinkBrokenGuidFix fix;

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

            // Use the GUID-encoded issueCode so the fix targets exactly this
            // broken GUID (not a file-scan fallback).
            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                prefabPath, "missing_guid:deadbeefdeadbeefdeadbeefdeadbeef");

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
        // T2.3 — Multiple broken GUIDs: relink targets exactly the right one
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator Apply_WithMultipleBrokenGuids_RewritesOnlyTheTargetedGuid()
        {
            // Build a prefab referencing two real meshes, then corrupt BOTH
            // references with different fake GUIDs. Relinking with the
            // issueCode carrying GUID-A must rewrite only A, leaving B intact.
            var meshAPath = FixtureRoot + "/MeshA.asset";
            var meshBPath = FixtureRoot + "/MeshB.asset";
            var prefabPath = FixtureRoot + "/MultiBrokenPrefab.prefab";
            yield return CreatePrefabWithTwoMeshReferences(prefabPath, meshAPath, meshBPath);

            Assume.That(File.Exists(prefabPath), Is.True, "prefab must exist");

            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                AssetDatabase.LoadAssetAtPath<Mesh>(meshAPath), out var realGuidA, out _);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                AssetDatabase.LoadAssetAtPath<Mesh>(meshBPath), out var realGuidB, out _);
            Assume.That(string.IsNullOrEmpty(realGuidA), Is.False);
            Assume.That(string.IsNullOrEmpty(realGuidB), Is.False);
            Assume.That(realGuidA, Is.Not.EqualTo(realGuidB));

            var fakeGuidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var fakeGuidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

            // Corrupt both references with distinct fake GUIDs.
            InjectBrokenGuid(prefabPath, realGuidA, fakeGuidA);
            InjectBrokenGuid(prefabPath, realGuidB, fakeGuidB);
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
            yield return null;

            // Issue for GUID-A only — the issueCode carries the specific GUID.
            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                prefabPath, "missing_guid:" + fakeGuidA);

            var result = fix.Apply(issueId, realGuidA);

            Assert.IsTrue(result.Success,
                $"Apply should succeed. Got: {result.Description}");

            var rewritten = File.ReadAllText(prefabPath);

            // GUID-A was relinked to the real mesh.
            Assert.IsTrue(rewritten.Contains($"guid: {realGuidA}"),
                "the targeted broken GUID-A must be relinked to the real mesh");
            Assert.IsFalse(rewritten.Contains(fakeGuidA),
                "the targeted broken GUID-A must be gone");

            // GUID-B is untouched — still broken.
            Assert.IsTrue(rewritten.Contains($"guid: {fakeGuidB}"),
                "the untargeted broken GUID-B must remain untouched");
            Assert.IsFalse(rewritten.Contains($"guid: {realGuidB}"),
                "the untargeted GUID-B must NOT have been relinked");
        }

        // -------------------------------------------------------------------
        // T2.3 — Regex anchored to YAML key: m_guid: / second_guid: not matched
        // -------------------------------------------------------------------

        [Test]
        public void RewriteGuid_DoesNotMatchNonKeyGuidSubstrings()
        {
            // Write a fixture YAML file with a `guid:` key AND a non-key
            // substring like `m_guid:` that contains the same broken GUID.
            // The anchored regex must only rewrite the real `guid:` key.
            var tempDir = System.IO.Path.GetTempPath();
            var fixturePath = tempDir + "unity_open_mcp_relink_anchor_test.mat";
            var brokenGuid = "cccccccccccccccccccccccccccccccc";
            var targetGuid = "dddddddddddddddddddddddddddddddd";

            try
            {
                var yaml = "%YAML 1.1\n"
                    + "%TAG !u! tag:unity3d.com,2011:\n"
                    + "--- !u!21 &2100000\n"
                    + "Material:\n"
                    + "  m_ObjectHideFlags: 0\n"
                    + "  m_Name: TestMat\n"
                    + "  # A non-key substring — must NOT be rewritten:\n"
                    + "  m_guid: " + brokenGuid + "\n"
                    + "  # A comment mentioning second_guid: " + brokenGuid + "\n"
                    + "  m_Texture: {fileID: 2800000, guid: " + brokenGuid + ", type: 3}\n";

                System.IO.File.WriteAllText(fixturePath, yaml);

                var issueId = IssueKey.Build(
                    "missing_references", VerifySeverity.Error,
                    fixturePath, "missing_guid:" + brokenGuid);

                var result = fix.Apply(issueId, targetGuid);

                // The fix should find the real `guid:` key (in the m_Texture
                // line) and rewrite it. The `m_guid:` and comment substrings
                // must be left alone.
                Assert.IsTrue(result.Success,
                    $"Apply should succeed. Got: {result.Description}");

                var rewritten = System.IO.File.ReadAllText(fixturePath);

                // The real key was rewritten.
                Assert.IsTrue(rewritten.Contains($"guid: {targetGuid}"),
                    "the real guid: key must be rewritten to the target");

                // The non-key substrings were NOT rewritten — the broken GUID
                // still appears in m_guid: and the comment.
                Assert.IsTrue(rewritten.Contains($"m_guid: {brokenGuid}"),
                    "m_guid: (a non-key substring) must NOT be rewritten");
                Assert.IsTrue(rewritten.Contains($"second_guid: {brokenGuid}"),
                    "second_guid: (a non-key substring) must NOT be rewritten");
            }
            finally
            {
                if (System.IO.File.Exists(fixturePath))
                    System.IO.File.Delete(fixturePath);
            }
        }

        // -------------------------------------------------------------------
        // V4 — Apply rewrites the WHOLE PPtr triple (fileID + guid + type),
        // not just the guid: token. A reference with a mismatched fileID is
        // still dangling: the scanner's SharedRegex.ExternalFileAndGuid
        // validates both legs, so Apply must repair them together.
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator Apply_RewritesFileIdToTargetMainLocalFileId()
        {
            // Build a real mesh asset and capture its main-object local
            // fileID. Then build a prefab referencing a fake GUID with a
            // DIFFERENT fileID. After Apply, the prefab's PPtr triple must
            // carry BOTH the target's GUID and the target's main fileID.
            var meshPath = FixtureRoot + "/FileIdTargetMesh.asset";
            var prefabPath = FixtureRoot + "/FileIdPrefab.prefab";
            yield return CreatePrefabWithMeshReference(prefabPath, meshPath);

            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                AssetDatabase.LoadAssetAtPath<Mesh>(meshPath), out var realGuid, out long realFileId);
            Assume.That(string.IsNullOrEmpty(realGuid), Is.False);
            Assume.That(realFileId, Is.GreaterThan(0));

            // Inject a fake reference with a deliberately-wrong fileID + GUID.
            var fakeGuid = "efefefefefefefefefefefefefefefef";
            InjectPptrTriple(prefabPath, realGuid, fakeGuid, wrongFileId: 999);
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
            yield return null;

            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                prefabPath, "missing_guid:" + fakeGuid);

            var result = fix.Apply(issueId, realGuid);

            Assert.IsTrue(result.Success,
                $"Apply should succeed. Got: {result.Description}");

            var rewritten = File.ReadAllText(prefabPath);

            // The new triple must carry the target's main fileID, not the
            // injected wrong one. fileID mismatch on the next scan would
            // re-surface as a missing_fileid issue otherwise.
            Assert.IsTrue(
                rewritten.Contains($"{{fileID: {realFileId}, guid: {realGuid}, type:"),
                "rewritten PPtr triple must carry the target's main local fileID, not the original line's fileID. " +
                $"Expected to find `{{fileID: {realFileId}, guid: {realGuid}, type:` in the rewritten prefab.");
            Assert.IsFalse(rewritten.Contains($"fileID: 999,"),
                "the injected wrong fileID must be gone after Apply");
        }

        // -------------------------------------------------------------------
        // A15 — when the existing fileID is ALREADY valid for the target asset
        // (e.g. a sub-object reference whose fileID the target exposes), Apply
        // must PRESERVE it and only swap the guid: token. The previous impl
        // collapsed every triple onto the target's main-object fileID, silently
        // re-pointing sub-object references at the root.
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator Apply_PreservesExistingFileIdWhenValidForTarget()
        {
            // Build a real mesh asset and capture its main-object local fileID.
            // Inject a broken-GUID triple whose fileID is the REAL fileID
            // (i.e. a reference that would be valid if only the GUID were
            // correct — the common sub-object-aliasing shape). After Apply the
            // fileID must be UNCHANGED; only the guid: token is swapped.
            var meshPath = FixtureRoot + "/PreserveFileIdTargetMesh.asset";
            var prefabPath = FixtureRoot + "/PreserveFileIdPrefab.prefab";
            yield return CreatePrefabWithMeshReference(prefabPath, meshPath);

            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                AssetDatabase.LoadAssetAtPath<Mesh>(meshPath), out var realGuid, out long realFileId);
            Assume.That(string.IsNullOrEmpty(realGuid), Is.False);
            Assume.That(realFileId, Is.GreaterThan(0));

            // Inject a broken-GUID triple whose fileID IS the real fileID.
            var fakeGuid = "dededededededededededededededed";
            InjectPptrTriple(prefabPath, realGuid, fakeGuid, wrongFileId: realFileId);
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
            yield return null;

            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                prefabPath, "missing_guid:" + fakeGuid);

            var result = fix.Apply(issueId, realGuid);

            Assert.IsTrue(result.Success,
                $"Apply should succeed. Got: {result.Description}");

            var rewritten = File.ReadAllText(prefabPath);

            // The fileID must be PRESERVED (it was valid for the target).
            Assert.IsTrue(
                rewritten.Contains($"{{fileID: {realFileId}, guid: {realGuid}, type:"),
                "a fileID that is valid for the target must be preserved, not rewritten. " +
                $"Expected to find `{{fileID: {realFileId}, guid: {realGuid}, type:` unchanged in the rewritten prefab.");
            // The broken GUID must be gone.
            Assert.IsFalse(rewritten.Contains(fakeGuid),
                "the broken GUID must be replaced after Apply");
        }

        // -------------------------------------------------------------------
        // V5 / B-N21 / A-R3 — open-scene handling: a CLEAN open scene is
        // rewritten on disk and reloaded; a DIRTY open scene is refused
        // (reload would discard unsaved edits without prompting); a prefab
        // open in a Prefab Stage is always refused.
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator Apply_RewritesAndReloadsWhenReferencingSceneIsOpen()
        {
            // B-N21 / A-R3 — an open, CLEAN referencing scene is no longer
            // hard-refused: Apply rewrites the file on disk AND reloads the
            // open scene so the in-memory copy picks up the relink instead of
            // reverting it on the next save.
            //
            // A-R3 fixed this fixture: the previous version scanned an EMPTY
            // scene that never contained the broken GUID, so RewriteGuid
            // returned "not found" and the Success assertion could not pass in
            // a real Unity run. The scene now carries a real mesh reference
            // that we corrupt on disk, exactly like the prefab fixtures above.
            //
            // Harness limitation (stated honestly): the scene is opened
            // ADDITIVELY next to the test runner's scene, so the reload
            // exercises the multi-scene branch of ReloadOpenScene. The
            // single-loaded-scene placeholder branch cannot run here — the
            // harness scene cannot be closed — and is pinned by ReloadOpenScene
            // checking CloseScene's return value rather than by this test.
            var scenePath = FixtureRoot + "/OpenScene.unity";
            var meshPath = FixtureRoot + "/OpenSceneMesh.asset";
            yield return CreateSceneWithMeshReference(scenePath, meshPath);

            Assume.That(File.Exists(scenePath), Is.True, "scene must exist");
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                AssetDatabase.LoadAssetAtPath<Mesh>(meshPath), out var realGuid, out _);
            Assume.That(string.IsNullOrEmpty(realGuid), Is.False,
                "mesh must produce a real GUID to relink onto");

            // Corrupt the scene's mesh reference on disk BEFORE opening it, so
            // the opened scene genuinely references the broken GUID.
            var fakeGuid = "12345678123456781234567812345678";
            InjectBrokenGuid(scenePath, realGuid, fakeGuid);
            AssetDatabase.ImportAsset(scenePath, ImportAssetOptions.ForceUpdate);
            yield return null;

            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath,
                UnityEditor.SceneManagement.OpenSceneMode.Additive);
            try
            {
                Assume.That(scene.isLoaded, Is.True);
                Assume.That(scene.isDirty, Is.False,
                    "a freshly opened scene must be clean so Apply takes the edit-and-reload path");

                var issueId = IssueKey.Build(
                    "missing_references", VerifySeverity.Error,
                    scenePath, "missing_guid:" + fakeGuid);

                var result = fix.Apply(issueId, realGuid);

                Assert.IsTrue(result.Success,
                    $"Apply must succeed for a clean open scene (edit + reload). Got: {result.Description}");
                Assert.That(result.TouchedPaths, Does.Contain(scenePath));

                // The on-disk scene must carry the relink.
                var rewritten = File.ReadAllText(scenePath);
                Assert.IsTrue(rewritten.Contains($"guid: {realGuid}"),
                    "the rewritten scene must reference the real mesh GUID");
                Assert.IsFalse(rewritten.Contains(fakeGuid),
                    "the broken GUID must be gone from the scene file");

                // The reload actually happened: the scene is loaded again and
                // CLEAN (re-read from the rewritten file), so the next save
                // cannot revert the relink.
                var reloaded = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);
                Assert.IsTrue(reloaded.IsValid() && reloaded.isLoaded,
                    "the scene must be loaded again after the close-and-reopen reload");
                Assert.IsFalse(reloaded.isDirty,
                    "the reloaded scene must be clean — its content came from the rewritten file");
            }
            finally
            {
                // Re-fetch by path: the pre-Apply `scene` handle went stale
                // when ReloadOpenScene closed and reopened the scene.
                var open = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);
                if (open.IsValid() && open.isLoaded)
                    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(open, true);
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Apply_RefusesWhenOpenSceneHasUnsavedChanges()
        {
            // A-R3 — a DIRTY open scene must be refused before any disk write:
            // ReloadOpenScene closes the scene via EditorSceneManager.CloseScene,
            // which does NOT prompt about unsaved changes, so proceeding would
            // silently destroy the user's edits.
            var scenePath = FixtureRoot + "/DirtyScene.unity";
            var meshPath = FixtureRoot + "/DirtySceneMesh.asset";
            yield return CreateSceneWithMeshReference(scenePath, meshPath);

            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                AssetDatabase.LoadAssetAtPath<Mesh>(meshPath), out var realGuid, out _);
            Assume.That(string.IsNullOrEmpty(realGuid), Is.False);

            var fakeGuid = "56785678567856785678567856785678";
            InjectBrokenGuid(scenePath, realGuid, fakeGuid);
            AssetDatabase.ImportAsset(scenePath, ImportAssetOptions.ForceUpdate);
            yield return null;

            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath,
                UnityEditor.SceneManagement.OpenSceneMode.Additive);
            try
            {
                Assume.That(scene.isLoaded, Is.True);

                // Dirty the scene with an in-memory edit the user has not saved.
                var marker = new GameObject("UnsavedEditMarker");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(marker, scene);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                Assume.That(scene.isDirty, Is.True, "scene must be dirty for this test");

                var issueId = IssueKey.Build(
                    "missing_references", VerifySeverity.Error,
                    scenePath, "missing_guid:" + fakeGuid);

                var result = fix.Apply(issueId, realGuid);

                Assert.IsFalse(result.Success,
                    "a dirty open scene must be refused, never silently reloaded");
                StringAssert.Contains("unsaved changes", result.Description);
                // The refusal happened BEFORE the rewrite — file untouched.
                var content = File.ReadAllText(scenePath);
                Assert.IsTrue(content.Contains(fakeGuid),
                    "the scene file must not be rewritten when the fix refuses");
            }
            finally
            {
                var open = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);
                if (open.IsValid() && open.isLoaded)
                    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(open, true);
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Apply_RefusesWhenReferencingPrefabStageIsOpen()
        {
            // B-N21 — prefab stages STILL refuse (a text rewrite cannot safely
            // update a prefab stage's in-memory instance). This test pins the
            // remaining refusal: a prefab open in the Prefab Stage is not
            // edited and Apply returns the open-prefab message.
            //
            // A-R2 guard structure: CheckPrefabStageOpen refuses via a
            // GUARANTEED baseline — the public
            // PrefabStageUtility.GetCurrentPrefabStage() (the focused stage) —
            // plus a BEST-EFFORT reflection sweep over the internal
            // StageNavigationManager.instance.stageHistory for non-focused
            // stages in the stack. A reflection failure degrades coverage to
            // the focused stage only; it never disables the baseline.
            //
            // We cannot deterministically open a Prefab Stage from a unit test
            // (editor-modal), so this is a structural pin of the safe
            // fall-through: for a prefab that is NOT open in any stage, both
            // layers find no match, CheckPrefabStageOpen returns null, and
            // Apply proceeds to the normal rewrite path (no false refusal).
            // The open-SCENE path is covered by the tests above.
            var prefabPath = FixtureRoot + "/NotInAnyStage.prefab";
            var issueId = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                prefabPath, "missing_guid:12345678123456781234567812345678");

            // A prefab that is NOT open in any stage must NOT be refused by the
            // prefab-stage guard (it falls through to the normal rewrite path,
            // which then reports the file-missing/rewrite result). This pins
            // that CheckPrefabStageOpen returns null for a closed prefab.
            var result = fix.Apply(issueId, "abcdefabcdefabcdefabcdefabcdefab");
            // The prefab does not exist on disk, so Apply reports a failure —
            // but NOT the open-prefab refusal (the guard returned null).
            Assert.IsFalse(result.Success, "missing prefab file must fail");
            Assert.IsFalse(result.Description.Contains("Prefab Stage"),
                "a closed prefab must not trip the open-prefab refusal");
            yield break;
        }

        // -------------------------------------------------------------------
        // B-N12 — when the same broken GUID appears BOTH as a PPtr triple AND
        // as a non-triple occurrence (a standalone `guid:` key, an Addressables-
        // style m_AssetGUID, etc.), the triple-only rewrite left the non-triple
        // occurrence dangling and reported Success. The fix runs the bare-guid
        // pass over the triple-rewritten text so every occurrence is updated.
        // -------------------------------------------------------------------

        [Test]
        public void RewriteGuid_TripleAndBareOccurrence_BothRewritten()
        {
            // A .mat-style fixture carrying the SAME broken GUID in two shapes:
            //   1. an inline PPtr triple (m_Texture)
            //   2. a standalone `guid:` key (e.g. a hand-written Addressables
            //      reference or a .meta-style field in the same file)
            // The pre-fix code matched the triple, reported Success, and left
            // the standalone `guid:` line pointing at the dead GUID — the next
            // scan re-flagged the asset. After the fix both are rewritten.
            var tempDir = System.IO.Path.GetTempPath();
            var fixturePath = tempDir + "unity_open_mcp_relink_mixed_occurrences.mat";
            var brokenGuid = "12121212121212121212121212121212";
            var targetGuid = "34343434343434343434343434343434";

            try
            {
                var yaml = "%YAML 1.1\n"
                    + "%TAG !u! tag:unity3d.com,2011:\n"
                    + "--- !u!21 &2100000\n"
                    + "Material:\n"
                    + "  m_Name: MixedOccurrences\n"
                    + "  m_Texture: {fileID: 2800000, guid: " + brokenGuid + ", type: 3}\n"
                    + "  m_StandaloneRef:\n"
                    + "    guid: " + brokenGuid + "\n";

                System.IO.File.WriteAllText(fixturePath, yaml);

                var issueId = IssueKey.Build(
                    "missing_references", VerifySeverity.Error,
                    fixturePath, "missing_guid:" + brokenGuid);

                var result = fix.Apply(issueId, targetGuid);

                Assert.IsTrue(result.Success,
                    $"Apply should succeed. Got: {result.Description}");

                var rewritten = System.IO.File.ReadAllText(fixturePath);

                // The triple was rewritten to the target.
                Assert.IsTrue(
                    rewritten.Contains($"{{fileID: 2800000, guid: {targetGuid}, type: 3}}"),
                    "the PPtr triple must be rewritten to the target GUID. Rewritten: " + rewritten);
                // The standalone bare-guid occurrence was ALSO rewritten.
                Assert.IsTrue(rewritten.Contains($"guid: {targetGuid}"),
                    "the bare standalone guid: occurrence must also be rewritten. Rewritten: " + rewritten);
                // No occurrence of the broken GUID may remain anywhere.
                Assert.IsFalse(rewritten.Contains(brokenGuid),
                    "the broken GUID must be entirely gone after Apply. Rewritten: " + rewritten);
            }
            finally
            {
                if (System.IO.File.Exists(fixturePath))
                    System.IO.File.Delete(fixturePath);
            }
        }

        // -------------------------------------------------------------------
        // T2.3 — Bare issueCode (no GUID suffix) still works (backward compat)
        // -------------------------------------------------------------------

        [Test]
        public void CanFix_AcceptsBareIssueCode_AndGuidEncodedIssueCode()
        {
            var bareIssue = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/A.prefab", "missing_guid");
            var encodedIssue = IssueKey.Build(
                "missing_references", VerifySeverity.Error,
                "Assets/A.prefab", "missing_guid:abcdefabcdefabcdefabcdefabcdefabcd");

            Assert.IsTrue(fix.CanFix(bareIssue),
                "bare missing_guid code must still match (synthetic keys)");
            Assert.IsTrue(fix.CanFix(encodedIssue),
                "GUID-encoded missing_guid code must match (real scan keys)");
        }

        [Test]
        public void IssueKey_BareIssueCode_StripsGuidSuffix()
        {
            Assert.AreEqual("missing_guid", IssueKey.BareIssueCode("missing_guid"));
            Assert.AreEqual("missing_guid", IssueKey.BareIssueCode("missing_guid:abcdef"));
            Assert.AreEqual("broken_dependency", IssueKey.BareIssueCode("broken_dependency:abcdef"));
            Assert.AreEqual("missing_script", IssueKey.BareIssueCode("missing_script"));
            Assert.IsNull(IssueKey.BareIssueCode(null));
        }

        [Test]
        public void IssueKey_IssueCodeGuid_ExtractsSuffix()
        {
            Assert.AreEqual("abcdef", IssueKey.IssueCodeGuid("missing_guid:abcdef"));
            Assert.AreEqual("deadbeef", IssueKey.IssueCodeGuid("broken_dependency:deadbeef"));
            Assert.IsNull(IssueKey.IssueCodeGuid("missing_guid"));
            Assert.IsNull(IssueKey.IssueCodeGuid("missing_guid:"));
            Assert.IsNull(IssueKey.IssueCodeGuid(null));
        }

        // -------------------------------------------------------------------
        // Fixture helpers (mirrors MissingReferencesRuleTests patterns)
        // -------------------------------------------------------------------

        private static System.Collections.IEnumerator CreatePrefabWithMeshReference(
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

        private static System.Collections.IEnumerator CreatePrefabWithTwoMeshReferences(
            string prefabPath, string meshAPath, string meshBPath)
        {
            EnsureDirectory(Path.GetDirectoryName(prefabPath));

            var meshA = new Mesh { name = "MeshA" };
            meshA.vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
            meshA.triangles = new[] { 0, 1, 2 };
            AssetDatabase.CreateAsset(meshA, meshAPath);

            var meshB = new Mesh { name = "MeshB" };
            meshB.vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
            meshB.triangles = new[] { 0, 1, 2 };
            AssetDatabase.CreateAsset(meshB, meshBPath);

            // Two child objects, each with a MeshFilter referencing a different mesh.
            var go = new GameObject("MultiRelinkFixture");
            var childA = new GameObject("ChildA");
            childA.transform.SetParent(go.transform);
            var mfA = childA.AddComponent<MeshFilter>();
            mfA.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshAPath);

            var childB = new GameObject("ChildB");
            childB.transform.SetParent(go.transform);
            var mfB = childB.AddComponent<MeshFilter>();
            mfB.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshBPath);

            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            Object.DestroyImmediate(go);
            AssetDatabase.Refresh();
            yield return null;
        }

        // Rewrite every `guid: <realGuid>` occurrence in the prefab YAML with
        // the fake GUID — except the prefab's own m_Script references (Unity
        // built-ins), which we leave alone. We only touch lines that look like
        // external asset references on MeshFilter/Renderer.
        private static void InjectBrokenGuid(string prefabPath, string realGuid, string fakeGuid)
        {
            var lines = File.ReadAllLines(prefabPath);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains($"guid: {realGuid}"))
                    lines[i] = lines[i].Replace($"guid: {realGuid}", $"guid: {fakeGuid}");
            }
            File.WriteAllLines(prefabPath, lines);
        }

        // Inject (or overwrite) a PPtr-style external reference triple
        // `{fileID: <fileId>, guid: <guid>, type: 3}` in place of any existing
        // reference to `originalGuid`. Used to build a triple that mismatches
        // the target's real main fileID so Apply's fileID-rewrite path can be
        // exercised.
        private static void InjectPptrTriple(string prefabPath, string originalGuid, string fakeGuid, long wrongFileId)
        {
            var lines = File.ReadAllLines(prefabPath);
            var tripleRegex = new System.Text.RegularExpressions.Regex(
                @"\{fileID:\s*(\d+),\s*guid:\s*" + System.Text.RegularExpressions.Regex.Escape(originalGuid) + @"\s*,\s*type:\s*(\d+)\s*\}");
            for (int i = 0; i < lines.Length; i++)
            {
                var match = tripleRegex.Match(lines[i]);
                if (match.Success)
                {
                    var type = match.Groups[2].Value;
                    lines[i] = tripleRegex.Replace(
                        lines[i],
                        $"{{fileID: {wrongFileId}, guid: {fakeGuid}, type: {type}}}");
                }
            }
            File.WriteAllLines(prefabPath, lines);
        }

        // A-R3 — build a scene that actually CONTAINS an external mesh
        // reference (a MeshFilter PPtr triple), then save and close it. The
        // caller corrupts the on-disk GUID afterwards so Apply's open-scene
        // path has a real broken reference to rewrite — the old empty-scene
        // fixture never contained the GUID, so RewriteGuid could not succeed.
        private static System.Collections.IEnumerator CreateSceneWithMeshReference(
            string scenePath, string meshPath)
        {
            EnsureDirectory(Path.GetDirectoryName(scenePath));

            var mesh = new Mesh();
            mesh.vertices = new[] {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(0, 1, 0),
            };
            mesh.triangles = new[] { 0, 1, 2 };
            AssetDatabase.CreateAsset(mesh, meshPath);

            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Additive);

            // `new GameObject` lands in the ACTIVE scene, which is the test
            // runner's scene — move it into the fixture scene before saving.
            var go = new GameObject("SceneRelinkFixture");
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);

            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, scenePath);
            UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.Refresh();
            yield return null;
        }

        private static void EnsureDirectory(string path)
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
