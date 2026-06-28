// M20 Plan 3 / T20.3.2 — UI (uGUI) embedded domain tools EditMode tests.
//
// Ungated (no UNITY_OPEN_MCP_EXT_UI): the Canvas / CanvasScaler /
// GraphicRaycaster / Image / Text / Button / Slider / Toggle / InputField /
// layout groups / EventSystem types are built-in engine (UnityEngine.UI +
// UnityEngine.EventSystems) types present on every Unity install, so the
// tools — and this suite — compile unconditionally. The test asmdef only
// constrains UNITY_TEST_FRAMEWORK.
//
// TextMesh Pro (TMP_Text) is OPTIONAL — the TMP_Text tests run only when TMP
// is installed in the test project (detected via reflection at runtime); when
// TMP is absent, the TMP_Text branch returns `tmp_package_required` and the
// suite Assert.Ignores the TMP-positive path so the rest of the surface
// (registry, paths_hint contract, canvas round-trip, layout group add, element
// modify) is covered by the unconditional tests.
#pragma warning disable CS0618
using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests.Extensions.UI
{
    public class UIToolsTests
    {
        // The 4 catalog tool ids this domain must register.
        private static readonly string[] ExpectedTools =
        {
            "unity_open_mcp_ui_canvas_add",
            "unity_open_mcp_ui_element_add",
            "unity_open_mcp_ui_layout_group_add",
            "unity_open_mcp_ui_element_modify",
        };

        [Test]
        public void Registry_AllFourToolsDiscovered()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected ui tool '{id}' to be discovered by BridgeToolRegistry.");
            }
        }

        [Test]
        public void Registry_AllToolsAreMutatingAndEditorSettle()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var info),
                    $"Tool '{id}' should be discoverable.");
                Assert.IsTrue(info.IsMutating, $"{id} should be mutating.");
                Assert.AreEqual(LifecyclePolicy.EditorSettle, info.Lifecycle,
                    $"{id} should declare EditorSettle lifecycle.");
            }
        }

        [Test]
        public void Registry_AllToolsAssignedToUIGroup()
        {
            foreach (var id in ExpectedTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var info));
                Assert.AreEqual("ui", info.Group,
                    $"Expected '{id}' to be in the 'ui' group.");
            }
        }

        // -----------------------------------------------------------------
        // paths_hint contract — every mutating tool refuses empty scope.
        // See AudioToolsTests for the dual-layer rationale.
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
        public void Dispatch_CanvasAdd_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_ui_canvas_add",
                "{}");
            AssertErrorEnvelope(result, "paths_hint_required");
        }

        [Test]
        public void Dispatch_ElementAdd_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_ui_element_add",
                "{\"element_type\":\"Text\"}");
            AssertErrorEnvelope(result, "paths_hint_required");
        }

        [Test]
        public void Dispatch_LayoutGroupAdd_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_ui_layout_group_add",
                "{\"layout_type\":\"VerticalLayoutGroup\"}");
            AssertErrorEnvelope(result, "paths_hint_required");
        }

        [Test]
        public void Dispatch_ElementModify_MissingPathsHint_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_ui_element_modify",
                "{\"component_type\":\"Text\",\"fields_json\":\"[]\"}");
            AssertErrorEnvelope(result, "paths_hint_required");
        }

        // -----------------------------------------------------------------
        // Parameter / target resolution branches.
        // -----------------------------------------------------------------

        [Test]
        public void Dispatch_ElementAdd_MissingElementType_ReturnsError()
        {
            var parent = new GameObject("UINoTypeParent",
                typeof(RectTransform), typeof(Canvas));
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_element_add",
                    "{\"parent_instance_id\":" + parent.GetInstanceID() +
                    ",\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "missing_parameter");
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void Dispatch_ElementAdd_NoParentTarget_ReturnsError()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_ui_element_add",
                "{\"element_type\":\"Text\",\"paths_hint\":[\"Assets/T.unity\"]}");
            AssertErrorEnvelope(result, "missing_parameter");
        }

        [Test]
        public void Dispatch_ElementAdd_OnUnknownParent_ReturnsParentNotFound()
        {
            var result = BridgeToolRegistry.TryDispatch(
                "unity_open_mcp_ui_element_add",
                "{\"parent_path\":\"__nonexistent_parent__\",\"element_type\":\"Text\"," +
                "\"paths_hint\":[\"Assets/T.unity\"]}");
            AssertErrorEnvelope(result, "parent_not_found");
        }

        [Test]
        public void Dispatch_ElementAdd_MissingSpriteAsset_ReturnsAssetNotFound()
        {
            var parent = new GameObject("UISpriteParent",
                typeof(RectTransform), typeof(Canvas));
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_element_add",
                    "{\"parent_instance_id\":" + parent.GetInstanceID() +
                    ",\"element_type\":\"Image\"," +
                    "\"sprite_path\":\"Assets/Does/Not/Exist.png\"," +
                    "\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "asset_not_found");
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void Dispatch_ElementAdd_InvalidElementType_ReturnsError()
        {
            var parent = new GameObject("UIBadTypeParent",
                typeof(RectTransform), typeof(Canvas));
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_element_add",
                    "{\"parent_instance_id\":" + parent.GetInstanceID() +
                    ",\"element_type\":\"NotARealElement\"," +
                    "\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "invalid_element_type");
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void Dispatch_LayoutGroupAdd_InvalidLayoutType_ReturnsError()
        {
            var parent = new GameObject("UIBadLayoutParent",
                typeof(RectTransform), typeof(Canvas));
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_layout_group_add",
                    "{\"instance_id\":" + parent.GetInstanceID() +
                    ",\"layout_type\":\"DiagonalLayoutGroup\"," +
                    "\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "invalid_layout_type");
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void Dispatch_ElementModify_OnTargetWithoutComponent_ReturnsComponentNotFound()
        {
            var go = new GameObject("UIModifyNoComp", typeof(RectTransform));
            try
            {
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_element_modify",
                    "{\"instance_id\":" + go.GetInstanceID() +
                    ",\"component_type\":\"Button\"," +
                    "\"fields_json\":\"[]\"," +
                    "\"paths_hint\":[\"Assets/T.unity\"]}");
                AssertErrorEnvelope(result, "component_not_found");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Canvas round-trip: add → companions ensured → EventSystem ensured.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_CanvasAdd_CreatesCanvasScalerGraphicRaycasterAndEventSystem()
        {
            // Clean up any stray EventSystem from prior tests so the
            // addedEventSystem assertion is unambiguous.
            foreach (var es in UnityEngine.Object.FindObjectsByType<EventSystem>(
                         FindObjectsInactive.Include))
                UnityEngine.Object.DestroyImmediate(es.gameObject);

            var go = new GameObject("UICanvasHost", typeof(RectTransform));
            try
            {
                var body = "{\"instance_id\":" + go.GetInstanceID() +
                           ",\"render_mode\":\"ScreenSpaceOverlay\"," +
                           "\"sorting_order\":5," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_canvas_add", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);

                StringAssert.Contains("\"added\":true", result.Output);
                StringAssert.Contains("\"addedCanvasScaler\":true", result.Output);
                StringAssert.Contains("\"addedGraphicRaycaster\":true", result.Output);
                StringAssert.Contains("\"addedEventSystem\":true", result.Output);

                Assert.IsNotNull(go.GetComponent<Canvas>());
                Assert.IsNotNull(go.GetComponent<CanvasScaler>());
                Assert.IsNotNull(go.GetComponent<GraphicRaycaster>());

                var canvas = go.GetComponent<Canvas>();
                Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode);
                Assert.AreEqual(5, canvas.sortingOrder);

                // EventSystem created at scene root.
                var es = UnityEngine.Object.FindObjectsByType<EventSystem>(
                    FindObjectsInactive.Include);
                Assert.AreEqual(1, es.Length, "Expected exactly one EventSystem.");
            }
            finally
            {
                Object.DestroyImmediate(go);
                foreach (var es in UnityEngine.Object.FindObjectsByType<EventSystem>(
                         FindObjectsInactive.Include))
                    UnityEngine.Object.DestroyImmediate(es.gameObject);
            }
        }

        [Test]
        public void RoundTrip_CanvasAdd_Idempotent_ReusingReportsAddedFalse()
        {
            foreach (var es in UnityEngine.Object.FindObjectsByType<EventSystem>(
                         FindObjectsInactive.Include))
                UnityEngine.Object.DestroyImmediate(es.gameObject);

            var go = new GameObject("UICanvasIdem", typeof(RectTransform));
            go.AddComponent<Canvas>();
            try
            {
                var body = "{\"instance_id\":" + go.GetInstanceID() +
                           ",\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_canvas_add", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"added\":false", result.Output);
                // Companions are still ensured.
                Assert.IsNotNull(go.GetComponent<CanvasScaler>());
                Assert.IsNotNull(go.GetComponent<GraphicRaycaster>());
            }
            finally
            {
                Object.DestroyImmediate(go);
                foreach (var es in UnityEngine.Object.FindObjectsByType<EventSystem>(
                         FindObjectsInactive.Include))
                    UnityEngine.Object.DestroyImmediate(es.gameObject);
            }
        }

        // -----------------------------------------------------------------
        // Element round-trip: add Text/Image/Button under a canvas parent.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_ElementAdd_Text_ParentedUnderCanvas()
        {
            var parent = new GameObject("UITextParent",
                typeof(RectTransform), typeof(Canvas));
            try
            {
                var body = "{\"parent_instance_id\":" + parent.GetInstanceID() +
                           ",\"element_type\":\"Text\",\"element_name\":\"Label\"," +
                           "\"text\":\"Hello\",\"color\":\"1,1,1,1\"," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_element_add", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"type\":\"Text\"", result.Output);

                var child = parent.transform.Find("Label");
                Assert.IsNotNull(child, "Text child should be parented under the parent.");
                var text = child.GetComponent<Text>();
                Assert.IsNotNull(text);
                Assert.AreEqual("Hello", text.text);
                Assert.AreEqual(Color.white, text.color);
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void RoundTrip_ElementAdd_Image_ParentedAndColored()
        {
            var parent = new GameObject("UIImageParent",
                typeof(RectTransform), typeof(Canvas));
            try
            {
                var body = "{\"parent_instance_id\":" + parent.GetInstanceID() +
                           ",\"element_type\":\"Image\"," +
                           "\"color\":\"1,0,0,1\"," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_element_add", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"type\":\"Image\"", result.Output);

                // The element GameObject defaults its name to element_type.
                var child = parent.transform.Find("Image");
                Assert.IsNotNull(child);
                var img = child.GetComponent<Image>();
                Assert.IsNotNull(img);
                Assert.AreEqual(new Color(1, 0, 0, 1), img.color);
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void RoundTrip_ElementAdd_Button_AddsImageAndButton()
        {
            var parent = new GameObject("UIButtonParent",
                typeof(RectTransform), typeof(Canvas));
            try
            {
                var body = "{\"parent_instance_id\":" + parent.GetInstanceID() +
                           ",\"element_type\":\"Button\"," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_element_add", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);

                var child = parent.transform.Find("Button");
                Assert.IsNotNull(child);
                Assert.IsNotNull(child.GetComponent<Image>());
                Assert.IsNotNull(child.GetComponent<Button>());
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void RoundTrip_ElementAdd_TMP_Text_RequiresTmpOrReturnsPackageRequired()
        {
            var parent = new GameObject("UITmppParent",
                typeof(RectTransform), typeof(Canvas));
            try
            {
                var body = "{\"parent_instance_id\":" + parent.GetInstanceID() +
                           ",\"element_type\":\"TMP_Text\",\"text\":\"Hello\"," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_element_add", body);

                if (IsTmpInstalled())
                {
                    Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                    StringAssert.Contains("\"type\":\"TMP_Text\"", result.Output);
                }
                else
                {
                    // TMP absent — the tool MUST return tmp_package_required
                    // and NOT silently fall back to a legacy Text component.
                    AssertErrorEnvelope(result, "tmp_package_required");
                    var child = parent.transform.Find("TMP_Text");
                    Assert.IsNull(child,
                        "No child GameObject should have been created when TMP is absent.");
                }
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        // -----------------------------------------------------------------
        // Layout group round-trip.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_LayoutGroupAdd_Vertical_AppliesPaddingSpacingChildControl()
        {
            var parent = new GameObject("UILayoutParent",
                typeof(RectTransform), typeof(Canvas));
            try
            {
                var body = "{\"instance_id\":" + parent.GetInstanceID() +
                           ",\"layout_type\":\"VerticalLayoutGroup\"," +
                           "\"padding\":\"5,10,15,20\",\"spacing\":\"4,4\"," +
                           "\"child_alignment\":\"MiddleCenter\"," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_layout_group_add", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"added\":true", result.Output);
                StringAssert.Contains("\"type\":\"VerticalLayoutGroup\"", result.Output);

                var vg = parent.GetComponent<VerticalLayoutGroup>();
                Assert.IsNotNull(vg);
                Assert.AreEqual(5, vg.padding.left);
                Assert.AreEqual(10, vg.padding.right);
                Assert.AreEqual(15, vg.padding.top);
                Assert.AreEqual(20, vg.padding.bottom);
                Assert.AreEqual(4f, vg.spacing);
                Assert.AreEqual(TextAnchor.MiddleCenter, vg.childAlignment);
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void RoundTrip_LayoutGroupAdd_Grid_AppliesVectorSpacing()
        {
            var parent = new GameObject("UIGridParent",
                typeof(RectTransform), typeof(Canvas));
            try
            {
                var body = "{\"instance_id\":" + parent.GetInstanceID() +
                           ",\"layout_type\":\"GridLayoutGroup\"," +
                           "\"spacing\":\"10,20\"," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_layout_group_add", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);

                var grid = parent.GetComponent<GridLayoutGroup>();
                Assert.IsNotNull(grid);
                Assert.AreEqual(new Vector2(10, 20), grid.spacing);
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        // -----------------------------------------------------------------
        // Element modify round-trip.
        // -----------------------------------------------------------------

        [Test]
        public void RoundTrip_ElementModify_ButtonInteractable_Applies()
        {
            var go = new GameObject("UIModifyButton",
                typeof(RectTransform), typeof(Image));
            var btn = go.AddComponent<Button>();
            btn.interactable = true;
            try
            {
                var body = "{\"instance_id\":" + go.GetInstanceID() +
                           ",\"component_type\":\"Button\"," +
                           "\"fields_json\":\"[{\\\"field\\\":\\\"interactable\\\"," +
                           "\\\"value\\\":false,\\\"type\\\":\\\"bool\\\"}]\"," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_element_modify", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);
                StringAssert.Contains("\"field\":\"interactable\"", result.Output);
                StringAssert.Contains("\"applied\":true", result.Output);

                Assert.IsFalse(btn.interactable);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RoundTrip_ElementModify_ImageColor_Applies()
        {
            var go = new GameObject("UIModifyImage",
                typeof(RectTransform), typeof(Image));
            var img = go.GetComponent<Image>();
            try
            {
                var body = "{\"instance_id\":" + go.GetInstanceID() +
                           ",\"component_type\":\"Image\"," +
                           "\"fields_json\":\"[{\\\"field\\\":\\\"color\\\"," +
                           "\\\"value\\\":\\\"0,0,1,1\\\",\\\"type\\\":\\\"color\\\"}]\"," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_element_modify", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);

                Assert.AreEqual(new Color(0, 0, 1, 1), img.color);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RoundTrip_ElementModify_TextText_Applies()
        {
            var go = new GameObject("UIModifyText",
                typeof(RectTransform));
            var text = go.AddComponent<Text>();
            try
            {
                var body = "{\"instance_id\":" + go.GetInstanceID() +
                           ",\"component_type\":\"Text\"," +
                           "\"fields_json\":\"[{\\\"field\\\":\\\"text\\\"," +
                           "\\\"value\\\":\\\"Updated\\\",\\\"type\\\":\\\"string\\\"}]\"," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_element_modify", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);

                Assert.AreEqual("Updated", text.text);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RoundTrip_ElementModify_LayoutElementPreferredSizes_Applies()
        {
            var go = new GameObject("UIModifyLE",
                typeof(RectTransform));
            var le = go.AddComponent<LayoutElement>();
            try
            {
                var body = "{\"instance_id\":" + go.GetInstanceID() +
                           ",\"component_type\":\"LayoutElement\"," +
                           "\"fields_json\":\"[" +
                           "{\\\"field\\\":\\\"preferredWidth\\\",\\\"value\\\":200,\\\"type\\\":\\\"float\\\"}," +
                           "{\\\"field\\\":\\\"preferredHeight\\\",\\\"value\\\":60,\\\"type\\\":\\\"float\\\"}]\"," +
                           "\"paths_hint\":[\"Assets/T.unity\"]}";
                var result = BridgeToolRegistry.TryDispatch(
                    "unity_open_mcp_ui_element_modify", body);
                Assert.IsTrue(result.Success, result.ErrorMessage ?? result.Output);

                Assert.AreEqual(200f, le.preferredWidth);
                Assert.AreEqual(60f, le.preferredHeight);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // -----------------------------------------------------------------
        // Helpers.
        // -----------------------------------------------------------------

        /// <summary>
        /// Detect whether TextMesh Pro is installed in the test project by
        /// resolving the TMPro.TMP_Text type via reflection. The UI tools use
        /// the same detection at call time.
        /// </summary>
        private static bool IsTmpInstalled()
        {
            return AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(a => a.GetType("TMPro.TMP_Text"))
                .FirstOrDefault(t => t != null) != null;
        }
    }
}
