using System.Collections.Generic;
using NUnit.Framework;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Rules.MissingReferences;

namespace UnityOpenMcpVerify.Tests
{
    // feedback-04-08-opus §4 follow-up — the anchor-metadata pre-pass must scan
    // each YAML object to the NEXT document header, not a fixed line budget.
    //
    // The first implementation capped the per-object scan at 12 lines, which
    // missed exactly the fields the map exists for on realistic Unity YAML:
    //   * m_Father comes AFTER m_Children — the 13th field line even with zero
    //     children on a 2022.3-era transform (m_ConstrainProportionsScale adds
    //     a line), and later still with any child entries.
    //   * m_Name on a GameObject with >= 4 components sits past the budget
    //     (the m_Component list precedes it, one line per component).
    // Either miss makes ResolveTransformPath truncate or return null, so the
    // mapper silently fell back to anchor keying — defeating the transform-path
    // keying this metadata was built to provide. These tests pin full-length
    // shapes end to end.
    [TestFixture]
    public class EmptyRefMetadataTests
    {
        // Realistic full-length Unity 2022.3 YAML: a Root GameObject with FOUR
        // components (m_Name lands on the 13th line after the header), its
        // Transform with a non-empty m_Children list BEFORE m_Father (m_Father
        // lands on the 13th line too), a Child GameObject + RectTransform, and
        // a MonoBehaviour on the Child.
        private static string[] FullLengthYaml()
        {
            return new[]
            {
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!1 &1000",
                "GameObject:",
                "  m_ObjectHideFlags: 0",
                "  m_CorrespondingSourceObject: {fileID: 0}",
                "  m_PrefabInstance: {fileID: 0}",
                "  m_PrefabAsset: {fileID: 0}",
                "  serializedVersion: 6",
                "  m_Component:",
                "  - component: {fileID: 4000}",
                "  - component: {fileID: 5001}",
                "  - component: {fileID: 5002}",
                "  - component: {fileID: 5003}",
                "  m_Layer: 0",
                "  m_Name: Root",
                "  m_TagString: Untagged",
                "  m_Icon: {fileID: 0}",
                "  m_NavMeshLayer: 0",
                "  m_StaticEditorFlags: 0",
                "  m_IsActive: 1",
                "--- !u!4 &4000",
                "Transform:",
                "  m_ObjectHideFlags: 0",
                "  m_CorrespondingSourceObject: {fileID: 0}",
                "  m_PrefabInstance: {fileID: 0}",
                "  m_PrefabAsset: {fileID: 0}",
                "  m_GameObject: {fileID: 1000}",
                "  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}",
                "  m_LocalPosition: {x: 0, y: 0, z: 0}",
                "  m_LocalScale: {x: 1, y: 1, z: 1}",
                "  m_ConstrainProportionsScale: 0",
                "  m_Children:",
                "  - {fileID: 4100}",
                "  m_Father: {fileID: 0}",
                "  m_RootOrder: 0",
                "  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}",
                "--- !u!1 &2000",
                "GameObject:",
                "  m_ObjectHideFlags: 0",
                "  m_CorrespondingSourceObject: {fileID: 0}",
                "  m_PrefabInstance: {fileID: 0}",
                "  m_PrefabAsset: {fileID: 0}",
                "  serializedVersion: 6",
                "  m_Component:",
                "  - component: {fileID: 4100}",
                "  - component: {fileID: 5100}",
                "  m_Layer: 5",
                "  m_Name: Child",
                "  m_TagString: Untagged",
                "  m_Icon: {fileID: 0}",
                "  m_NavMeshLayer: 0",
                "  m_StaticEditorFlags: 0",
                "  m_IsActive: 1",
                "--- !u!224 &4100",
                "RectTransform:",
                "  m_ObjectHideFlags: 0",
                "  m_CorrespondingSourceObject: {fileID: 0}",
                "  m_PrefabInstance: {fileID: 0}",
                "  m_PrefabAsset: {fileID: 0}",
                "  m_GameObject: {fileID: 2000}",
                "  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}",
                "  m_LocalPosition: {x: 0, y: 0, z: 0}",
                "  m_LocalScale: {x: 1, y: 1, z: 1}",
                "  m_ConstrainProportionsScale: 0",
                "  m_Children: []",
                "  m_Father: {fileID: 4000}",
                "  m_RootOrder: 0",
                "  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}",
                "  m_AnchorMin: {x: 0, y: 0}",
                "  m_AnchorMax: {x: 1, y: 1}",
                "  m_AnchoredPosition: {x: 0, y: 0}",
                "  m_SizeDelta: {x: 0, y: 0}",
                "  m_Pivot: {x: 0.5, y: 0.5}",
                "--- !u!114 &5100",
                "MonoBehaviour:",
                "  m_ObjectHideFlags: 0",
                "  m_CorrespondingSourceObject: {fileID: 0}",
                "  m_PrefabInstance: {fileID: 0}",
                "  m_PrefabAsset: {fileID: 0}",
                "  m_GameObject: {fileID: 2000}",
                "  m_Enabled: 1",
                "  m_EditorHideFlags: 0",
                "  m_Script: {fileID: 11500000, guid: abcdef0123456789abcdef0123456789, type: 3}",
                "  m_Name: ",
                "  m_EditorClassIdentifier: ",
                "  someField: {fileID: 0}",
            };
        }

