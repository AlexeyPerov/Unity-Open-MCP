// Input simulation — text entry into uGUI input fields (unity_open_mcp_inputsim_text).
//
// Types text the way a hardware keyboard does: character by character through
// the field's own public ProcessEvent (a KeyDown carrying the character), so the
// field's onValidateInput, characterValidation / content type, characterLimit,
// read-only flag, line type and (uGUI) font check decide what goes in, and
// onValueChanged fires once per accepted character. No selection is used (a
// TextMeshPro selection needs a built mesh): `replace` empties the field first
// (text = "", one onValueChanged(""), as select-all + delete), `append` puts the
// caret at the end, `clear` only empties. `end` then finishes the edit the way a
// player does: end_edit (onEndEdit — tapping away), submit (onSubmit, then
// onEndEdit — Enter in a single-line field) or none. A focused field ends through
// its own DeactivateInputField; an unfocused one gets the events invoked. Focus
// itself is not emulated (activation is deferred a frame and would select all):
// tap the field with inputsim_pointer first when the game reacts to it.
//
// UnityEngine.UI.InputField is handled here; TMP_InputField registers an adapter
// from the optional TextMeshPro sub-assembly (UiTextFields.Register), as the TMP
// text reader does. Password / PIN fields answer lengths only.
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    // One editable field, whatever its component.
    internal interface IUiTextField
    {
        string Kind { get; }                 // "ugui" | "tmp"
        GameObject Go { get; }
        Selectable Selectable { get; }
        string Text { get; }
        bool ReadOnly { get; }
        bool Focused { get; }
        bool HasTextComponent { get; }
        int CharacterLimit { get; }
        string ContentType { get; }
        string LineType { get; }
        bool Masked { get; }                 // password / PIN: never echo the text
        void Clear();                        // text = "" (fires onValueChanged when it changes)
        void CaretToEnd();
        void Type(char c);                   // one KeyDown through the field's own key path
        void RefreshLabel();                 // ForceLabelUpdate: ProcessEvent leaves the label to the field's own update
        void AddValueChanged(UnityAction<string> listener);
        void RemoveValueChanged(UnityAction<string> listener);
        void Submit();                       // onSubmit(text)
        void EndEdit();                      // focused: DeactivateInputField; else onEndEdit(text)
    }

    internal interface IUiTextFieldAdapter
    {
        string Kind { get; }
        // The field component on exactly this GameObject, or null.
        IUiTextField On(GameObject go);
    }

    internal static class UiTextFields
    {
        private static readonly List<IUiTextFieldAdapter> adapters = new List<IUiTextFieldAdapter> { new UguiTextFieldAdapter() };

        internal static void Register(IUiTextFieldAdapter adapter)
        {
            if (adapter == null) return;
            for (int i = 0; i < adapters.Count; i++)
                if (adapters[i].Kind == adapter.Kind) { adapters[i] = adapter; return; }
            adapters.Add(adapter);
        }

        internal static List<string> Kinds()
        {
            var list = new List<string>();
            foreach (var a in adapters) list.Add(a.Kind);
            return list;
        }

        // The field on the GameObject, else its nearest field ancestor (a label or
        // placeholder was targeted), else the only field below it.
        internal static IUiTextField Find(GameObject go)
        {
            for (var t = go != null ? go.transform : null; t != null; t = t.parent)
            {
                var field = On(t.gameObject);
                if (field != null) return field;
            }
            if (go == null) return null;
            IUiTextField below = null;
            foreach (var t in go.GetComponentsInChildren<Transform>(false))
            {
                var field = On(t.gameObject);
                if (field == null) continue;
                if (below != null) return null;   // ambiguous: the caller names one
                below = field;
            }
            return below;
        }

        private static IUiTextField On(GameObject go)
        {
            foreach (var a in adapters)
            {
                var field = a.On(go);
                if (field != null) return field;
            }
            return null;
        }

        // A KeyDown carrying one character and no key: the fields' KeyPressed
        // skips every key case and takes the character path.
        internal static Event KeyEvent(char c) => new Event
        {
            type = EventType.KeyDown, character = c, keyCode = KeyCode.None, modifiers = EventModifiers.None,
        };
    }

    internal sealed class UguiTextFieldAdapter : IUiTextFieldAdapter
    {
        public string Kind => "ugui";
        public IUiTextField On(GameObject go)
        {
            var field = go.GetComponent<InputField>();
            return field != null ? new UguiTextField(field) : null;
        }
    }

    internal sealed class UguiTextField : IUiTextField
    {
        private readonly InputField field;
        public UguiTextField(InputField field) { this.field = field; }
        public string Kind => "ugui";
        public GameObject Go => field.gameObject;
        public Selectable Selectable => field;
        public string Text => field.text ?? "";
        public bool ReadOnly => field.readOnly;
        public bool Focused => field.isFocused;
        public bool HasTextComponent => field.textComponent != null && field.textComponent.font != null;
        public int CharacterLimit => field.characterLimit;
        public string ContentType => ToSnake(field.contentType.ToString());
        public string LineType => ToSnake(field.lineType.ToString());
        public bool Masked => field.contentType == InputField.ContentType.Password || field.contentType == InputField.ContentType.Pin
                              || field.inputType == InputField.InputType.Password;
        public void Clear() { if (Text.Length > 0) field.text = ""; }
        public void CaretToEnd() => field.caretPosition = Text.Length;
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

        internal static string ToSnake(string pascal)
        {
            var sb = new StringBuilder(pascal.Length + 4);
            for (int i = 0; i < pascal.Length; i++)
            {
                char c = pascal[i];
                if (char.IsUpper(c) && i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }

    // What one call typed, for the answer.
    internal sealed class TextEntryOutcome
    {
        public string Before = "", After = "";
        public int Requested, Accepted, Transformed, ValueChanged;
        public bool WasFocused;
        public readonly List<KeyValuePair<int, string>> Rejected = new List<KeyValuePair<int, string>>(); // index → reason
        public bool Cleared, EndEdit, Submitted;
        public string ExceptionType, ExceptionMessage;

        public void Caught(System.Exception ex)
        {
            Debug.LogException(ex);
            if (ExceptionType != null) return;
            ExceptionType = ex.GetType().Name;
            ExceptionMessage = ex.Message;
        }
    }

    [BridgeToolType]
    public static class TextEntryTools
    {
        internal const string ToolName = "unity_open_mcp_inputsim_text";
        internal const int MaxTextLength = 1000;
        private static readonly string[] Modes = { "replace", "append", "clear" };
        private static readonly string[] Ends = { "end_edit", "submit", "none" };

        [BridgeTool(ToolName,
            Title = "Input Simulation: Type Text",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = false,
            IdempotentHint = false,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "input-simulation")]
        [System.ComponentModel.Description(
            "Type text into a uGUI InputField or TextMeshPro input field during play mode, character by " +
            "character through the field's own validation, then end the edit (end_edit / submit / none). " +
            "Play-mode only; gate-free. Requires com.unity.ugui.")]
        public static string Text(
            string text = null,
            string mode = "replace",
            string end = "end_edit",
            long? object_id = null,
            string target = null,
            string target_text = null,
            string under = null,
            int? index = null,
            string text_match = null,
            bool secret = false)
        {
            if (!EditorApplication.isPlaying)
                return InputSimulationJson.Error("play_mode_required",
                    "Typing into a field requires play mode. Call unity_open_mcp_editor_set_state(state=\"play\") first.");
            mode = string.IsNullOrEmpty(mode) ? "replace" : mode;
            end = string.IsNullOrEmpty(end) ? "end_edit" : end;
            if (System.Array.IndexOf(Modes, mode) < 0)
                return InputSimulationJson.Error("invalid_arguments", $"Unknown mode '{mode}'. Valid: replace, append, clear.");
            if (System.Array.IndexOf(Ends, end) < 0)
                return InputSimulationJson.Error("invalid_arguments", $"Unknown end '{end}'. Valid: end_edit, submit, none.");
            if (mode != "clear" && text == null)
                return InputSimulationJson.Error("invalid_arguments", "text is required (mode clear empties the field without it).");
            if (mode == "clear" && !string.IsNullOrEmpty(text))
                return InputSimulationJson.Error("invalid_arguments", "mode clear takes no text; use replace to type new text.");
            if (text != null && text.Length > MaxTextLength)
                return InputSimulationJson.Error("invalid_arguments", $"text is {text.Length} characters; at most {MaxTextLength} per call.");

            int forms = (object_id.HasValue ? 1 : 0) + (!string.IsNullOrEmpty(target) ? 1 : 0) + (!string.IsNullOrEmpty(target_text) ? 1 : 0);
            if (forms == 0)
                return InputSimulationJson.Error("invalid_arguments", "Name the field: target_text (its label or placeholder as ui_snapshot shows it), object_id, or target.");
            if (forms > 1)
                return InputSimulationJson.Error("conflicting_targets", "Pass one target form: target_text, object_id, or target.");
            if (string.IsNullOrEmpty(target_text) && (!string.IsNullOrEmpty(under) || index.HasValue || text_match != null))
                return InputSimulationJson.Error("invalid_arguments", "under, index and text_match refine target_text; pass target_text with them.");

            GameObject go;
            string textTarget = null;
            if (!string.IsNullOrEmpty(target_text))
            {
                if (!UiTextTargets.TryResolve(target_text, text_match, under, index, null, out var byText, out var textError))
                    return textError;
                go = byText.Node.Go;
                textTarget = byText.Json();
            }
            else if (object_id.HasValue)
            {
                go = PointerTargets.FindByInstanceId(object_id.Value);
                if (go == null)
                    return InputSimulationJson.Error("object_not_found", $"No live GameObject for object_id {object_id.Value}.");
            }
            else
            {
                var candidates = new List<string>();
                go = PointerTargets.FindByPath(target, candidates);
                if (go == null && candidates.Count > 0)
                    return InputSimulationJson.ErrorWithCandidates("ambiguous_target",
                        $"Target '{target}' matches {candidates.Count} active GameObjects. Pass the full path or an object_id.", candidates);
                if (go == null)
                    return InputSimulationJson.Error("target_not_found", $"No active GameObject found for target '{target}'.");
            }

            var field = UiTextFields.Find(go);
            if (field == null)
                return InputSimulationJson.Error("not_a_text_field",
                    $"'{PointerTargets.BuildPath(go)}' is not an input field and has no single input field above or below it " +
                    $"(handled: {string.Join(", ", UiTextFields.Kinds())}). Find it with ui_snapshot role=input.");
            if (!field.Go.activeInHierarchy)
                return InputSimulationJson.Error("field_inactive", $"The input field '{PointerTargets.BuildPath(field.Go)}' is not active.");
            if (!field.Selectable.IsInteractable() || !PointerTargets.ComputeInteractable(field.Go))
                return FieldError("field_not_interactable", "The input field is not interactable (disabled, or a CanvasGroup blocks it): a player could not type into it.", field);
            if (field.ReadOnly)
                return FieldError("field_read_only", "The input field is read-only: a player could not type into it.", field);
            if (!field.HasTextComponent)
                return FieldError("field_misconfigured", "The input field has no text component (or no font): it cannot show or accept typed text.", field);

            var outcome = Type(field, text ?? "", mode, end);
            return Answer(field, outcome, mode, end, secret, textTarget);
        }

        // The typing itself, shared with the tests (edit mode works the same).
        internal static TextEntryOutcome Type(IUiTextField field, string text, string mode, string end)
        {
            var o = new TextEntryOutcome { Before = field.Text, Requested = mode == "clear" ? 0 : text.Length, WasFocused = field.Focused };
            UnityAction<string> count = _ => o.ValueChanged++;
            field.AddValueChanged(count);
            try
            {
                if (mode == "replace" || mode == "clear")
                {
                    o.Cleared = field.Text.Length > 0;
                    try { field.Clear(); } catch (System.Exception ex) { o.Caught(ex); }
                }
                field.CaretToEnd();
                if (mode != "clear")
                {
                    for (int i = 0; i < text.Length; i++)
                    {
                        char c = text[i];
                        var before = field.Text;
                        bool atLimit = field.CharacterLimit > 0 && before.Length >= field.CharacterLimit;
                        try { field.Type(c); } catch (System.Exception ex) { o.Caught(ex); }
                        var after = field.Text;
                        if (after.Length > before.Length)
                        {
                            o.Accepted++;
                            if (after[after.Length - 1] != c) o.Transformed++;
                        }
                        else if (after == before) o.Rejected.Add(new KeyValuePair<int, string>(i, atLimit ? "limit" : "validation"));
                        else o.Accepted++;
                    }
                }
                // ProcessEvent changes the text only: the field redraws its label from its own
                // input update, which a typed character never reaches. Refresh it as that would.
                try { field.RefreshLabel(); } catch (System.Exception ex) { o.Caught(ex); }
                if (end == "submit")
                {
                    try { field.Submit(); o.Submitted = true; } catch (System.Exception ex) { o.Caught(ex); o.Submitted = true; }
                }
                if (end == "submit" || end == "end_edit")
                {
                    try { field.EndEdit(); o.EndEdit = true; } catch (System.Exception ex) { o.Caught(ex); o.EndEdit = true; }
                }
            }
            finally
            {
                field.RemoveValueChanged(count);
                // Ending the edit of a focused field resets its caret and redraws from there; draw the final text.
                try { field.RefreshLabel(); } catch (System.Exception ex) { o.Caught(ex); }
            }
            o.After = field.Text;
            return o;
        }

        internal static string Answer(IUiTextField field, TextEntryOutcome o, string mode, string end, bool secret, string textTarget)
        {
            bool masked = field.Masked || secret;
            var sb = new StringBuilder(512);
            sb.Append("{\"status\":\"ok\",\"action\":").Append(InputSimulationJson.Esc(mode));
            AppendField(sb, field);
            if (textTarget != null) sb.Append(",\"textTarget\":").Append(textTarget);
            sb.Append(",\"wasFocused\":").Append(o.WasFocused ? "true" : "false");
            sb.Append(",\"masked\":").Append(masked ? "true" : "false");
            if (masked)
            {
                sb.Append(",\"lengthBefore\":").Append(o.Before.Length);
                sb.Append(",\"lengthAfter\":").Append(o.After.Length);
            }
            else
            {
                sb.Append(",\"textBefore\":").Append(InputSimulationJson.Esc(o.Before));
                sb.Append(",\"textAfter\":").Append(InputSimulationJson.Esc(o.After));
            }
            sb.Append(",\"cleared\":").Append(o.Cleared ? "true" : "false");
            sb.Append(",\"typed\":{\"requested\":").Append(o.Requested);
            sb.Append(",\"accepted\":").Append(o.Accepted);
            sb.Append(",\"transformed\":").Append(o.Transformed);
            sb.Append(",\"rejected\":").Append(o.Rejected.Count);
            if (o.Rejected.Count > 0)
            {
                sb.Append(",\"rejectedAt\":[");
                for (int i = 0; i < o.Rejected.Count && i < 50; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"index\":").Append(o.Rejected[i].Key);
                    sb.Append(",\"reason\":").Append(InputSimulationJson.Esc(o.Rejected[i].Value)).Append('}');
                }
                sb.Append(']');
            }
            sb.Append('}');
            sb.Append(",\"events\":{\"value_changed\":").Append(o.ValueChanged);
            sb.Append(",\"end_edit\":").Append(o.EndEdit ? "true" : "false");
            sb.Append(",\"submit\":").Append(o.Submitted ? "true" : "false").Append('}');
            sb.Append(",\"end\":").Append(InputSimulationJson.Esc(end));
            if (o.ExceptionType != null)
            {
                sb.Append(",\"exception\":{\"type\":").Append(InputSimulationJson.Esc(o.ExceptionType));
                sb.Append(",\"message\":").Append(InputSimulationJson.Esc(o.ExceptionMessage)).Append('}');
            }
            var notes = new List<string>();
            if (o.Rejected.Count > 0)
                notes.Add($"{o.Rejected.Count} of {o.Requested} character(s) were refused by the field ({(o.Rejected.Exists(r => r.Value == "limit") ? "character limit" : "its validation / content type")}), as they would be for a player.");
            if (o.Transformed > 0)
                notes.Add($"{o.Transformed} character(s) went in changed by the field's validation (for example case or a mask).");
            if (end == "none") notes.Add("The edit was left open: no onEndEdit / onSubmit was sent.");
            if (notes.Count > 0)
            {
                sb.Append(",\"notes\":[");
                for (int i = 0; i < notes.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(InputSimulationJson.Esc(notes[i]));
                }
                sb.Append(']');
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendField(StringBuilder sb, IUiTextField field)
        {
            sb.Append(",\"field\":{\"id\":").Append(InstanceId.ToJson(field.Go));
            sb.Append(",\"path\":").Append(InputSimulationJson.Esc(PointerTargets.BuildPath(field.Go)));
            sb.Append(",\"kind\":").Append(InputSimulationJson.Esc(field.Kind));
            sb.Append(",\"contentType\":").Append(InputSimulationJson.Esc(field.ContentType));
            sb.Append(",\"lineType\":").Append(InputSimulationJson.Esc(field.LineType));
            sb.Append(",\"characterLimit\":").Append(field.CharacterLimit);
            sb.Append(",\"readOnly\":").Append(field.ReadOnly ? "true" : "false");
            sb.Append(",\"interactable\":").Append(field.Selectable.IsInteractable() ? "true" : "false");
            sb.Append(",\"focused\":").Append(field.Focused ? "true" : "false");
            sb.Append('}');
        }

        private static string FieldError(string code, string message, IUiTextField field)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":").Append(InputSimulationJson.Esc(code));
            sb.Append(",\"message\":").Append(InputSimulationJson.Esc(message)).Append('}');
            AppendField(sb, field);
            sb.Append('}');
            return sb.ToString();
        }
    }
}
