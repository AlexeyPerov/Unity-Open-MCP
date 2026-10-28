// TextMeshPro text in ui_snapshot / label targeting. Compiles only when the
// TMP reader sub-assembly does (com.unity.textmeshpro >= 3.0 or com.unity.ugui
// >= 2.0). Tests that create TMP components need the project's TMP Settings and
// a default font asset (TMP Essential Resources); without them they are ignored
// instead of opening TMP's resource importer.
#if UNITY_OPEN_MCP_EXT_INPUTSIM_TMP
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityOpenMcpBridge.Extensions.InputSimulation;

namespace UnityOpenMcpBridge.Tests.Extensions.InputSimulation
{
    public class TmpTextReaderTests
    {
        private GameObject canvasGo;
        private string canvasName;

        [Test]
        public void ReaderIsRegistered()
        {
            CollectionAssert.Contains(UiTextReaders.Sources(), "tmp");
        }

        [SetUp]
        public void SetUp()
        {
            canvasName = "TmpSnapTest_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        [TearDown]
        public void TearDown()
        {
            if (canvasGo != null) Object.DestroyImmediate(canvasGo);
        }

        private static void RequireTmpResources()
        {
            var settings = Resources.Load<TMP_Settings>("TMP Settings");
            if (settings == null || TMP_Settings.defaultFontAsset == null)
                Assert.Ignore("TMP Settings / default font asset not imported in this project.");
        }

        private TextMeshProUGUI Label(string name, float w, float h, string text, Transform parent = null)
        {
            if (canvasGo == null)
            {
                canvasGo = new GameObject(canvasName, typeof(RectTransform));
                canvasGo.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            }
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent != null ? parent : canvasGo.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(20, -20);
            rt.sizeDelta = new Vector2(w, h);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.fontSize = 24;
            t.text = text;
            return t;
        }

        [Test]
        public void ReadsTmpTextWithRichTextStripped()
        {
            RequireTmpResources();
            var t = Label("Title", 400, 60, "  <color=#ff0>Daily</color>   <b>Shop</b> ");
            Assert.IsTrue(new TmpTextReader().TryRead(t.gameObject, out var info));
            Assert.AreEqual("tmp", info.Source);
            Assert.IsFalse(info.Overflowing);
            Assert.AreEqual("Daily Shop", UiText.Display(info.Raw, info.RichText));
        }

        [Test]
        public void FlagsOverflowFromPreferredSize()
        {
            RequireTmpResources();
            var t = Label("Long", 120, 30, "This TextMeshPro label is far too long for its tiny rect.");
            Assert.IsTrue(new TmpTextReader().TryRead(t.gameObject, out var info));
            Assert.IsTrue(info.Overflowing);
            Assert.Greater(info.Content.y, 30f);
            var json = UiSnapshotTools.Snapshot(under: canvasName, page_size: 1);
            StringAssert.Contains("\"source\":\"tmp\"", json);
            StringAssert.Contains(canvasName + "/Long", json);
        }

        [Test]
        public void TmpLabelOfAButtonIsTargetable()
        {
            RequireTmpResources();
            Label("Anchor", 10, 10, "");
            var buttonGo = new GameObject("Btn_07", typeof(RectTransform));
            var brt = (RectTransform)buttonGo.transform;
            brt.SetParent(canvasGo.transform, false);
            brt.anchorMin = brt.anchorMax = new Vector2(0, 1);
            brt.pivot = new Vector2(0, 1);
            brt.anchoredPosition = new Vector2(20, -100);
            brt.sizeDelta = new Vector2(240, 80);
            buttonGo.AddComponent<UnityEngine.UI.Image>();
            buttonGo.AddComponent<UnityEngine.UI.Button>();
            Label("Label", 240, 80, "Claim ×2", buttonGo.transform).rectTransform.anchoredPosition = Vector2.zero;
            Assert.IsTrue(UiTextTargets.TryResolve("claim", "contains", canvasName, null, null, out var res, out var error), error);
            Assert.AreEqual(canvasName + "/Btn_07", res.Node.Path);
            Assert.AreEqual("Claim ×2", res.Node.Text);
        }

        [Test]
        public void DisabledTmpComponentIsNotRead()
        {
            RequireTmpResources();
            var t = Label("Off", 200, 40, "hidden");
            t.enabled = false;
            Assert.IsFalse(new TmpTextReader().TryRead(t.gameObject, out _));
        }
    }
}
#endif
