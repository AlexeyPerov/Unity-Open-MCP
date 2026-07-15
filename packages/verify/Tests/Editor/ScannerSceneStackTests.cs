using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityOpenMcpVerify.Rules.ScenePrefabHealth;

namespace UnityOpenMcpVerify.Tests
{
    // M30-polish Plan 5 / T5.5 — the ScenePrefabHealth scanner must NOT close
    // an additive scene it did not open. The pre-fix finally block ran
    // CloseScene whenever sceneCount > 1, which evicted an additive scene the
    // agent/user had open (the gate's scene_create / scene_open mutation).
    // These tests create a real .unity fixture, open it additively, run the
    // scanner, and assert the scene is STILL open afterwards.
    [TestFixture]
    public class ScannerSceneStackTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/ScannerSceneStack";

        private List<string> _openedBefore;

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

        [SetUp]
        public void SetUp()
        {
            _openedBefore = CaptureOpenedPaths();
        }

        [TearDown]
        public void TearDown()
        {
            // Close anything we opened during the test so the stack is clean.
            RestoreOpenedScenes(_openedBefore);
        }

        [Test]
        public void Scanner_DoesNotClose_AlreadyOpenAdditiveScene()
        {
            var scenePath = FixtureRoot + "/AdditiveScan.unity";
            WriteEmptySceneFile(scenePath);
            AssetDatabase.ImportAsset(scenePath, ImportAssetOptions.ForceUpdate);

            // Open the scene additively BEFORE scanning — this simulates the
            // gate's scene_create / scene_open mutation sitting in the stack.
            Scene opened;
            try
            {
                opened = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            }
            catch (System.Exception e)
            {
                Assert.Inconclusive($"Could not open fixture scene additively: {e.Message}");
                return;
            }
            Assume.That(opened.IsValid(), Is.True);
            Assume.That(opened.isLoaded, Is.True);

            try
            {
                var settings = ScanSettings.Default();
                var scenes = new List<SceneData>();
                var prefabs = new List<PrefabData>();

                // Scan the scene path — the scanner should detect it is already
                // open and NOT close it afterwards.
                Scanner.ScanPaths(new[] { scenePath }, settings, scenes, prefabs);

                // The scene must STILL be loaded after the scan.
                var stillOpen = IsSceneLoaded(scenePath);
                Assert.IsTrue(stillOpen,
                    "scanner must NOT close an additive scene that was already open " +
                    "(the gate's scene_create / scene_open mutation)");
            }
            finally
            {
                // Clean up: close the scene we opened.
                if (IsSceneLoaded(scenePath))
                    EditorSceneManager.CloseScene(opened, true);
            }
        }

        [Test]
        public void Scanner_ClosesScene_ItOpenedItself()
        {
            // The complement: when the scanner opens a scene that was NOT in the
            // stack, it must still close it (the original cleanup behavior). This
            // prevents the scanner from leaking scenes it opened for analysis.
            var scenePath = FixtureRoot + "/ScannerOpened.unity";
            WriteEmptySceneFile(scenePath);
            AssetDatabase.ImportAsset(scenePath, ImportAssetOptions.ForceUpdate);

            Assume.That(IsSceneLoaded(scenePath), Is.False,
                "fixture scene must not be open before the scan");

            var settings = ScanSettings.Default();
            var scenes = new List<SceneData>();
            var prefabs = new List<PrefabData>();

            Scanner.ScanPaths(new[] { scenePath }, settings, scenes, prefabs);

            Assert.IsFalse(IsSceneLoaded(scenePath),
                "scanner must close a scene it opened itself (no leak)");
        }

        // ----------------------- helpers ----------------------------------

        private static bool IsSceneLoaded(string path)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && s.path == path) return true;
            }
            return false;
        }

        private static List<string> CaptureOpenedPaths()
        {
            var list = new List<string>(SceneManager.sceneCount);
            for (int i = 0; i < SceneManager.sceneCount; i++)
                list.Add(SceneManager.GetSceneAt(i).path);
            return list;
        }

        private static void RestoreOpenedScenes(List<string> openedBefore)
        {
            // Close every loaded scene that wasn't open at SetUp, keeping at
            // least one scene open (Unity refuses an empty stack).
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded && !openedBefore.Contains(s.path) && SceneManager.sceneCount > 1)
                    EditorSceneManager.CloseScene(s, true);
            }
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

        // Minimal valid empty Unity scene YAML — enough for the scanner to load.
        private static void WriteEmptySceneFile(string path)
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
            File.WriteAllText(path, yaml);
        }
    }
}
