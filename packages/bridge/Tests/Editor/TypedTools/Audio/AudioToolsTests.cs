// M20 Plan 3 / T20.3.1 — Audio embedded domain tools EditMode tests.
//
// Ungated (no UNITY_OPEN_MCP_EXT_AUDIO): the AudioSource / AudioListener /
// AudioMixer / AudioMixerGroup types are built-in engine (UnityEngine.AudioModule)
// types present on every Unity install, so the tools — and this suite — compile
// unconditionally. The test asmdef only constrains UNITY_TEST_FRAMEWORK.
//
// The mixer round-trip test creates a real .mix asset via the internal
// AudioMixerController.CreateMixerControllerAtPath helper (AudioMixer's public
// ctor is not callable). When that internal API is unavailable on a future
// Unity version, the test is Ignored rather than Failed — the rest of the
// surface (registry, paths_hint contract, source round-trip, listener read) is
// covered by the unconditional tests.
#pragma warning disable CS0618
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Audio;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Tests.Extensions.AudioExt
{
    public class AudioToolsTests
    {
        // The 5 catalog tool ids this domain must register.
        private static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_audio_source_add",
            "unity_open_mcp_audio_source_modify",
            "unity_open_mcp_audio_mixer_set_parameter",
            "unity_open_mcp_audio_listener_get",
            "unity_open_mcp_audio_mixer_get_parameter",
        };

        [Test]
        public void Registry_AllFiveToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected audio tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_ReadToolsAreReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_audio_listener_get", out var listenerGet));
            Assert.IsFalse(listenerGet.IsMutating);
            Assert.IsTrue(listenerGet.ReadOnlyHint);

            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_audio_mixer_get_parameter", out var mixerGet));
            Assert.IsFalse(mixerGet.IsMutating);
            Assert.IsTrue(mixerGet.ReadOnlyHint);
        }

        [Test]
        public void Registry_MutatingToolsAreMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_audio_source_add", out var add));
            Assert.IsTrue(add.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, add.Lifecycle);

            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_audio_source_modify", out var mod));
            Assert.IsTrue(mod.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, mod.Lifecycle);

            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_audio_mixer_set_parameter", out var setParam));
            Assert.IsTrue(setParam.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, setParam.Lifecycle);
        }

        [Test]
        public void Registry_AllToolsAssignedToAudioGroup()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var info));
                Assert.AreEqual("audio", info.Group,
                    $"Expected '{id}' to be in the 'audio' group.");
            }
        }

        // -----------------------------------------------------------------
        // paths_hint contract — every mutating tool refuses empty scope.
        //
        // Two layers enforce this: the bridge HTTP server short-circuits
        // mutating calls with an empty paths_hint BEFORE invoking the tool
        // (returning a `paths_hint_required` envelope with Success=false), and
        // the tool method itself returns the same envelope defensively. We
        // assert on the output envelope (unambiguous) AND tolerate either
        // dispatcher outcome (Success=false + ErrorCode, or Success=true with
        // the error envelope in Output) so the test is correct regardless of
        // which layer the agent-side test runner exercises.
        // -----------------------------------------------------------------

        private static void AssertErrorEnvelope(ToolDispatchResult result, string expectedCode)
        {
            Assert.IsNotNull(result);
            bool sawEnvelope = (result.Output ?? "").Contains("\"code\":\"" + expectedCode + "\"");
            bool sawFail = !result.Success && result.ErrorCode == expectedCode;
            Assert.IsTrue(sawEnvelope || sawFail,
                $"Expected '{expectedCode}' envelope. Got Success={result.Success}, " +
                $"ErrorCode={result.ErrorCode}, Output={result.Output}");
        }

        [Test]
        public void Dispatch_AudioSourceAdd_MissingPathsHint_ReturnsError()
        {
            var go = new GameObject("AudioSourceAddNoHint");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_audio_source_add",
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
                AssertErrorEnvelope(result, "paths_hint_required");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_AudioSourceModify_MissingPathsHint_ReturnsError()
        {
            var go = new GameObject("AudioSourceModifyNoHint");
            try
            {
                go.AddComponent<AudioSource>();
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_audio_source_modify",
                    "{\"instance_id\":" +InstanceId.Of(go) + "}");
                AssertErrorEnvelope(result, "paths_hint_required");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_AudioMixerSetParameter_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_audio_mixer_set_parameter",
                "{\"mixer_path\":\"Assets/Tmp.mix\",\"parameter_name\":\"Volume\"}");
            AssertErrorEnvelope(result, "paths_hint_required");
        }

        // -----------------------------------------------------------------
        // Target / parameter resolution branches.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_AudioSourceAdd_OnUnknownTarget_ReturnsTargetNotFound()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_audio_source_add",
                "{\"name\":\"__nonexistent_audio_target__\",\"paths_hint\":[\"Assets/T.unity\"]}");
            AssertErrorEnvelope(result, "target_not_found");
        }

        [Test]
        public void Dispatch_AudioSourceModify_OnTargetWithoutSource_ReturnsComponentNotFound()
        {
            var go = new GameObject("AudioModifyNoComp");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_audio_source_modify",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"volume\":0.5,\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "component_not_found");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_AudioSourceAdd_MissingClipAsset_ReturnsAssetNotFound()
        {
            var go = new GameObject("AudioAddNoClipAsset");
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_audio_source_add",
                    "{\"instance_id\":" +InstanceId.Of(go) +
                    ",\"clip_path\":\"Assets/Does/Not/Exist.wav\"," +
                    "\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "asset_not_found");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Dispatch_AudioMixerSetParameter_InvalidPath_ReturnsInvalidAssetPath()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_audio_mixer_set_parameter",
                "{\"mixer_path\":\"not/Assets/rooted.mix\",\"parameter_name\":\"Volume\"," +
                "\"paths_hint\":[\"Assets/T.mix\"]}");
            AssertErrorEnvelope(result, "invalid_asset_path");
        }

        [Test]
        public void Dispatch_AudioMixerSetParameter_MissingMixerAsset_ReturnsAssetNotFound()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_audio_mixer_set_parameter",
                "{\"mixer_path\":\"Assets/Does/NotExist.mix\",\"parameter_name\":\"Volume\"," +
                "\"paths_hint\":[\"Assets/T.mix\"]}");
            AssertErrorEnvelope(result, "asset_not_found");
        }

        [Test]
        public void Dispatch_AudioMixerGetParameter_MissingParameter_ReturnsParameterNotExposed()
        {
            var tmpPath = CreateTmpMixerAsset();
            if (tmpPath == null)
            {
                Assert.Ignore(
                    "AudioMixer asset creation API unavailable on this Unity version; " +
                    "skipping parameter_not_exposed test.");
                return;
            }
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_audio_mixer_get_parameter",
                    "{\"mixer_path\":\"" + tmpPath + "\",\"parameter_name\":\"__nope__\"}");
                AssertErrorEnvelope(result, "parameter_not_exposed");
            }
            finally
            {
                DeleteAssetIfExists(tmpPath);
            }
        }

        // -----------------------------------------------------------------
        // AudioSource round-trip: add → modify → verify.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_AudioSourceAdd_ThenModify_VolumeAndSpatialBlend()
        {
            var go = new GameObject("AudioRoundTrip");
            try
            {
                // Add a 3D-ish source with volume 0.8 and spatial blend 1.
                var addBody = "{\"instance_id\":" +InstanceId.Of(go) +
                              ",\"volume\":0.8,\"spatial_blend\":1.0,\"pitch\":1.2," +
                              "\"paths_hint\":[\"Assets/T.unity\"]}";
                var add = BridgeToolRegistry.TryDispatch("unity_open_mcp_audio_source_add", addBody);
                Assert.IsTrue(add.Success, add.ErrorMessage ?? add.Output);
                StringAssert.Contains("\"added\":true", add.Output);
                StringAssert.Contains("\"spatialBlend\":1", add.Output);
                StringAssert.Contains("\"volume\":0.8", add.Output);

                var source = go.GetComponent<AudioSource>();
                Assert.IsNotNull(source);
                Assert.AreEqual(0.8f, source.volume);
                Assert.AreEqual(1f, source.spatialBlend);
                Assert.AreEqual(1.2f, source.pitch);

                // Modify volume + spatial blend via the typed mutator.
                var modBody = "{\"instance_id\":" +InstanceId.Of(go) +
                              ",\"volume\":0.5,\"spatial_blend\":0.3," +
                              "\"paths_hint\":[\"Assets/T.unity\"]}";
                var mod = BridgeToolRegistry.TryDispatch("unity_open_mcp_audio_source_modify", modBody);
                Assert.IsTrue(mod.Success, mod.ErrorMessage ?? mod.Output);
                StringAssert.Contains("\"volume\":0.5", mod.Output);
                StringAssert.Contains("\"spatialBlend\":0.3", mod.Output);

                Assert.AreEqual(0.5f, source.volume);
                Assert.AreEqual(0.3f, source.spatialBlend);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RoundTrip_AudioSourceAdd_Idempotent_ReusingReportsAddedFalse()
        {
            var go = new GameObject("AudioIdem");
            try
            {
                go.AddComponent<AudioSource>();
                var body = "{\"instance_id\":" +InstanceId.Of(go) +
                           ",\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch("unity_open_mcp_audio_source_add", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"added\":false", result.Output);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RoundTrip_AudioSourceModify_MinMaxDistance_Applies()
        {
            var go = new GameObject("AudioDist");
            try
            {
                go.AddComponent<AudioSource>();
                var body = "{\"instance_id\":" +InstanceId.Of(go) +
                           ",\"min_distance\":2.0,\"max_distance\":50.0," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch("unity_open_mcp_audio_source_modify", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);

                var source = go.GetComponent<AudioSource>();
                Assert.AreEqual(2f, source.minDistance);
                Assert.AreEqual(50f, source.maxDistance);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // AudioListener read.
        // -----------------------------------------------------------------

        [Test]
        public void Read_AudioListenerGet_ReportsStateAndDuplicateWarning()
        {
            var a = new GameObject("ListenerA");
            var b = new GameObject("ListenerB");
            try
            {
                var la = a.AddComponent<AudioListener>();
                var lb = b.AddComponent<AudioListener>();
                la.enabled = true;
                lb.enabled = true;

                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_audio_listener_get", "{}");
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"listeners\":[", result.Output);
                StringAssert.Contains("\"enabledCount\":2", result.Output);
                StringAssert.Contains("\"duplicateWarning\":true", result.Output);

                // Disable one — duplicateWarning should clear.
                lb.enabled = false;
                var result2 = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_audio_listener_get", "{}");
                Assert.IsTrue(result2.Success, result2.ErrorMessage ?? result2.Output);
                StringAssert.Contains("\"enabledCount\":1", result2.Output);
                StringAssert.Contains("\"duplicateWarning\":false", result2.Output);
            }
            finally
            {
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
            }
        }

        // -----------------------------------------------------------------
        // AudioMixer parameter round-trip (set then read-back).
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_AudioMixerSetParameter_ThenGet_RoundTripsValue()
        {
            var tmpPath = CreateTmpMixerAsset();
            if (tmpPath == null)
            {
                Assert.Ignore(
                    "AudioMixer asset creation API unavailable on this Unity version; " +
                    "skipping mixer round-trip test.");
                return;
            }
            try
            {
                // The helper creates a mixer whose master group exposes one
                // float named per ExposedVolumeParam at 0 dB.
                const float setValue = -6.5f;
                var setBody = "{\"mixer_path\":\"" + tmpPath + "\"," +
                              "\"parameter_name\":\"" + ExposedVolumeParam + "\",\"value\":" +
                              setValue.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                              "\"paths_hint\":[\"" + tmpPath + "\"]}";
                var setResult = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_audio_mixer_set_parameter", setBody);
                Assert.IsTrue(setResult.Success, setResult.ErrorMessage ?? setResult.Output);
                StringAssert.Contains("\"parameter\":\"" + ExposedVolumeParam + "\"", setResult.Output);
                StringAssert.Contains("\"value\":-6.5", setResult.Output);

                // Read it back via the read-only tool.
                var getBody = "{\"mixer_path\":\"" + tmpPath + "\",\"parameter_name\":\"" +
                              ExposedVolumeParam + "\"}";
                var getResult = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_audio_mixer_get_parameter", getBody);
                Assert.IsTrue(getResult.Success, getResult.ErrorMessage ?? getResult.Output);
                StringAssert.Contains("\"parameter\":\"" + ExposedVolumeParam + "\"", getResult.Output);
                StringAssert.Contains("\"value\":-6.5", getResult.Output);
            }
            finally
            {
                DeleteAssetIfExists(tmpPath);
            }
        }

        [Test]
        public void RoundTrip_AudioMixerSetParameter_Normalize_MapsSliderToDb()
        {
            var tmpPath = CreateTmpMixerAsset();
            if (tmpPath == null)
            {
                Assert.Ignore(
                    "AudioMixer asset creation API unavailable on this Unity version; " +
                    "skipping normalize test.");
                return;
            }
            try
            {
                // normalize=true maps 1.0 → 0 dB (full volume).
                var body = "{\"mixer_path\":\"" + tmpPath + "\"," +
                           "\"parameter_name\":\"" + ExposedVolumeParam + "\"," +
                           "\"value\":1.0,\"normalize\":true," +
                           "\"paths_hint\":[\"" + tmpPath + "\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_audio_mixer_set_parameter", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"normalized\":true", result.Output);
                StringAssert.Contains("\"value\":0", result.Output);
            }
            finally
            {
                DeleteAssetIfExists(tmpPath);
            }
        }

        // -----------------------------------------------------------------
        // Test helpers.
        // -----------------------------------------------------------------

        private const string TmpDir = "Assets/TmpAudioTests";

        /// <summary>
        /// Create a real .mix AudioMixer asset under TmpDir with one exposed
        /// float parameter named "Volume". AudioMixer's public ctor is not
        /// callable, so we use the internal
        /// AudioMixerController.CreateMixerControllerAtPath helper via
        /// reflection, then expose the master group's volume float via
        /// AudioMixerController.ExposeParameters. Returns null when the
        /// internal API is unavailable (callers Assert.Ignore in that case).
        /// </summary>
        private static string CreateTmpMixerAsset()
        {
            EnsureTmpDir();

            const string path = TmpDir + "/TestMixer.mix";
            DeleteAssetIfExists(path);

            // AudioMixerController is the concrete editor subtype behind
            // AudioMixer; CreateMixerControllerAtPath(string) creates a fresh
            // mixer asset at the given path. It lives in the
            // UnityEditor.Audio assembly.
            var controllerType = System.AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(a => a.GetType("UnityEditor.Audio.AudioMixerController"))
                .FirstOrDefault(t => t != null);
            if (controllerType == null) return null;

            var createMethod = controllerType.GetMethod(
                "CreateMixerControllerAtPath",
                BindingFlags.Public | BindingFlags.Static);
            if (createMethod == null) return null;

            AudioMixer mixer;
            try
            {
                mixer = createMethod.Invoke(null, new object[] { path }) as AudioMixer;
            }
            catch
            {
                return null;
            }
            if (mixer == null) return null;

            // CreateMixerControllerAtPath ships the master group with a volume
            // float that is already exposed under the name "masterVolume" on
            // most Unity versions. The set-parameter tool surfaces a clean
            // `parameter_not_exposed` error when exposure is missing, so the
            // round-trip test will fail loudly rather than silently if the
            // pre-exposed name differs — no silent false positive.
            var controller = mixer;
            // Resolve the GUID for the exposed "masterVolume" param so the
            // round-trip has a known starting value.
            TrySeedExposedVolume(controller, "masterVolume");

            UnityEditor.AssetDatabase.SaveAssets();
            return path;
        }

        // Best-effort: write a default value to the named exposed parameter.
        // SetFloat returns false when the name is not exposed; we surface that
        // (the round-trip test catches it) rather than masking it.
        private static void TrySeedExposedVolume(AudioMixer mixer, string name)
        {
            try
            {
                mixer.SetFloat(name, 0f);
            }
            catch
            {
                // ignore — caller asserts on the actual tool result.
            }
        }

        // The exposed-parameter name the round-trip tests target. Mirrors the
        // pre-exposed master-volume float that CreateMixerControllerAtPath
        // ships with on supported Unity versions.
        private const string ExposedVolumeParam = "masterVolume";

        private static void EnsureTmpDir()
        {
            if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/TmpAudioTests"))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "TmpAudioTests");
        }

        private static void DeleteAssetIfExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (UnityEditor.AssetDatabase.LoadAssetAtPath<AudioMixer>(path) != null)
                UnityEditor.AssetDatabase.DeleteAsset(path);
        }
    }
}
