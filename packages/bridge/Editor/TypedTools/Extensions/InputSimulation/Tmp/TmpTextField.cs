// TextMeshPro input fields for inputsim_text (TextEntryTools.cs).
//
// Same contract as the uGUI field: characters go through TMP_InputField's own
// public ProcessEvent (onValidateInput, characterValidation incl. a custom
// validator, content type, characterLimit, read-only, line type). Caret moves use
// string positions only and never a selection: TMP resolves a selection through
// the text mesh, which an unfocused field may not have built.
#if UNITY_OPEN_MCP_EXT_INPUTSIM_TMP
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    [InitializeOnLoad]
    internal static class TmpTextFieldRegistration
    {
        static TmpTextFieldRegistration()
        {
            UiTextFields.Register(new TmpTextFieldAdapter());
        }
    }

    internal sealed class TmpTextFieldAdapter : IUiTextFieldAdapter
    {
        public string Kind => "tmp";
        public IUiTextField On(GameObject go)
        {
            var field = go.GetComponent<TMP_InputField>();
            return field != null ? new TmpTextField(field) : null;
        }
    }

    internal sealed class TmpTextField : IUiTextField
    {
        private readonly TMP_InputField field;
        public TmpTextField(TMP_InputField field) { this.field = field; }
        public string Kind => "tmp";
        public GameObject Go => field.gameObject;
        public Selectable Selectable => field;
        public string Text => field.text ?? "";
        public bool ReadOnly => field.readOnly;
        public bool Focused => field.isFocused;
        public bool HasTextComponent => field.textComponent != null;
        public int CharacterLimit => field.characterLimit;
        public string ContentType => UguiTextField.ToSnake(field.contentType.ToString());
        public string LineType => UguiTextField.ToSnake(field.lineType.ToString());
        public bool Masked => field.contentType == TMP_InputField.ContentType.Password || field.contentType == TMP_InputField.ContentType.Pin
                              || field.inputType == TMP_InputField.InputType.Password;
        public void Clear() { if (Text.Length > 0) field.text = ""; }
        public void CaretToEnd()
        {
            int end = Text.Length;
            field.selectionStringAnchorPosition = end;
            field.selectionStringFocusPosition = end;
        }
        public void Type(char c) => field.ProcessEvent(UiTextFields.KeyEvent(c));
        public void RefreshLabel() => field.ForceLabelUpdate();
        public void AddValueChanged(UnityAction<string> listener) => field.onValueChanged.AddListener(listener);
        public void RemoveValueChanged(UnityAction<string> listener) => field.onValueChanged.RemoveListener(listener);
        public void Submit() => field.onSubmit.Invoke(field.text);
        public void EndEdit()
        {
            if (field.isFocused) field.DeactivateInputField();
            else field.onEndEdit.Invoke(field.text);
        }
    }
}
#endif
