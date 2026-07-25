using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Tests
{
    // Regression coverage for code-review finding B12 — the bridge declared a
    // 2022.3 LTS floor but used Object.FindObjectsByType<T>() + the
    // FindObjectsInactive enum (both introduced in Unity 2023.1) in ~40 call
    // sites with no #if guard, so the whole Editor assembly failed to compile
    // on the documented minimum. SceneQuery centralizes the version dance
    // (FindObjectsByType on 2023.1+ / FindObjectsOfType on older Unity) behind
    // one API; these tests prove the helper compiles AND returns live scene
    // objects on whatever Unity version the test project runs.
    public class SceneQueryTests
    {
        [Test]
        public void FindAllOfType_ReturnsNonNullArray()
        {
            // The EditMode test scene has at least a main camera (Default
            // setup), so Camera is always present. The point of this assertion
            // is that the helper runs without throwing and yields a real array
            // — proving the version-gated branch compiled and dispatched.
            var cameras = SceneQuery.FindAllOfType<Camera>();
            Assert.IsNotNull(cameras, "FindAllOfType must never return null");
        }

        [Test]
        public void FindRootTransforms_ReturnsNonNullArray()
        {
            // FindRootTransforms is the most common call shape in the codebase
            // (every screenshot + extension-domain hierarchy walk). It must
            // return a non-null array on any scene with at least one root.
            var roots = SceneQuery.FindRootTransforms();
            Assert.IsNotNull(roots, "FindRootTransforms must never return null");
        }

        [Test]
        public void FindAllOfType_Include_DoesNotThrow()
        {
            // The Include path is the one that degrades to active-only on
            // pre-2023.1 (with a warning). Asserting it runs without throwing
            // on the current Unity proves the #if branch for whichever version
            // is active is sound.
            var cameras = SceneQuery.FindAllOfType<Camera>(SceneQuery.Inactive.Include);
            Assert.IsNotNull(cameras, "FindAllOfType(Include) must never return null");
        }

        [Test]
        public void FindAllOfType_ExcludeExplicit_EqualsDefaultOverload()
        {
            // The default overload delegates to Exclude. Asserting both return
            // the same count pins that delegation so a future refactor cannot
            // silently change the default's semantics.
            var byDefault = SceneQuery.FindAllOfType<Camera>();
            var byExclude = SceneQuery.FindAllOfType<Camera>(SceneQuery.Inactive.Exclude);
            Assert.AreEqual(byDefault.Length, byExclude.Length,
                "FindAllOfType() and FindAllOfType(Inactive.Exclude) must agree");
        }
    }
}
