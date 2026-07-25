// Central scene-object query helper — isolates the Unity-version dance for
// enumerating live scene objects behind one API. Every screenshot, scene-
// hierarchy walk, and extension domain that needs "all active roots" or "all
// objects of type T" goes through here so the #if version-gating lives in one
// audited place, mirroring InstanceId.
//
// Unity 2023.1 introduced Object.FindObjectsByType<T>(FindObjectsInactive)
// and marked the legacy Object.FindObjectsOfType<T>() [Obsolete]. The package
// manifest declares a 2022.3 LTS floor, so the legacy API is the only one
// available there. On UNITY_2023_1_OR_NEWER the helper uses the new API; on
// older Unity it falls back to FindObjectsOfType<T>(), which returns active
// objects only — so FindObjectsInactive.Include degrades to active-only on
// pre-2023.1 (graceful, never a compile failure), and a one-time warning
// makes the semantic loss observable.
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
#if UNITY_2023_1_OR_NEWER
            var mode = inactive == Inactive.Include
                ? FindObjectsInactive.Include
                : FindObjectsInactive.Exclude;
            return Object.FindObjectsByType<T>(mode);
#else
            // Pre-2023.1 has no FindObjectsInactive enum and FindObjectsOfType<T>()
            // is the only option — it returns active objects only. Include
            // degrades to active-only (graceful, never a crash). Warn once per
            // call so the semantic loss is observable in the Editor log rather
            // than silently changing the result set.
            if (inactive == Inactive.Include)
            {
                Debug.LogWarning(
                    "[unity-open-mcp] SceneQuery.Inactive.Include is not available " +
                    "on Unity < 2023.1; degrading to active-only (Exclude).");
            }
            return Object.FindObjectsOfType<T>();
#endif
        }

        /// <summary>
        /// The most common call shape in this codebase: every active root
        /// Transform across open scenes. Centralized so every screenshot and
        /// extension-domain hierarchy walk emits the same set. Equivalent to
        /// <c>FindObjectsByType&lt;Transform&gt;(FindObjectsInactive.Exclude)</c>.
        /// </summary>
        public static Transform[] FindRootTransforms()
            => FindAllOfType<Transform>(Inactive.Exclude);
    }
}
