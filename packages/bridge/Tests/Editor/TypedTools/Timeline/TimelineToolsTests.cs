// M20 Plan 6 / T20.6.2 — Timeline embedded domain tools EditMode tests.
//
// Gated by UNITY_OPEN_MCP_EXT_TIMELINE via the owning test asmdef's
// defineConstraints, so the suite only compiles + runs when the
// com.unity.timeline package is present — matching the compile-gate on the
// tool code under test.
//
// NOTE on the error-envelope contract: the registry's TryDispatch wraps every
// successful invocation in ToolDispatchResult.Ok(output). A tool that refuses
// (e.g. missing paths_hint) returns an Ok dispatch whose Output carries the
// JSON error envelope `{"error":{"code":...,"message":...}}`. These tests
// therefore assert against Output content for the refusal paths, and against
// Success + Output for the happy paths.
#if UNITY_OPEN_MCP_EXT_TIMELINE
#pragma warning disable CS0618
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.Extensions.Timeline;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Tests.Extensions.Timeline
{
    public class TimelineToolsTests
    {
        // The 5 catalog tool ids this pack must register.
        private static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_timeline_create",
            "unity_open_mcp_timeline_track_add",
            "unity_open_mcp_timeline_clip_add",
            "unity_open_mcp_timeline_director_bind",
            "unity_open_mcp_timeline_modify",
        };

        // Each test that creates an asset / GameObject cleans it up here.
        private string tempAssetPath;
        private GameObject tempDirectorHost;

        [SetUp]
        public void SetUp()
        {
            tempAssetPath = $"Assets/TimelineTest_{System.Guid.NewGuid():N}.playable";
            tempDirectorHost = null;
        }

        [TearDown]
        public void TearDown()
        {
            if (tempDirectorHost != null) Object.DestroyImmediate(tempDirectorHost);
            if (!string.IsNullOrEmpty(tempAssetPath) &&
                AssetDatabase.LoadAssetAtPath<TimelineAsset>(tempAssetPath) != null)
            {
                AssetDatabase.DeleteAsset(tempAssetPath);
            }
        }

        [Test]
        public void Registry_AllFiveToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected timeline tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_CreateIsMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_timeline_create", out var create));
            Assert.IsTrue(create.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, create.Lifecycle);
            Assert.AreEqual("timeline", create.Group);
        }

        [Test]
        public void Registry_AllTimelineToolsBelongToTimelineGroup()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var entry),
                    $"Tool '{id}' not registered.");
                Assert.AreEqual("timeline", entry.Group,
                    $"Tool '{id}' should belong to the 'timeline' group.");
            }
        }

        // -----------------------------------------------------------------
        // paths_hint contract — every mutating tool refuses empty scope.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_Create_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_timeline_create",
                "{\"asset_path\":\"Assets/NoHint.playable\"}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("paths_hint_required", result.Output);
        }

        [Test]
        public void Dispatch_TrackAdd_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_timeline_track_add",
                "{\"track_type\":\"Animation\"}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("paths_hint_required", result.Output);
        }

        // -----------------------------------------------------------------
        // Validation branches.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_Create_MissingAssetPath_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_timeline_create",
                "{\"paths_hint\":[\"Assets/NoScene.unity\"]}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("missing_parameter", result.Output);
        }

        [Test]
        public void Dispatch_Create_BadExtension_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_timeline_create",
                "{\"asset_path\":\"Assets/NotPlayable.asset\"," +
                "\"paths_hint\":[\"Assets/NoScene.unity\"]}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("invalid_parameter", result.Output);
        }

        [Test]
        public void Dispatch_TrackAdd_InvalidTrackType_ReturnsError()
        {
            // First create a real asset so the asset resolves.
            CreateAsset();
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_timeline_track_add",
                "{\"asset_path\":\"" + tempAssetPath + "\"," +
                "\"track_type\":\"NotATrack\"," +
                "\"paths_hint\":[\"" + tempAssetPath + "\"]}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            StringAssert.Contains("invalid_track_type", result.Output);
        }

        // -----------------------------------------------------------------
        // Happy path round-trip: create → add track → add clip.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_Create_TrackAdd_ClipAdd_AddsTrackAndClip()
        {
            CreateAsset();
            AddTrack("Animation", "PlayerAnim");
            AddClip(trackName: "PlayerAnim", clipName: "Intro");
            AddTrack("Activation", "TitleCard");

            var asset = AssetDatabase.LoadAssetAtPath<TimelineAsset>(tempAssetPath);
            Assert.IsNotNull(asset);
            // 2 root tracks.
            Assert.GreaterOrEqual(asset.GetRootTracks().Count, 2);
        }

        [Test]
        public void RoundTrip_DirectorBind_AddsPlayableDirectorAndBinds()
        {
            CreateAsset();
            tempDirectorHost = new GameObject("DirectorHost");

            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_timeline_director_bind",
                "{\"instance_id\":" +InstanceId.Of(tempDirectorHost) + "," +
                "\"asset_path\":\"" + tempAssetPath + "\"," +
                "\"paths_hint\":[\"Assets/TimelineTest.unity\",\"" + tempAssetPath + "\"]}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);

            var director = tempDirectorHost.GetComponent<PlayableDirector>();
            Assert.IsNotNull(director, "PlayableDirector should be added.");
            Assert.IsNotNull(director.playableAsset, "Asset should be bound.");
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private void CreateAsset()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_timeline_create",
                "{\"asset_path\":\"" + tempAssetPath + "\"," +
                "\"frame_rate\":\"30\"," +
                "\"paths_hint\":[\"" + tempAssetPath + "\"]}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"trackCount\":0", result.Output);
        }

        private void AddTrack(string trackType, string trackName)
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_timeline_track_add",
                "{\"asset_path\":\"" + tempAssetPath + "\"," +
                "\"track_type\":\"" + trackType + "\"," +
                "\"track_name\":\"" + trackName + "\"," +
                "\"paths_hint\":[\"" + tempAssetPath + "\"]}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
            StringAssert.Contains("\"added\":true", result.Output);
        }

        private void AddClip(string trackName, string clipName)
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_timeline_clip_add",
                "{\"asset_path\":\"" + tempAssetPath + "\"," +
                "\"track_name\":\"" + trackName + "\"," +
                "\"clip_name\":\"" + clipName + "\"," +
                "\"paths_hint\":[\"" + tempAssetPath + "\"]}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
            StringAssert.Contains("\"added\":true", result.Output);
        }
    }
}
#endif
