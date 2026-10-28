using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UnityOpenMcpDemo.Fixtures
{
    // Timed content for playtest_wait / capture_series, all on the game clock
    // (Time.deltaTime, so time_scale speeds it up):
    //   t = popupOpenAt (1 s): the popup tweens its scale 0 → 1 linearly over
    //                          popupSeconds (300 ms) and logs when it opens;
    //   t = enemyLifetime (3 s): the enemy is destroyed (logged);
    //   t = timerSeconds (5 s):  the timer label appears and the timer logs.
    // "Show Popup" replays the tween, "Throw" throws an exception (fail_on).
    // `glow` breathes (alpha 0.2..1, 2 s period) for the luminance metric.
    // Frames and times are recorded so a wait / capture can be checked
    // against what the game did.
    public sealed class WaitFixtureDirector : MonoBehaviour
    {
        public float timerSeconds = 5f;
        public float popupOpenAt = 1f;
        public float popupSeconds = 0.3f;
        public float enemyLifetime = 3f;
        public GameObject timerLabel;
        public RectTransform popup;
        public GameObject enemy;
        public Graphic glow;

        public float elapsed;
        public int timerFrame = -1;
        public float timerTime = -1f;
        public int enemyDestroyedFrame = -1;
        public int popupOpenFrame = -1;
        public float popupOpenTime = -1f;
        public int popupDoneFrame = -1;
        public int popupOpens;
        public readonly List<int> popupFrames = new List<int>();
        public readonly List<float> popupScales = new List<float>();

        private bool tweening;
        private float tweenElapsed;

        private void Start()
        {
            if (timerLabel != null) timerLabel.SetActive(false);
            if (popup != null) popup.localScale = Vector3.zero;
        }

        private void Update()
        {
            elapsed += Time.deltaTime;

            if (popupOpens == 0 && elapsed >= popupOpenAt) OpenPopup();
            if (tweening)
            {
                tweenElapsed += Time.deltaTime;
                float s = Mathf.Clamp01(tweenElapsed / popupSeconds);
                popup.localScale = new Vector3(s, s, 1f);
                popupFrames.Add(Time.frameCount);
                popupScales.Add(s);
                if (s >= 1f)
                {
                    tweening = false;
                    popupDoneFrame = Time.frameCount;
                    Debug.Log($"[WaitFixture] Popup opened at frame {Time.frameCount}");
                }
            }

            if (enemy != null && enemyDestroyedFrame < 0 && elapsed >= enemyLifetime)
            {
                enemyDestroyedFrame = Time.frameCount;
                Debug.Log($"[WaitFixture] Enemy destroyed at frame {Time.frameCount}");
                Destroy(enemy);
            }

            if (timerFrame < 0 && elapsed >= timerSeconds)
            {
                timerFrame = Time.frameCount;
                timerTime = Time.time;
                if (timerLabel != null) timerLabel.SetActive(true);
                Debug.Log($"[WaitFixture] Timer done at frame {Time.frameCount} (t={Time.time:0.000})");
            }

            if (glow != null)
            {
                var c = glow.color;
                c.a = 0.6f + 0.4f * Mathf.Sin(Time.time * Mathf.PI);
                glow.color = c;
            }
        }

        // Starts (or restarts) the 300 ms popup tween at scale 0 this frame.
        public void OpenPopup()
        {
            if (popup == null) return;
            popupOpens++;
            popupOpenFrame = Time.frameCount;
            popupOpenTime = Time.time;
            popupDoneFrame = -1;
            popupFrames.Clear();
            popupScales.Clear();
            popup.localScale = Vector3.zero;
            tweenElapsed = 0f;
            tweening = true;
            Debug.Log($"[WaitFixture] Popup opening at frame {Time.frameCount}");
        }

        // A deliberate game exception (bound to the "Throw" button).
        public void ThrowNow()
        {
            throw new System.InvalidOperationException("[WaitFixture] Deliberate exception for fail_on");
        }
    }
}
