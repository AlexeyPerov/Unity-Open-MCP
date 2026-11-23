// inputsim_text — typing into a uGUI InputField through its own key path:
// replace / append / clear, the field's character limit, content type and
// onValidateInput deciding what goes in, onValueChanged per accepted character,
// end_edit / submit events and their order, single- vs multi-line newlines, a
// throwing listener, masked password answers, finding the field from a label, and
// the tool's argument / play-mode guards. The typing works the same in edit mode,
// so the core runs here directly; the play-mode tool is walked live on the demo
// fixture.
#if UNITY_OPEN_MCP_EXT_INPUTSIM_UGUI
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityOpenMcpBridge.Extensions.InputSimulation;

namespace UnityOpenMcpBridge.Tests.Extensions.InputSimulation
{
    public class TextEntryTests
    {
        private GameObject canvasGo;
        private Font font;

        [SetUp]
        public void SetUp()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            canvasGo = new GameObject("TextEntryTest_" + System.Guid.NewGuid().ToString("N").Substring(0, 8), typeof(RectTransform));
            canvasGo.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        }

        [TearDown]
        public void TearDown()
        {
            if (canvasGo != null) Object.DestroyImmediate(canvasGo);
        }

        private InputField Field(string name, string value = "")
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(canvasGo.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(300, 60);
            var field = go.AddComponent<InputField>();
            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(go.transform, false);
            var text = textGo.AddComponent<Text>();
            text.font = font;
            text.supportRichText = false;
            var placeholderGo = new GameObject("Placeholder", typeof(RectTransform));
            placeholderGo.transform.SetParent(go.transform, false);
            var placeholder = placeholderGo.AddComponent<Text>();
            placeholder.font = font;
            placeholder.text = "Nickname";
            field.textComponent = text;
            field.placeholder = placeholder;
            field.text = value;
            return field;
        }

        private static TextEntryOutcome Type(InputField field, string text, string mode = "replace", string end = "end_edit")
            => TextEntryTools.Type(new UguiTextField(field), text, mode, end);

        [Test]
        public void Replace_EmptiesThenTypesEachCharacterAndEndsTheEdit()
        {
            var field = Field("Nick", "old");
            var changes = new List<string>();
            var ended = new List<string>();
            field.onValueChanged.AddListener(changes.Add);
            field.onEndEdit.AddListener(ended.Add);

            var o = Type(field, "Alex");

            Assert.AreEqual("Alex", field.text);
            Assert.AreEqual("old", o.Before);
            Assert.AreEqual("Alex", o.After);
            Assert.IsTrue(o.Cleared);
            Assert.AreEqual(4, o.Accepted);
            Assert.AreEqual(0, o.Rejected.Count);
            CollectionAssert.AreEqual(new[] { "", "A", "Al", "Ale", "Alex" }, changes, "one change for the clear, one per character");
            Assert.AreEqual(5, o.ValueChanged);
            CollectionAssert.AreEqual(new[] { "Alex" }, ended);
            Assert.IsTrue(o.EndEdit);
            Assert.IsFalse(o.Submitted);
            Assert.AreEqual("Alex", field.textComponent.text, "the label shows what was typed");
            Assert.IsFalse(field.placeholder.enabled, "the placeholder is hidden");
            Assert.IsFalse(o.WasFocused);
        }

        [Test]
        public void Append_And_Clear()
        {
            var field = Field("Nick", "ab");
            var o = Type(field, "cd", "append", "none");
            Assert.AreEqual("abcd", field.text);
            Assert.IsFalse(o.Cleared);
            Assert.IsFalse(o.EndEdit);

            var cleared = Type(field, "", "clear");
            Assert.AreEqual("", field.text);
            Assert.AreEqual(0, cleared.Requested);
            Assert.IsTrue(cleared.EndEdit);
        }

        [Test]
        public void TheFieldDecides_LimitContentTypeAndValidator()
        {
            var limited = Field("Limited");
            limited.characterLimit = 3;
            var o = Type(limited, "abcdef");
            Assert.AreEqual("abc", limited.text);
            Assert.AreEqual(3, o.Rejected.Count);
            Assert.AreEqual("limit", o.Rejected[0].Value);
            Assert.AreEqual(3, o.Rejected[0].Key);

            var number = Field("Number");
            number.contentType = InputField.ContentType.IntegerNumber;
            o = Type(number, "12a3");
            Assert.AreEqual("123", number.text);
            Assert.AreEqual(1, o.Rejected.Count);
            Assert.AreEqual(2, o.Rejected[0].Key);
            Assert.AreEqual("validation", o.Rejected[0].Value);

            var upper = Field("Upper");
            upper.onValidateInput = (text, index, c) => char.ToUpperInvariant(c);
            o = Type(upper, "ab1");
            Assert.AreEqual("AB1", upper.text);
            Assert.AreEqual(2, o.Transformed);
        }

