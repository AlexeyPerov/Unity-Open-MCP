using UnityEngine;

namespace UnityOpenMcpDemo.Fixtures
{
    // Fits its RectTransform to Screen.safeArea (anchors in normalised screen
    // space) whenever the screen or safe area changes in play mode, and records
    // the Screen values it last saw — so a check can compare them with what
    // game_view reports (Screen.width/height read from Editor code describe an
    // Editor panel, not the game). Play mode only: fitting in edit mode would
    // dirty the scene.
    [RequireComponent(typeof(RectTransform))]
    public sealed class SafeAreaFitter : MonoBehaviour
    {
        public int screenWidth;
        public int screenHeight;
        public Rect safeArea;
        public int fittedAtFrame;
        public int fits;

        private void OnEnable() => Fit();
        private void Update() => Fit();

        private void Fit()
        {
            int w = Screen.width, h = Screen.height;
            var area = Screen.safeArea;
            if (w <= 0 || h <= 0) return;
            if (w == screenWidth && h == screenHeight && area == safeArea) return;
            screenWidth = w;
            screenHeight = h;
            safeArea = area;
            fittedAtFrame = Time.frameCount;
            fits++;
            var rect = (RectTransform)transform;
            rect.anchorMin = new Vector2(area.xMin / w, area.yMin / h);
            rect.anchorMax = new Vector2(area.xMax / w, area.yMax / h);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
