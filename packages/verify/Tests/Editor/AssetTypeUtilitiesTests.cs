using NUnit.Framework;
using UnityOpenMcpVerify.Internals.AssetDatabase;

namespace UnityOpenMcpVerify.Tests
{
    // Pins the three distinct extension-set policies on AssetTypeUtilities.
    // These used to be inline extension comparisons scattered across
    // ReferenceGraph.cs and Rules/Dependencies/Scanner.cs; each call site meant
    // a slightly different set, so the helpers are intentionally separate
    // (CanContainAssetReferences / IsTextSerializedYaml / IsSceneOrPrefab) and
    // must NOT collapse into one IsYamlAsset.
    [TestFixture]
    public static class AssetTypeUtilitiesTests
    {
        // ----- CanContainAssetReferences: .asset / .prefab / .unity -----

        [Test]
        public static void CanContainAssetReferences_TrueForAssetPrefabScene()
        {
            Assert.IsTrue(AssetTypeUtilities.CanContainAssetReferences("Assets/foo.asset"));
            Assert.IsTrue(AssetTypeUtilities.CanContainAssetReferences("Assets/foo.prefab"));
            Assert.IsTrue(AssetTypeUtilities.CanContainAssetReferences("Assets/foo.unity"));
        }

        [Test]
        public static void CanContainAssetReferences_FalseForMaterialAndOthers()
        {
            // Materials / controllers / anim are text-serialized YAML but do NOT
            // embed m_AssetGUID references the way ScriptableObject .asset files do.
            Assert.IsFalse(AssetTypeUtilities.CanContainAssetReferences("Assets/foo.mat"));
            Assert.IsFalse(AssetTypeUtilities.CanContainAssetReferences("Assets/foo.controller"));
            Assert.IsFalse(AssetTypeUtilities.CanContainAssetReferences("Assets/foo.anim"));
            Assert.IsFalse(AssetTypeUtilities.CanContainAssetReferences("Assets/foo.png"));
            Assert.IsFalse(AssetTypeUtilities.CanContainAssetReferences("Assets/noext"));
        }

        // ----- IsTextSerializedYaml: prefab / unity / asset / mat / controller / anim -----

        [Test]
        public static void IsTextSerializedYaml_TrueForAllSixTextYamlExtensions()
        {
            Assert.IsTrue(AssetTypeUtilities.IsTextSerializedYaml("Assets/x.prefab"));
            Assert.IsTrue(AssetTypeUtilities.IsTextSerializedYaml("Assets/x.unity"));
            Assert.IsTrue(AssetTypeUtilities.IsTextSerializedYaml("Assets/x.asset"));
            Assert.IsTrue(AssetTypeUtilities.IsTextSerializedYaml("Assets/x.mat"));
            Assert.IsTrue(AssetTypeUtilities.IsTextSerializedYaml("Assets/x.controller"));
            Assert.IsTrue(AssetTypeUtilities.IsTextSerializedYaml("Assets/x.anim"));
        }

        [Test]
        public static void IsTextSerializedYaml_FalseForBinaryOrUnknown()
        {
            Assert.IsFalse(AssetTypeUtilities.IsTextSerializedYaml("Assets/x.png"));
            Assert.IsFalse(AssetTypeUtilities.IsTextSerializedYaml("Assets/x.fbx"));
            Assert.IsFalse(AssetTypeUtilities.IsTextSerializedYaml("Assets/x.cs"));
            Assert.IsFalse(AssetTypeUtilities.IsTextSerializedYaml("Assets/x"));
        }

        // ----- IsSceneOrPrefab: .prefab / .unity (case-insensitive) -----

        [Test]
        public static void IsSceneOrPrefab_TrueForPrefabAndScene_RegardlessOfCasing()
        {
            Assert.IsTrue(AssetTypeUtilities.IsSceneOrPrefab("Assets/x.prefab"));
            Assert.IsTrue(AssetTypeUtilities.IsSceneOrPrefab("Assets/x.PREFAB"));
            Assert.IsTrue(AssetTypeUtilities.IsSceneOrPrefab("Assets/x.unity"));
            Assert.IsTrue(AssetTypeUtilities.IsSceneOrPrefab("Assets/x.Unity"));
        }

        [Test]
        public static void IsSceneOrPrefab_FalseForAssetAndMaterial()
        {
            // Broader than scene/prefab: .asset and .mat host data, not a hierarchy.
            Assert.IsFalse(AssetTypeUtilities.IsSceneOrPrefab("Assets/x.asset"));
            Assert.IsFalse(AssetTypeUtilities.IsSceneOrPrefab("Assets/x.mat"));
            Assert.IsFalse(AssetTypeUtilities.IsSceneOrPrefab("Assets/x.anim"));
        }

        // ----- the three sets are distinct by design -----

        [Test]
        public static void TheThreePoliciesAreDistinct()
        {
            // .asset: can-contain-refs yes, text-yaml yes, scene/prefab no.
            var asset = "Assets/x.asset";
            Assert.IsTrue(AssetTypeUtilities.CanContainAssetReferences(asset));
            Assert.IsTrue(AssetTypeUtilities.IsTextSerializedYaml(asset));
            Assert.IsFalse(AssetTypeUtilities.IsSceneOrPrefab(asset));

            // .mat: can-contain-refs no, text-yaml yes, scene/prefab no.
            var mat = "Assets/x.mat";
            Assert.IsFalse(AssetTypeUtilities.CanContainAssetReferences(mat));
            Assert.IsTrue(AssetTypeUtilities.IsTextSerializedYaml(mat));
            Assert.IsFalse(AssetTypeUtilities.IsSceneOrPrefab(mat));

            // .prefab: all three yes.
            var prefab = "Assets/x.prefab";
            Assert.IsTrue(AssetTypeUtilities.CanContainAssetReferences(prefab));
            Assert.IsTrue(AssetTypeUtilities.IsTextSerializedYaml(prefab));
            Assert.IsTrue(AssetTypeUtilities.IsSceneOrPrefab(prefab));
        }
    }
}