        [Test]
        public void Newlines_SingleLineDropsThem_MultiLineKeepsThem()
        {
            var single = Field("Single");
            var o = Type(single, "a\nb");
            Assert.AreEqual("ab", single.text);
            Assert.AreEqual(1, o.Rejected.Count);

            var multi = Field("Multi");
            multi.lineType = InputField.LineType.MultiLineNewline;
            Type(multi, "a\nb");
            Assert.AreEqual("a\nb", multi.text);
        }

        [Test]
        public void Submit_SendsSubmitThenEndEdit()
        {
            var field = Field("Search");
            var order = new List<string>();
            field.onSubmit.AddListener(v => order.Add("submit:" + v));
            field.onEndEdit.AddListener(v => order.Add("end:" + v));
            var o = Type(field, "boots", "replace", "submit");
            CollectionAssert.AreEqual(new[] { "submit:boots", "end:boots" }, order);
            Assert.IsTrue(o.Submitted);
            Assert.IsTrue(o.EndEdit);
        }

        [Test]
        public void AThrowingListenerIsReported_TheTextStillGoesIn()
        {
            var field = Field("Throws");
            field.onEndEdit.AddListener(_ => throw new System.InvalidOperationException("save failed"));
            UnityEngine.TestTools.LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("save failed"));
            var o = Type(field, "x");
            Assert.AreEqual("x", field.text);
            Assert.AreEqual("InvalidOperationException", o.ExceptionType);
            Assert.AreEqual("save failed", o.ExceptionMessage);
            StringAssert.Contains("\"exception\":{\"type\":\"InvalidOperationException\"",
                TextEntryTools.Answer(new UguiTextField(field), o, "replace", "end_edit", false, null));
        }

        [Test]
        public void Answer_MasksPasswordFieldsAndSecretText()
        {
            var password = Field("Password");
            password.contentType = InputField.ContentType.Password;
            var o = Type(password, "hunter2");
            Assert.AreEqual("hunter2", password.text);
            var json = TextEntryTools.Answer(new UguiTextField(password), o, "replace", "end_edit", false, null);
            StringAssert.Contains("\"masked\":true", json);
            StringAssert.Contains("\"lengthAfter\":7", json);
            StringAssert.DoesNotContain("hunter2", json);
            StringAssert.Contains("\"contentType\":\"password\"", json);

            var plain = Field("Plain");
            o = Type(plain, "visible");
            json = TextEntryTools.Answer(new UguiTextField(plain), o, "replace", "end_edit", false, null);
            StringAssert.Contains("\"textAfter\":\"visible\"", json);
            StringAssert.Contains("\"typed\":{\"requested\":7,\"accepted\":7", json);
            StringAssert.DoesNotContain("visible", TextEntryTools.Answer(new UguiTextField(plain), o, "replace", "end_edit", true, null));
        }

        [Test]
        public void Find_FromLabelOrPlaceholder_AndAmbiguousContainer()
        {
            var a = Field("A");
            Field("B");
            Assert.AreEqual(a.gameObject, UiTextFields.Find(a.placeholder.gameObject).Go, "a placeholder resolves to its field");
            Assert.AreEqual(a.gameObject, UiTextFields.Find(a.gameObject).Go);
            Assert.IsNull(UiTextFields.Find(canvasGo), "two fields below: the caller must name one");
            CollectionAssert.Contains(UiTextFields.Kinds(), "ugui");
        }

        [Test]
        public void Tool_GuardsArgumentsAndPlayMode()
        {
            StringAssert.Contains("\"play_mode_required\"", TextEntryTools.Text(text: "x", target_text: "Nickname"));
            if (!BridgeToolRegistry.Contains("unity_open_mcp_inputsim_touch"))
                Assert.Ignore("Tool registry not scanned in this Editor session (batch -runTests).");
            Assert.IsTrue(BridgeToolRegistry.TryGet(TextEntryTools.ToolName, out var info));
            Assert.IsFalse(info.IsMutating);
            Assert.AreEqual(GateMode.Off, info.Gate);
            Assert.AreEqual("input-simulation", info.Group);
        }
    }
}
#endif
