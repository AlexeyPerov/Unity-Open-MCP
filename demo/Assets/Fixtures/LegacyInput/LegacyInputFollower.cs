using System.Collections.Generic;
using UnityEngine;

namespace UnityOpenMcpDemo.Fixtures
{
    // Polled legacy-input "follow the finger" fixture. Reads UnityEngine.Input in
    // Update — the surface a legacy Input Manager game polls (touchCount, GetTouch,
    // GetMouseButton, mousePosition) — and moves to the world point under the
    // first touch, or under the cursor while the left button is held. Each frame
    // with input appends a sample to a bounded list so a test can read where the
    // object was at a given frame.
    public sealed class LegacyInputFollower : MonoBehaviour
    {
        [System.Serializable]
        public struct Sample
        {
            public int frame;
            public float time;
            public Vector2 screen;
            public Vector3 world;
            public string source;
            public string phase;
            public int fingerId;
        }

        public Camera viewCamera;
        public float depth = 10f;
        public int maxSamples = 2000;
        // A steady frame rate keeps multi-second gestures inside the sample window.
        public int targetFrameRate = 60;

        public int touchCount;
        public bool mouseHeld;
        public Vector2 mousePosition;
        public Vector2 lastInputScreen;
        public string lastPhase = "";
        public int lastFingerId = -1;
        public int framesWithInput;
        public int touchBegan;
        public int touchEnded;
        public int mouseDown;
        public int mouseUp;
        public List<Sample> samples = new List<Sample>();

        private int previousFrameRate;

        private void Awake()
        {
            previousFrameRate = Application.targetFrameRate;
            if (targetFrameRate > 0) Application.targetFrameRate = targetFrameRate;
        }

        private void OnDestroy()
        {
            if (targetFrameRate > 0) Application.targetFrameRate = previousFrameRate;
        }

        private void Update()
        {
            touchCount = Input.touchCount;
            mouseHeld = Input.GetMouseButton(0);
            mousePosition = Input.mousePosition;
            if (Input.GetMouseButtonDown(0)) mouseDown++;
            if (Input.GetMouseButtonUp(0)) mouseUp++;

            if (touchCount > 0)
            {
                var touch = Input.GetTouch(0);
                if (touch.phase == TouchPhase.Began) touchBegan++;
                if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled) touchEnded++;
                Follow(touch.position, "touch", touch.phase.ToString(), touch.fingerId);
            }
            else if (mouseHeld)
            {
                Follow(mousePosition, "mouse", "Held", -1);
            }
        }

        private void Follow(Vector2 screen, string source, string phase, int fingerId)
        {
            framesWithInput++;
            lastInputScreen = screen;
            lastPhase = phase;
            lastFingerId = fingerId;
            var cam = viewCamera != null ? viewCamera : Camera.main;
            var world = cam != null
                ? cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, depth))
                : new Vector3(screen.x, screen.y, 0f);
            transform.position = world;
            if (samples.Count >= maxSamples) samples.RemoveAt(0);
            samples.Add(new Sample
            {
                frame = Time.frameCount,
                time = Time.time,
                screen = screen,
                world = world,
                source = source,
                phase = phase,
                fingerId = fingerId,
            });
        }

        public void ResetSamples()
        {
            samples.Clear();
            framesWithInput = touchBegan = touchEnded = mouseDown = mouseUp = 0;
            lastPhase = "";
            lastFingerId = -1;
        }
    }
}
