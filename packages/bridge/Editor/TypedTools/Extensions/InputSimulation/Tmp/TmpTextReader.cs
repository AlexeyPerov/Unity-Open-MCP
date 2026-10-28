// TextMeshPro text for ui_snapshot and label targeting.
//
// Optional sub-assembly: compiles when TextMeshPro is present — the standalone
// com.unity.textmeshpro 3.x package (Unity 2022.3) or uGUI 2.x, which ships
// TextMeshPro inside it (Unity 6). Both versionDefines set the same
// UNITY_OPEN_MCP_EXT_INPUTSIM_TMP symbol; without either the assembly is skipped
// and the snapshot reads UnityEngine.UI.Text only (textSources: ["ugui"]).
//
// The reader only touches components that already exist on a node, so a project
// that never uses TextMeshPro never reaches TMP_Settings (and never triggers the
// essential-resources importer). Overflow: preferred height above the text area
// (wrapping at the area width), preferred width above it for text that does not
// wrap, or TMP's own isTextOverflowing (auto-size at its minimum). Preferred
// values are computed on demand; no mesh is rebuilt.
#if UNITY_OPEN_MCP_EXT_INPUTSIM_TMP
using TMPro;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    [InitializeOnLoad]
    internal static class TmpTextReaderRegistration
    {
        static TmpTextReaderRegistration()
        {
            UiTextReaders.Register(new TmpTextReader());
        }
    }

    internal sealed class TmpTextReader : IUiTextReader
    {
        public string Source => "tmp";

        public bool TryRead(GameObject go, out UiTextInfo info)
        {
            info = default;
            var text = go.GetComponent<TMP_Text>();
            if (text == null || !text.enabled) return false;

            info.Raw = text.text;
            info.RichText = text.richText;
            info.Alpha = text.color.a;
            info.Source = Source;

            var rect = text.rectTransform.rect;
            var margin = text.margin;
            float areaW = rect.width - margin.x - margin.z;
            float areaH = rect.height - margin.y - margin.w;
            if (string.IsNullOrEmpty(info.Raw))
            {
                info.Content = Vector2.zero;
                return true;
            }
            // Preferred values are computed on demand (fresh in edit mode too);
            // isTextOverflowing covers auto-size text that cannot shrink enough.
            // preferredWidth is the single-line width, so it overflows only
            // when the text does not wrap.
            float needH = text.preferredHeight;
            float needW = text.preferredWidth;
            bool vertical = needH > areaH + UiScreenModel.SizeEpsilon;
            bool horizontal = !Wraps(text) && needW > areaW + UiScreenModel.SizeEpsilon;
            info.Overflowing = text.isTextOverflowing || (!text.enableAutoSizing && (vertical || horizontal));
            info.Content = new Vector2(Wraps(text) ? Mathf.Min(needW, areaW) : needW, needH);
            return true;
        }

        private static bool Wraps(TMP_Text text)
        {
#if UNITY_2023_2_OR_NEWER
            // uGUI 2.x TextMeshPro: wrapping is an enum.
            return text.textWrappingMode == TextWrappingModes.Normal
                || text.textWrappingMode == TextWrappingModes.PreserveWhitespace;
#else
            return text.enableWordWrapping;
#endif
        }
    }
}
#endif
