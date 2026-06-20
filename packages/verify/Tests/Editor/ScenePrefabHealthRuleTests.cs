using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Rules;

namespace UnityOpenMcpVerify.Tests
{
    [TestFixture]
    public class ScenePrefabHealthRuleTests
    {
        const string FixtureRoot = "Assets/Tests/VerifyFixtures/ScenePrefabHealth";

        ScenePrefabHealthRule rule;

        [SetUp]
        public void SetUp()
        {
            rule = new ScenePrefabHealthRule();
        }

        // Shared fixture folder lifetime: created once, reaped once. Previously
        // each [UnityTest] created + deleted the folder with a Refresh() on
        // both sides. The folder now outlives individual tests.
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
            Assert.AreEqual("scene_prefab_health", rule.Id);
        }

        [Test]
        public void Scan_EmptyPaths_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new string[0]);

            rule.Scan(scope, VerifyRunMode.Checkpoint, sink);

            Assert.AreEqual(0, sink.Count);
        }

        [Test]
        public void Scan_NullPaths_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(null);

            rule.Scan(scope, VerifyRunMode.Checkpoint, sink);

            Assert.AreEqual(0, sink.Count);
        }

        [Test]
        public void Scan_NonexistentPath_ProducesNoIssues()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Assets/Nonexistent99999.prefab" });

            rule.Scan(scope, VerifyRunMode.Checkpoint, sink);

            Assert.AreEqual(0, sink.Count);
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_Prefab_CheckpointMode_ProducesKeys()
        {
            var prefabPath = FixtureRoot + "/TestPrefab.prefab";
            yield return CreateMinimalPrefab(prefabPath);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });

            rule.Scan(scope, VerifyRunMode.Checkpoint, sink);

            foreach (var issue in sink)
            {
                Assert.AreEqual("scene_prefab_health", issue.RuleId);
                var key = IssueKey.Build(issue);
                Assert.IsTrue(IssueKey.TryParse(key, out _, out _, out _, out _),
                    $"Issue key '{key}' must be valid");
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_Prefab_IssuesUseKnownCodes()
        {
            var prefabPath = FixtureRoot + "/TestPrefab.prefab";
            yield return CreateMinimalPrefab(prefabPath);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            var knownCodes = new HashSet<string>
            {
                "broken_reference", "high_risk_bootstrap", "scene_object_count",
                "component_hotspot", "inactive_expensive", "inactive_heavy",
                "deep_nesting", "override_explosion"
            };

            foreach (var issue in sink)
            {
                CollectionAssert.Contains(knownCodes, issue.IssueCode,
                    $"Issue code '{issue.IssueCode}' must be a known ScenePrefabHealth code");
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_Scene_IssuesMatchAssetPath()
        {
            var scenePath = FixtureRoot + "/TestScene.unity";
            yield return CreateMinimalScene(scenePath);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { scenePath });

            rule.Scan(scope, VerifyRunMode.Validate, sink);

            foreach (var issue in sink)
            {
                Assert.AreEqual(scenePath, issue.AssetPath);
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_SeveritiesAreErrorOrWarning()
        {
            var prefabPath = FixtureRoot + "/TestPrefab.prefab";
            yield return CreateMinimalPrefab(prefabPath);

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            foreach (var issue in sink)
            {
                Assert.IsTrue(
                    issue.Severity == VerifySeverity.Error || issue.Severity == VerifySeverity.Warning,
                    $"Severity must be Error or Warning, got {issue.Severity}");
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_PackagesPath_Skipped()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Packages/com.unity.render-pipelines.core/Test.prefab" });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            Assert.AreEqual(0, sink.Count);
            yield return null;
        }

        [UnityTest]
        public System.Collections.IEnumerator Scan_LibraryPath_Skipped()
        {
            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { "Library/cache/something.prefab" });

            rule.Scan(scope, VerifyRunMode.Full, sink);

            Assert.AreEqual(0, sink.Count);
            yield return null;
        }

        static System.Collections.IEnumerator CreateMinimalPrefab(string path)
        {
            EnsureDirectory(System.IO.Path.GetDirectoryName(path));
            var go = new GameObject("VerifyTestPrefab");
#if UNITY_EDITOR
            PrefabUtility.SaveAsPrefabAsset(go, path);
#endif
            Object.DestroyImmediate(go);
            AssetDatabase.Refresh();
            yield return null;
        }

        static System.Collections.IEnumerator CreateMinimalScene(string path)
        {
            EnsureDirectory(System.IO.Path.GetDirectoryName(path));
            // EditorSceneManager.NewScene(Additive) throws when the test
            // runner has an unsaved untitled scene loaded (common in batch
            // EditMode runs). Write a minimal valid empty-scene file directly
            // instead of going through the scene manager — the rule only needs
            // a real .unity asset on disk to scan.
            WriteEmptySceneFile(path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            yield return null;
        }

        // Minimal valid empty Unity scene YAML. Enough for the verify rules to
        // load and scan the scene asset without touching EditorSceneManager.
        static void WriteEmptySceneFile(string path)
        {
            const string yaml =
                "%YAML 1.1\n" +
                "%TAG !u! tag:unity3d.com,2011:\n" +
                "--- !u!29 &1\n" +
                "OcclusionCullingSettings:\n" +
                "  m_ObjectHideFlags: 0\n" +
                "  serializedVersion: 2\n" +
                "  m_OcclusionBakeSettings:\n" +
                "    smallestOccluder: 5\n" +
                "    smallestHole: 0.25\n" +
                "    backfaceThreshold: 100\n" +
                "  m_SceneGUID: 00000000000000000000000000000000\n" +
                "  m_OcclusionCullingData: {fileID: 0}\n" +
                "--- !u!104 &2\n" +
                "RenderSettings:\n" +
                "  m_ObjectHideFlags: 0\n" +
                "  serializedVersion: 9\n" +
                "  m_Fog: 0\n" +
                "  m_FogColor: {r: 0.5, g: 0.5, b: 0.5, a: 1}\n" +
                "  m_FogMode: 3\n" +
                "  m_FogDensity: 0.01\n" +
                "  m_LinearFogStart: 0\n" +
                "  m_LinearFogEnd: 300\n" +
                "  m_AmbientLight: {r: 0.2, g: 0.2, b: 0.2, a: 1}\n" +
                "  m_AmbientProbe: {fileID: 0}\n" +
                "  m_SkyboxMaterial: {fileID: 0}\n" +
                "  m_HaloStrength: 0.5\n" +
                "  m_FlareStrength: 1\n" +
                "  m_FlareFadeSpeed: 3\n" +
                "  m_HaloTexture: {fileID: 0}\n" +
                "  m_SpotCookie: {fileID: 0}\n" +
                "  m_DefaultReflectionMode: 0\n" +
                "  m_DefaultReflectionResolution: 128\n" +
                "  m_ReflectionBounces: 1\n" +
                "  m_ReflectionIntensity: 1\n" +
                "  m_CustomReflection: {fileID: 0}\n" +
                "  m_Sun: {fileID: 0}\n" +
                "  m_IndirectSpecularColor: {r: 0, g: 0, b: 0, a: 1}\n" +
                "  m_UseRadianceAmbientProbe: 0\n" +
                "--- !u!157 &3\n" +
                "LightmapSettings:\n" +
                "  m_ObjectHideFlags: 0\n" +
                "  serializedVersion: 12\n" +
                "  m_GIWorkflowMode: 1\n" +
                "  m_GISettings:\n" +
                "    serializedVersion: 2\n" +
                "    m_BounceScale: 1\n" +
                "    m_IndirectOutputScale: 1\n" +
                "    m_AlbedoBoost: 1\n" +
                "    m_EnvironmentLightingMode: 0\n" +
                "    m_EnableBakedLightmaps: 1\n" +
                "    m_EnableRealtimeLightmaps: 0\n" +
                "  m_LightmapEditorSettings:\n" +
                "    serializedVersion: 12\n" +
                "    m_Resolution: 2\n" +
                "    m_BakeResolution: 40\n" +
                "    m_AtlasSize: 1024\n" +
                "    m_AO: 0\n" +
                "    m_AOMaxDistance: 1\n" +
                "    m_CompAOExponent: 1\n" +
                "    m_CompAOExponentDirect: 0\n" +
                "    m_ExtractAmbientOcclusion: 0\n" +
                "    m_Padding: 2\n" +
                "    m_LightmapParameters: {fileID: 0}\n" +
                "    m_LightmapsBakeMode: 1\n" +
                "    m_TextureCompression: 1\n" +
                "    m_FinalGather: 0\n" +
                "    m_FinalGatherFiltering: 1\n" +
                "    m_FinalGatherRayCount: 256\n" +
                "    m_ReflectionCompression: 2\n" +
                "    m_MixedBakeMode: 2\n" +
                "    m_BakeBackend: 1\n" +
                "    m_PVRSampling: 1\n" +
                "    m_PVRDirectSampleCount: 32\n" +
                "    m_PVRSampleCount: 512\n" +
                "    m_PVRBounces: 2\n" +
                "    m_PVREnvironmentSampleCount: 256\n" +
                "    m_PVREnvironmentReferencePointCount: 2048\n" +
                "    m_PVRFilteringMode: 1\n" +
                "    m_PVRDenoiserTypeDirect: 1\n" +
                "    m_PVRDenoiserTypeIndirect: 1\n" +
                "    m_PVRDenoiserTypeAO: 1\n" +
                "    m_PVRFilterTypeDirect: 0\n" +
                "    m_PVRFilterTypeIndirect: 0\n" +
                "    m_PVRFilterTypeAO: 0\n" +
                "    m_PVRFilteringGaussRadiusDirect: 1\n" +
                "    m_PVRFilteringGaussRadiusIndirect: 5\n" +
                "    m_PVRFilteringGaussRadiusAO: 2\n" +
                "    m_PVRFilteringAtrousPositionSigmaDirect: 0.5\n" +
                "    m_PVRFilteringAtrousPositionSigmaIndirect: 2\n" +
                "    m_PVRFilteringAtrousPositionSigmaAO: 1\n" +
                "    m_ExportTrainingData: 0\n" +
                "    m_TrainingDataDestination: TrainingData\n" +
                "    m_LightProbeSampleCountMultiplier: 4\n" +
                "  m_LightingDataAsset: {fileID: 0}\n" +
                "  m_LightingSettings: {fileID: 0}\n" +
                "--- !u!196 &4\n" +
                "NavMeshSettings:\n" +
                "  serializedVersion: 2\n" +
                "  m_ObjectHideFlags: 0\n" +
                "  m_BuildSettings:\n" +
                "    serializedVersion: 3\n" +
                "    agentTypeID: 0\n" +
                "    agentRadius: 0.5\n" +
                "    agentHeight: 2\n" +
                "    agentSlope: 45\n" +
                "    agentClimb: 0.4\n" +
                "    ledgeDropHeight: 0\n" +
                "    maxJumpAcrossDistance: 0\n" +
                "    minRegionArea: 2\n" +
                "    manualCellSize: 0\n" +
                "    cellSize: 0.16666667\n" +
                "    manualTileSize: 0\n" +
                "    tileSize: 256\n" +
                "    buildHeightMesh: 0\n" +
                "    maxJobWorkers: 0\n" +
                "    preserveTilesOutsideBounds: 0\n" +
                "    debug:\n" +
                "      m_Flags: 0\n" +
                "  m_NavMeshData: {fileID: 0}\n";
            System.IO.File.WriteAllText(path, yaml);
        }

        static void EnsureDirectory(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                var parent = System.IO.Path.GetDirectoryName(path);
                var name = System.IO.Path.GetFileName(path);
                if (!AssetDatabase.IsValidFolder(parent))
                    EnsureDirectory(parent);
                AssetDatabase.CreateFolder(parent, name);
            }
        }
    }
}
