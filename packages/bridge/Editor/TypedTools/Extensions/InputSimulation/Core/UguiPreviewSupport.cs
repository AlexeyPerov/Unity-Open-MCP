// Registers the uGUI half of unity_senses_screenshot's prefab view
// (PrefabView.Ugui): CanvasScaler read / write and the layout rebuild. The
// bridge root assembly cannot reference uGUI, so — like the playtest `ui`
// condition probe — the glue lives in this uGUI sub-assembly.
using UnityEngine;
using UnityEngine.UI;
using UnityOpenMcpBridge.Screenshot;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    [UnityEditor.InitializeOnLoad]
    internal static class UguiPreviewSupportRegistration
    {
        static UguiPreviewSupportRegistration()
        {
            PrefabView.Ugui = new UguiPreviewSupport();
        }
    }

    internal sealed class UguiPreviewSupport : IUguiPreviewSupport
    {
        public CanvasScalerSettings ReadScaler(Canvas canvas)
        {
            var scaler = canvas != null ? canvas.GetComponent<CanvasScaler>() : null;
            if (scaler == null) return null;
            return new CanvasScalerSettings
            {
                UiScaleMode = ScaleModeName(scaler.uiScaleMode),
                ScaleFactor = scaler.scaleFactor,
                ReferenceResolution = scaler.referenceResolution,
                ScreenMatchMode = MatchModeName(scaler.screenMatchMode),
                MatchWidthOrHeight = scaler.matchWidthOrHeight,
                ReferencePixelsPerUnit = scaler.referencePixelsPerUnit,
                PhysicalUnit = UnitName(scaler.physicalUnit),
                FallbackScreenDpi = scaler.fallbackScreenDPI,
                DefaultSpriteDpi = scaler.defaultSpriteDPI,
            };
        }

        public void ApplyScaler(Canvas canvas, CanvasScalerSettings s)
        {
            var scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = ParseScaleMode(s.UiScaleMode);
            scaler.scaleFactor = s.ScaleFactor;
            scaler.referenceResolution = s.ReferenceResolution;
            scaler.screenMatchMode = ParseMatchMode(s.ScreenMatchMode);
            scaler.matchWidthOrHeight = s.MatchWidthOrHeight;
            scaler.referencePixelsPerUnit = s.ReferencePixelsPerUnit;
            scaler.physicalUnit = ParseUnit(s.PhysicalUnit);
            scaler.fallbackScreenDPI = s.FallbackScreenDpi;
            scaler.defaultSpriteDPI = s.DefaultSpriteDpi;
        }

        public void RebuildLayout(RectTransform root) => LayoutRebuilder.ForceRebuildLayoutImmediate(root);

        internal static string ScaleModeName(CanvasScaler.ScaleMode mode)
        {
            switch (mode)
            {
                case CanvasScaler.ScaleMode.ScaleWithScreenSize: return "scale_with_screen_size";
                case CanvasScaler.ScaleMode.ConstantPhysicalSize: return "constant_physical_size";
                default: return "constant_pixel_size";
            }
        }

        internal static CanvasScaler.ScaleMode ParseScaleMode(string name)
        {
            switch (name)
            {
                case "scale_with_screen_size": return CanvasScaler.ScaleMode.ScaleWithScreenSize;
                case "constant_physical_size": return CanvasScaler.ScaleMode.ConstantPhysicalSize;
                default: return CanvasScaler.ScaleMode.ConstantPixelSize;
            }
        }

        internal static string MatchModeName(CanvasScaler.ScreenMatchMode mode)
        {
            switch (mode)
            {
                case CanvasScaler.ScreenMatchMode.Expand: return "expand";
                case CanvasScaler.ScreenMatchMode.Shrink: return "shrink";
                default: return "match_width_or_height";
            }
        }

        internal static CanvasScaler.ScreenMatchMode ParseMatchMode(string name)
        {
            switch (name)
            {
                case "expand": return CanvasScaler.ScreenMatchMode.Expand;
                case "shrink": return CanvasScaler.ScreenMatchMode.Shrink;
                default: return CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            }
        }

        internal static string UnitName(CanvasScaler.Unit unit)
        {
            switch (unit)
            {
                case CanvasScaler.Unit.Centimeters: return "centimeters";
                case CanvasScaler.Unit.Millimeters: return "millimeters";
                case CanvasScaler.Unit.Inches: return "inches";
                case CanvasScaler.Unit.Picas: return "picas";
                default: return "points";
            }
        }

        internal static CanvasScaler.Unit ParseUnit(string name)
        {
            switch (name)
            {
                case "centimeters": return CanvasScaler.Unit.Centimeters;
                case "millimeters": return CanvasScaler.Unit.Millimeters;
                case "inches": return CanvasScaler.Unit.Inches;
                case "picas": return CanvasScaler.Unit.Picas;
                default: return CanvasScaler.Unit.Points;
            }
        }
    }
}
