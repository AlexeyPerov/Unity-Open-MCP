using System;
using UnityEditor;

namespace UnityOpenMcpVerify.Rules.MissingReferences
{
    // feedback-04-08-opus §5 — classify an empty_local_ref site by the OWNER of
    // the field it sits on, so a real empty ref on a USER-SCRIPT field stays a
    // Warning while empty-by-default BUILT-IN Unity/package component fields are
    // demoted to Info. The report's 729-issue wall was ~98 % built-in noise
    // (m_SelectOn*, sprite-swap sprites, TMP material/style fields) that buried a
    // genuine shipping NullReferenceException on a user-script field
    // (ButtonPromotionPotions.PromotionTimer).
    //
    // Two signals, both already captured by the scanner:
    //   - OwnerScriptGuid: when the owning component is a MonoBehaviour whose
    //     m_Script guid resolves UNDER Assets/, the field is a user-script field
    //     → Warning (a real, possibly-shipping null).
    //   - otherwise (no script guid, or the guid resolves to a package / built-in
    //     assembly): the field is a built-in / package component field. If it is
    //     on the KnownOptional list, demote to Info; if not, keep Warning (an
    //     unexpected empty on a built-in field can still be a real miswire).
    internal static class EmptyRefClassifier
    {
        // Built-in Unity / package component fields that are EMPTY BY DEFAULT on
        // virtually every authored instance and carry no information as an empty
        // ref. Drawn from the report's property histogram (m_SelectOn* Button
        // navigation, sprite-swap sprites, TMP material/style/linked fields,
        // scrollbar refs, common renderer/light/probe anchors). An empty ref on
        // any of these is demoted to Info unless the owning component is a user
        // script (user-script fields are NEVER demoted regardless of name).
        //
        // Sourced from UnityEngine.UI / TextMeshPro / UnityEngine core. Kept as a
        // prefix set so a name like "m_SelectOnUp" / "m_SelectOnDown" all match
        // the single "m_SelectOn" entry without enumerating every direction.
        private static readonly string[] KnownOptionalPrefixes =
        {
            // UnityEngine.UI Button / Selectable navigation targets.
            "m_SelectOn",
            // UnityEngine.UI Button / Image sprite-state swaps.
            "m_HighlightedSprite",
            "m_PressedSprite",
            "m_SelectedSprite",
            "m_DisabledSprite",
            // UnityEngine.UI Scrollbar / ScrollView hookups.
            "m_HorizontalScrollbar",
            "m_VerticalScrollbar",
            // TextMeshPro material / style / linked fields (empty by default).
            "m_fontMaterial",
            "m_fontColorGradientPreset",
            "m_spriteAsset",
            "m_StyleSheet",
            "m_linkedTextComponent",
            "parentLinkedComponent",
            "m_baseMaterial",
            // Common renderer / light / probe anchors that are routinely empty.
            "m_Material",      // Renderer.sharedMaterial slot — empty is the default for UI.
            "m_ProbeAnchor",
            "m_LightmapParameters",
            "m_StaticBatchRoot",
        };

        // The full property name for a few fields that are known-optional only in
        // their exact form (no prefix overlap risk). Currently empty — the prefix
        // set above covers the reported histogram. Kept for future exact matches.
        private static readonly string[] KnownOptionalExact = Array.Empty<string>();

        /// <summary>
        /// Classify an empty_local_ref site's severity. Returns Warning for
        /// user-script fields and unexpected built-in empties; Info for the
        /// known-optional built-in/package fields that are empty-by-default.</summary>
        public static VerifySeverity Classify(EmptyLocalFileIDRegistry empty)
        {
            // A user-script field is NEVER demoted: the owning MonoBehaviour's
            // m_Script guid resolves under Assets/ (a first-party script), so an
            // empty ref here is a real, possibly-shipping null — exactly the
            // class of bug the report found (PromotionTimer empty while the
            // notificator was set → live NRE in shipping code).
            if (!string.IsNullOrEmpty(empty.OwnerScriptGuid) && IsUserScript(empty.OwnerScriptGuid))
            {
                return VerifySeverity.Warning;
            }

            // Built-in / package component field. Demote the known-optional
            // empty-by-default fields to Info; keep unexpected empties as
            // Warning (an empty m_Father at the root is expected, but an empty
            // m_Mesh on a MeshFilter is a real miswire).
            var prop = empty.Property ?? "";
            if (IsKnownOptional(prop))
            {
                return VerifySeverity.Info;
            }

            return VerifySeverity.Warning;
        }

        private static bool IsUserScript(string scriptGuid)
        {
            if (string.IsNullOrEmpty(scriptGuid)) return false;
            var path = AssetDatabase.GUIDToAssetPath(scriptGuid);
            // A first-party script lives under Assets/. A package/built-in
            // script resolves under Packages/ or Library/ (or empty for a
            // built-in assembly like UnityEngine.UI). Only Assets/ counts as
            // user-owned for severity purposes.
            return !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsKnownOptional(string property)
        {
            if (string.IsNullOrEmpty(property)) return false;
            foreach (var exact in KnownOptionalExact)
            {
                if (property == exact) return true;
            }
            foreach (var prefix in KnownOptionalPrefixes)
            {
                if (property.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