        [Test]
        public void Build_FullLengthYaml_CapturesFieldsPastTheOldLineBudget()
        {
            var map = EmptyRefMetadata.Build(FullLengthYaml(), new HashSet<long>());

            // m_Name on a GameObject with 4 components — the 13th line after
            // the header, past the old 12-line budget.
            Assert.IsTrue(map.ContainsKey(1000), "Root GameObject anchor must be in the map");
            Assert.AreEqual("Root", map[1000].Name,
                "m_Name after a 4-entry m_Component list must be captured");

            // m_Father after a non-empty m_Children list — also past the old
            // budget on the child RectTransform.
            Assert.IsTrue(map.ContainsKey(4100), "child RectTransform anchor must be in the map");
            Assert.AreEqual(4000, map[4100].FatherId,
                "m_Father after m_Children must be captured");
            Assert.AreEqual(2000, map[4100].GameObjectId);

            Assert.IsTrue(map.ContainsKey(2000));
            Assert.AreEqual("Child", map[2000].Name);

            Assert.IsTrue(map.ContainsKey(5100), "MonoBehaviour anchor must be in the map");
            Assert.AreEqual("abcdef0123456789abcdef0123456789", map[5100].ScriptGuid);
            Assert.AreEqual(2000, map[5100].GameObjectId);
        }

        [Test]
        public void ResolveTransformPath_FullLengthYaml_ResolvesFullPath()
        {
            var map = EmptyRefMetadata.Build(FullLengthYaml(), new HashSet<long>());

            // The MonoBehaviour sits on the Child GameObject; the walk must
            // climb Child -> Root through the m_Father chain that only resolves
            // when m_Father/m_Name were captured past the old budget. Under the
            // 12-line budget this returned a truncated "Child" (root father
            // missed) or null (m_Name missed) and the mapper fell back to
            // anchor keying.
            var path = EmptyRefMetadata.ResolveTransformPath(5100, map);
            Assert.AreEqual("Root/Child", path);
        }

        [Test]
        public void ResolveTransformPath_RootComponent_ResolvesSingleSegment()
        {
            var map = EmptyRefMetadata.Build(FullLengthYaml(), new HashSet<long>());

            // The root Transform itself: the walk starts at 4000 whose
            // m_Father is {fileID: 0}, so the path is just the root name.
            var path = EmptyRefMetadata.ResolveTransformPath(4000, map);
            Assert.AreEqual("Root", path);
        }
    }

    // review 2026-08-13 — `m_Material` (singular) is empty-by-default on UI
    // Images and demotes to Info, but `m_Materials` (Renderer's material ARRAY)
    // and `m_MaterialIndex` are real miswires (pink shader) and must stay
    // Warning. The prefix match previously demoted all three.
    [TestFixture]
    public class EmptyRefClassifierTests
    {
        [Test]
        public void Classify_PluralMaterials_OnBuiltIn_IsWarning()
        {
            var empty = new EmptyLocalFileIDRegistry(1, 100, "m_Materials");
            Assert.AreEqual(VerifySeverity.Warning, EmptyRefClassifier.Classify(empty));
        }

        [Test]
        public void Classify_MaterialIndex_OnBuiltIn_IsWarning()
        {
            var empty = new EmptyLocalFileIDRegistry(1, 100, "m_MaterialIndex");
            Assert.AreEqual(VerifySeverity.Warning, EmptyRefClassifier.Classify(empty));
        }

        [Test]
        public void Classify_SingularMaterial_OnBuiltIn_IsInfo()
        {
            var empty = new EmptyLocalFileIDRegistry(1, 100, "m_Material");
            Assert.AreEqual(VerifySeverity.Info, EmptyRefClassifier.Classify(empty));
        }

        [Test]
        public void Classify_SelectOnPrefix_OnBuiltIn_IsInfo()
        {
            var empty = new EmptyLocalFileIDRegistry(1, 100, "m_SelectOnUp");
            Assert.AreEqual(VerifySeverity.Info, EmptyRefClassifier.Classify(empty));
        }

        [Test]
        public void Classify_UnexpectedEmpty_OnBuiltIn_IsWarning()
        {
            var empty = new EmptyLocalFileIDRegistry(1, 100, "m_Father");
            Assert.AreEqual(VerifySeverity.Warning, EmptyRefClassifier.Classify(empty));
        }
    }
}
