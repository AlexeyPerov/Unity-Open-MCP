using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;

namespace UnityOpenMcpExtensions.Animation.Tests
{
    public class AnimationToolsTests
    {
        private const string TmpRoot = "Assets/TmpAnimTests";
        private static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_animation_create",
            "unity_open_mcp_animation_get_data",
            "unity_open_mcp_animation_modify",
            "unity_open_mcp_animator_create",
            "unity_open_mcp_animator_get_data",
            "unity_open_mcp_animator_modify",
        };

        [SetUp]
        public void EnsureTmpRoot()
        {
            if (!AssetDatabase.IsValidFolder(TmpRoot))
                AssetDatabase.CreateFolder("Assets", "TmpAnimTests");
        }

        [TearDown]
        public void CleanTmpRoot()
        {
            if (AssetDatabase.IsValidFolder(TmpRoot))
            {
                AssetDatabase.DeleteAsset(TmpRoot);
                AssetDatabase.Refresh();
            }
        }

        // -----------------------------------------------------------------
        // Registry discovery + lifecycle flags.
        // -----------------------------------------------------------------

        [Test]
        public void Registry_AllSixToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected animation tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_ReadToolsAreReadOnly()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_animation_get_data", out var clipGet));
            Assert.IsFalse(clipGet.IsMutating);
            Assert.IsTrue(clipGet.ReadOnlyHint);

            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_animator_get_data", out var ctrlGet));
            Assert.IsFalse(ctrlGet.IsMutating);
            Assert.IsTrue(ctrlGet.ReadOnlyHint);
        }

        [Test]
        public void Registry_ModifyToolsAreMutatingAndEditorSettle()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_animation_modify", out var clipMod));
            Assert.IsTrue(clipMod.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, clipMod.Lifecycle);

            Assert.IsTrue(BridgeToolRegistry.TryGet(
                "unity_open_mcp_animator_modify", out var ctrlMod));
            Assert.IsTrue(ctrlMod.IsMutating);
            Assert.AreEqual(LifecyclePolicy.EditorSettle, ctrlMod.Lifecycle);
        }

        // -----------------------------------------------------------------
        // paths_hint contract — every mutating tool refuses empty scope.
        //
        // Two layers enforce this: the bridge HTTP server short-circuits
        // mutating calls with an empty paths_hint BEFORE invoking the tool
        // (returning a `paths_hint_required` envelope with Success=false), and
        // the tool method itself returns the same envelope defensively. We
        // assert on the output envelope (unambiguous) AND tolerate either
        // dispatcher outcome so the test is correct regardless of which layer
        // the agent-side test runner exercises.
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
        public void Dispatch_AnimationCreate_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_create",
                "{\"asset_paths\":[\"Assets/TmpAnimTests/Foo.anim\"]}");
            AssertErrorEnvelope(result, "paths_hint_required");
        }

        [Test]
        public void Dispatch_AnimationModify_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_modify",
                "{\"asset_path\":\"Assets/TmpAnimTests/Foo.anim\"," +
                "\"modifications_json\":\"[{\\\"type\\\":\\\"ClearCurves\\\"}]\"}");
            AssertErrorEnvelope(result, "paths_hint_required");
        }

        [Test]
        public void Dispatch_AnimatorCreate_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animator_create",
                "{\"asset_paths\":[\"Assets/TmpAnimTests/Foo.controller\"]}");
            AssertErrorEnvelope(result, "paths_hint_required");
        }

        // -----------------------------------------------------------------
        // Parameter validation branches.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_AnimationModify_MissingModificationsJson_ReturnsError()
        {
            // Create the asset first so we get past the load step.
            CreateClip("ValidateMissingModJson.anim");
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_modify",
                "{\"asset_path\":\"" + TmpRoot + "/ValidateMissingModJson.anim\"," +
                "\"paths_hint\":[\"" + TmpRoot + "/ValidateMissingModJson.anim\"]}");
            AssertErrorEnvelope(result, "missing_parameter");
        }

        [Test]
        public void Dispatch_AnimationGet_InvalidPath_ReturnsInvalidAssetPath()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_get_data",
                "{\"asset_path\":\"Assets/NotAFolder/NoExtension\"}");
            AssertErrorEnvelope(result, "invalid_asset_path");
        }

        [Test]
        public void Dispatch_AnimationGet_NonExistentAsset_ReturnsAssetNotFound()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_get_data",
                "{\"asset_path\":\"" + TmpRoot + "/NeverCreated.anim\"}");
            AssertErrorEnvelope(result, "asset_not_found");
        }

        // -----------------------------------------------------------------
        // AnimationClip round-trip.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_Clip_Create_Get_ModifySetCurve_Get_ReflectsCurve()
        {
            var path = TmpRoot + "/ClipRoundTrip.anim";

            // Create.
            var create = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_create",
                "{\"asset_paths\":[\"" + path + "\"],\"paths_hint\":[\"" + path + "\"]}");
            Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);
            StringAssert.Contains("\"createdPaths\":[\"" + path + "\"]", create.Output);

            // Get (empty clip).
            var get1 = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_get_data",
                "{\"asset_path\":\"" + path + "\"}");
            Assert.IsTrue(get1.Success);
            StringAssert.Contains("\"empty\":true", get1.Output);

            // Modify: set a curve (m_LocalPosition.x on the root Transform).
            // Use a JSON-escaped payload that the bridge's body parser handles
            // cleanly — the modifications_json is itself a JSON string field.
            var modsJson =
                "[{\"type\":\"SetCurve\"," +
                "\"componentType\":\"UnityEngine.Transform\"," +
                "\"propertyName\":\"m_LocalPosition.x\"," +
                "\"keyframes\":[" +
                "{\"time\":0.0,\"value\":0.0}," +
                "{\"time\":1.0,\"value\":2.0}]}]";
            var escaped = JsonEscape(modsJson);
            var modify = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_modify",
                "{\"asset_path\":\"" + path + "\"," +
                "\"modifications_json\":\"" + escaped + "\"," +
                "\"paths_hint\":[\"" + path + "\"]}");
            Assert.IsTrue(modify.Success, modify.ErrorMessage ?? modify.Output);
            StringAssert.Contains("\"applied\":[\"SetCurve(", modify.Output);

            // Get reflects the new curve.
            var get2 = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_get_data",
                "{\"asset_path\":\"" + path + "\"}");
            Assert.IsTrue(get2.Success);
            StringAssert.Contains("\"empty\":false", get2.Output);
            StringAssert.Contains("\"m_LocalPosition.x\"", get2.Output);
            StringAssert.Contains("\"keyframeCount\":2", get2.Output);
        }

        [Test]
        public void RoundTrip_Clip_ModifySetFrameRate_GetReflectsChange()
        {
            var path = TmpRoot + "/FrameRate.anim";
            BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_create",
                "{\"asset_paths\":[\"" + path + "\"],\"paths_hint\":[\"" + path + "\"]}");

            var modsJson = "[{\"type\":\"SetFrameRate\",\"frameRate\":30.0}]";
            var modify = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_modify",
                "{\"asset_path\":\"" + path + "\"," +
                "\"modifications_json\":\"" + JsonEscape(modsJson) + "\"," +
                "\"paths_hint\":[\"" + path + "\"]}");
            Assert.IsTrue(modify.Success);

            var get = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_get_data",
                "{\"asset_path\":\"" + path + "\"}");
            Assert.IsTrue(get.Success);
            StringAssert.Contains("\"frameRate\":30", get.Output);
        }

        [Test]
        public void RoundTrip_Clip_ModifyAddEvent_GetReflectsEvent()
        {
            var path = TmpRoot + "/Event.anim";
            BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_create",
                "{\"asset_paths\":[\"" + path + "\"],\"paths_hint\":[\"" + path + "\"]}");

            var modsJson = "[{\"type\":\"AddEvent\",\"time\":0.5,\"functionName\":\"OnAnimEvent\"," +
                           "\"stringParameter\":\"hello\"}]";
            var modify = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_modify",
                "{\"asset_path\":\"" + path + "\"," +
                "\"modifications_json\":\"" + JsonEscape(modsJson) + "\"," +
                "\"paths_hint\":[\"" + path + "\"]}");
            Assert.IsTrue(modify.Success, modify.ErrorMessage ?? modify.Output);

            var get = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_get_data",
                "{\"asset_path\":\"" + path + "\"}");
            Assert.IsTrue(get.Success);
            StringAssert.Contains("\"events\":[", get.Output);
            StringAssert.Contains("\"functionName\":\"OnAnimEvent\"", get.Output);
        }

        [Test]
        public void RoundTrip_Clip_ModifyBadComponentType_ReportsError_BatchSucceeds()
        {
            var path = TmpRoot + "/BatchErrors.anim";
            BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_create",
                "{\"asset_paths\":[\"" + path + "\"],\"paths_hint\":[\"" + path + "\"]}");

            // The first entry is invalid (no such type); the second is valid.
            // The call must return 200 with both `applied` (the valid one) and
            // `errors` (the invalid one), proving per-entry error accumulation.
            var modsJson = "[" +
                           "{\"type\":\"SetCurve\",\"componentType\":\"NoSuch.Type\"," +
                           "\"propertyName\":\"foo\",\"keyframes\":[{\"time\":0,\"value\":1}]}," +
                           "{\"type\":\"SetFrameRate\",\"frameRate\":24.0}" +
                           "]";
            var modify = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_modify",
                "{\"asset_path\":\"" + path + "\"," +
                "\"modifications_json\":\"" + JsonEscape(modsJson) + "\"," +
                "\"paths_hint\":[\"" + path + "\"]}");
            Assert.IsTrue(modify.Success, modify.ErrorMessage ?? modify.Output);
            StringAssert.Contains("\"errorCount\":1", modify.Output);
            StringAssert.Contains("\"applied\":[\"SetFrameRate(24)\"", modify.Output);
        }

        // -----------------------------------------------------------------
        // AnimatorController round-trip.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_Controller_Create_AddParameterAndState_GetReflects()
        {
            var path = TmpRoot + "/ControllerRoundTrip.controller";

            // Create.
            var create = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animator_create",
                "{\"asset_paths\":[\"" + path + "\"],\"paths_hint\":[\"" + path + "\"]}");
            Assert.IsTrue(create.Success, create.ErrorMessage ?? create.Output);
            StringAssert.Contains("\"createdPaths\":[\"" + path + "\"]", create.Output);

            // Add a parameter + a state on the base layer.
            var modsJson = "[" +
                           "{\"type\":\"AddParameter\",\"parameterName\":\"Speed\"," +
                           "\"parameterType\":\"Float\",\"defaultFloat\":0.5}," +
                           "{\"type\":\"AddState\",\"layerName\":\"Base Layer\",\"stateName\":\"Run\"}," +
                           "{\"type\":\"AddState\",\"layerName\":\"Base Layer\",\"stateName\":\"Idle\"}," +
                           "{\"type\":\"SetDefaultState\",\"layerName\":\"Base Layer\",\"stateName\":\"Idle\"}" +
                           "]";
            var modify = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animator_modify",
                "{\"asset_path\":\"" + path + "\"," +
                "\"modifications_json\":\"" + JsonEscape(modsJson) + "\"," +
                "\"paths_hint\":[\"" + path + "\"]}");
            Assert.IsTrue(modify.Success, modify.ErrorMessage ?? modify.Output);
            StringAssert.Contains("\"applied\":[\"AddParameter(Speed)\"", modify.Output);
            StringAssert.Contains("\"AddState(Base Layer, Run)\"", modify.Output);

            // Get reflects the changes.
            var get = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animator_get_data",
                "{\"asset_path\":\"" + path + "\"}");
            Assert.IsTrue(get.Success);
            StringAssert.Contains("\"name\":\"Speed\"", get.Output);
            StringAssert.Contains("\"type\":\"Float\"", get.Output);
            StringAssert.Contains("\"defaultFloat\":0.5", get.Output);
            StringAssert.Contains("\"defaultStateName\":\"Idle\"", get.Output);
            StringAssert.Contains("\"Run\"", get.Output);
        }

        [Test]
        public void RoundTrip_Controller_AddTransition_GetReflects()
        {
            var path = TmpRoot + "/ControllerTransitions.controller";
            BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animator_create",
                "{\"asset_paths\":[\"" + path + "\"],\"paths_hint\":[\"" + path + "\"]}");

            var modsJson = "[" +
                           "{\"type\":\"AddState\",\"layerName\":\"Base Layer\",\"stateName\":\"Idle\"}," +
                           "{\"type\":\"AddState\",\"layerName\":\"Base Layer\",\"stateName\":\"Move\"}," +
                           "{\"type\":\"AddParameter\",\"parameterName\":\"Go\",\"parameterType\":\"Trigger\"}," +
                           "{\"type\":\"SetDefaultState\",\"layerName\":\"Base Layer\",\"stateName\":\"Idle\"}," +
                           "{\"type\":\"AddTransition\"," +
                           "\"layerName\":\"Base Layer\"," +
                           "\"sourceStateName\":\"Idle\"," +
                           "\"destinationStateName\":\"Move\"," +
                           "\"hasExitTime\":false,\"duration\":0.2," +
                           "\"conditions\":[{\"parameter\":\"Go\",\"mode\":\"If\"}]}" +
                           "]";
            var modify = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animator_modify",
                "{\"asset_path\":\"" + path + "\"," +
                "\"modifications_json\":\"" + JsonEscape(modsJson) + "\"," +
                "\"paths_hint\":[\"" + path + "\"]}");
            Assert.IsTrue(modify.Success, modify.ErrorMessage ?? modify.Output);

            var get = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animator_get_data",
                "{\"asset_path\":\"" + path + "\"}");
            Assert.IsTrue(get.Success);
            StringAssert.Contains("\"destinationStateName\":\"Move\"", get.Output);
            StringAssert.Contains("\"parameter\":\"Go\"", get.Output);
        }

        [Test]
        public void RoundTrip_Controller_ModifyUnknownType_ReportsError()
        {
            var path = TmpRoot + "/ControllerUnknownType.controller";
            BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animator_create",
                "{\"asset_paths\":[\"" + path + "\"],\"paths_hint\":[\"" + path + "\"]}");

            var modsJson = "[{\"type\":\"NoSuchOperation\"}]";
            var modify = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animator_modify",
                "{\"asset_path\":\"" + path + "\"," +
                "\"modifications_json\":\"" + JsonEscape(modsJson) + "\"," +
                "\"paths_hint\":[\"" + path + "\"]}");
            Assert.IsTrue(modify.Success); // 200 — batch succeeds with error entry
            StringAssert.Contains("\"errorCount\":1", modify.Output);
            StringAssert.Contains("Unknown modification type", modify.Output);
        }

        // -----------------------------------------------------------------
        // Test helpers.
        // -----------------------------------------------------------------

        private static void CreateClip(string name)
        {
            var path = TmpRoot + "/" + name;
            BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_animation_create",
                "{\"asset_paths\":[\"" + path + "\"],\"paths_hint\":[\"" + path + "\"]}");
        }

        // Escape a JSON payload for embedding as a string literal inside the
        // outer tool body. Backslashes + quotes only — the bridge body parser
        // unescapes once, leaving the modifications_json value as the raw
        // JSON string the modification parser expects.
        private static string JsonEscape(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
