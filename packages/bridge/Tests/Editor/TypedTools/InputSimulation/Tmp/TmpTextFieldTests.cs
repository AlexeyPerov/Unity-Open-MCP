// inputsim_text on a TextMeshPro input field: the adapter is registered, and
// characters go through TMP_InputField's own key path (replace / append, the
// character limit, content type, onValidateInput, end events) without a
// selection. Needs TMP Settings and a default font asset (TMP Essential
// Resources); without them the tests are ignored.
#if UNITY_OPEN_MCP_EXT_INPUTSIM_TMP
using System.Collections.Generic;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityOpenMcpBridge.Extensions.InputSimulation;

namespace UnityOpenMcpBridge.Tests.Extensions.InputSimulation
{
    public class TmpTextFieldTests
    {
        private GameObject canvasGo;

        [TearDown]
        public void TearDown()
        {
            if (canvasGo != null) Object.DestroyImmediate(canvasGo);
        }

        private TMP_InputField Field(string value = "")
        {
            var settings = Resources.Load<TMP_Settings>("TMP Settings");
            if (settings == null || TMP_Settings.defaultFontAsset == null)
                Assert.Ignore("TMP Settings / default font asset not imported in this project.");
            if (canvasGo == null)
            {
                canvasGo = new GameObject("TmpFieldTest_" + System.Guid.NewGuid().ToString("N").Substring(0, 8), typeof(RectTransform));
                canvasGo.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            }
            var go = new GameObject("Field", typeof(RectTransform));
            go.transform.SetParent(canvasGo.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(300, 60);
            var viewport = new GameObject("Text Area", typeof(RectTransform), typeof(UnityEngine.UI.RectMask2D));
            viewport.transform.SetParent(go.transform, false);
            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(viewport.transform, false);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            var field = go.AddComponent<TMP_InputField>();
            field.textViewport = (RectTransform)viewport.transform;
            field.textComponent = text;
            field.text = value;
            return field;
        }

        private static TextEntryOutcome Type(TMP_InputField field, string text, string mode = "replace", string end = "end_edit")
            => TextEntryTools.Type(new TmpTextField(field), text, mode, end);

        [Test]
        public void AdapterIsRegistered()
        {
            CollectionAssert.Contains(UiTextFields.Kinds(), "tmp");
        }

        [Test]
        public void ReplaceAppendAndEndEvents()
        {
            var field = Field("old");
            Assert.AreEqual("tmp", UiTextFields.Find(field.textComponent.gameObject).Kind, "the text child resolves to its field");
            var changes = new List<string>();
            var order = new List<string>();
            field.onValueChanged.AddListener(changes.Add);
            field.onSubmit.AddListener(v => order.Add("submit:" + v));
            field.onEndEdit.AddListener(v => order.Add("end:" + v));

            var o = Type(field, "Alex", "replace", "submit");
            Assert.AreEqual("Alex", field.text);
            Assert.AreEqual(4, o.Accepted);
            CollectionAssert.AreEqual(new[] { "", "A", "Al", "Ale", "Alex" }, changes);
            CollectionAssert.AreEqual(new[] { "submit:Alex", "end:Alex" }, order);

            Type(field, "!", "append", "none");
            Assert.AreEqual("Alex!", field.text);
        }

        [Test]
        public void TheFieldDecides_LimitContentTypeAndValidator()
        {
            var limited = Field();
            limited.characterLimit = 2;
            var o = Type(limited, "abc");
            Assert.AreEqual("ab", limited.text);
            Assert.AreEqual("limit", o.Rejected[0].Value);

            var number = Field();
            number.contentType = TMP_InputField.ContentType.IntegerNumber;
            o = Type(number, "4x2");
            Assert.AreEqual("42", number.text);
            Assert.AreEqual(1, o.Rejected.Count);

            var upper = Field();
            upper.onValidateInput = (text, index, c) => char.ToUpperInvariant(c);
            o = Type(upper, "ok");
            Assert.AreEqual("OK", upper.text);
            Assert.AreEqual(2, o.Transformed);
        }
    }
}
#endif
