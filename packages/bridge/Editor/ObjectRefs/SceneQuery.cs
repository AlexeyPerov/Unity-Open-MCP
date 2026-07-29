// Central scene-object query helper — isolates the Unity-version dance for
// enumerating live scene objects behind one API. Every screenshot, scene-
// hierarchy walk, and extension domain that needs "all active roots" or "all
// objects of type T" goes through here so the #if version-gating lives in one
// audited place, mirroring InstanceId.
//
// Unity 2023.1 introduced Object.FindObjectsByType<T>(FindObjectsInactive,
// FindObjectsSortMode) and marked the legacy Object.FindObjectsOfType<T>()
// [Obsolete]. There is NO single-arg FindObjectsByType<T>(FindObjectsInactive)
// overload on 2023.1–6000.4 — calling that form fails CS1503 (FindObjectsInactive
// cannot convert to FindObjectsSortMode). Unity 6000.5 obsolete'd
// FindObjectsSortMode and added no-sort overloads including
// FindObjectsByType<T>(FindObjectsInactive).
//
// The package manifest declares a 2022.3 LTS floor, so the legacy API is the
// only one available there. Branching:
//   UNITY_6000_5_OR_NEWER  → FindObjectsByType<T>(FindObjectsInactive)
//   UNITY_2023_1_OR_NEWER  → FindObjectsByType<T>(…, FindObjectsSortMode.None)
//   else                   → FindObjectsOfType<T>() (active-only; Include
//                            degrades gracefully with a one-time warning)
//
// Resources.FindObjectsOfTypeAll (used by the toolbar / window enumerators)
// is NOT version-gated — it exists in 2022.3 — so it stays at its call sites.
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.ObjectRefs
{
    public static class SceneQuery
    {
        /// <summary>
        /// Whether to include inactive objects in the result. Mirrors Unity
        /// 2023.1's <c>FindObjectsInactive</c> enum as a version-neutral shape
        /// the helper can switch on without leaking the 2023.1 type into
        /// pre-2023.1 call sites.
        /// </summary>
        public enum Inactive
        {
            /// <summary>Return only active objects. Always available.</summary>
            Exclude,
            /// <summary>
            /// Also return inactive objects. On Unity &lt; 2023.1 this degrades
            /// to <see cref="Exclude"/> (active-only) with a one-time warning,
            /// because the legacy <c>FindObjectsOfType</c> has no inactive mode.
            /// </summary>
            Include,
        }

        // B-N22 — the pre-2023.1 Inactive.Include degradation warning used to
        // fire on EVERY call, so on the declared 2022.3 floor the console
        // spammed from audio_listener_get, EnsureEventSystem and the
        // Cinemachine camera list. Latch it to a process-lifetime flag so the
        // semantic loss is logged exactly once per Editor session.
        private static bool _warnedIncludeUnavailable;

        /// <summary>
        /// Find every live object of type <typeparamref name="T"/> in open
        /// scenes, active objects only. Equivalent to the codebase's most
        /// common call shape (<c>FindObjectsByType&lt;T&gt;(FindObjectsInactive.Exclude)</c>).
        /// </summary>
        public static T[] FindAllOfType<T>() where T : Object
            => FindAllOfType<T>(Inactive.Exclude);

        /// <summary>
        /// Find every live object of type <typeparamref name="T"/> in open
        /// scenes, optionally including inactive ones. See <see cref="Inactive"/>
        /// for the pre-2023.1 degradation behaviour.
        /// </summary>
        public static T[] FindAllOfType<T>(Inactive inactive) where T : Object
        {
#if UNITY_6000_5_OR_NEWER
            // 6000.5+ — FindObjectsSortMode overloads are obsolete (CS0619);
            // use the no-sort FindObjectsInactive-only overload.
            var mode = inactive == Inactive.Include
                ? FindObjectsInactive.Include
                : FindObjectsInactive.Exclude;
            return Object.FindObjectsByType<T>(mode);
#elif UNITY_2023_1_OR_NEWER
            // 2023.1–6000.4 — must pass FindObjectsSortMode; there is no
            // single-arg FindObjectsInactive overload (CS1503 on 6000.0).
            var mode = inactive == Inactive.Include
                ? FindObjectsInactive.Include
                : FindObjectsInactive.Exclude;
            return Object.FindObjectsByType<T>(mode, FindObjectsSortMode.None);
#else
            // Pre-2023.1 has no FindObjectsInactive enum and FindObjectsOfType<T>()
            // is the only option — it returns active objects only. Include
            // degrades to active-only (graceful, never a crash). B-N22 — warn
            // ONCE per process (not per call) so the semantic loss is
            // observable in the Editor log without spamming it from every
            // Inactive.Include caller.
            if (inactive == Inactive.Include && !_warnedIncludeUnavailable)
            {
                _warnedIncludeUnavailable = true;
                Debug.LogWarning(
                    "[unity-open-mcp] SceneQuery.Inactive.Include is not available " +
                    "on Unity < 2023.1; degrading to active-only (Exclude). This " +
                    "warning is logged once per Editor session.");
            }
            return Object.FindObjectsOfType<T>();
#endif
        }

        /// <summary>
        /// The most common call shape in this codebase: every ACTIVE Transform
        /// across open scenes (NOT only root transforms — every Transform in
        /// the hierarchy, including children). Centralized so every screenshot
        /// and extension-domain hierarchy walk emits the same set. Equivalent
        /// to <c>FindObjectsByType&lt;Transform&gt;(FindObjectsInactive.Exclude)</c>.
        ///
        /// <para><b>B-N22 — naming honesty.</b> The previous name
        /// <c>FindRootTransforms</c> and its XML doc claimed "every active
        /// ROOT Transform", but the implementation returns every active
        /// Transform (children included), because <c>Transform</c> is queried
        /// as a type and every child is one. Behaviour matched the pre-fix
        /// call sites (they all match on the first path segment by name), but
        /// the name invited a real bug the first time someone trusted
        /// "roots". The name now says what it does; callers that genuinely
        /// need only root Transforms should filter on
        /// <c>t.parent == null</c> or use the scene's
        /// <c>GetRootGameObjects()</c>.</para>
        /// </summary>
        public static Transform[] FindActiveTransforms()
            => FindAllOfType<Transform>(Inactive.Exclude);
    }
}
