using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpVerify.Internals.RegexPatterns;
using UnityOpenMcpVerify.Rules;
using UnityOpenMcpVerify.Rules.MissingReferences;

namespace UnityOpenMcpVerify.Tests
{
    // empty_local_ref drops a `{fileID: 0}` only when the owning component's
    // configuration does not read that reference: a disabled ParticleSystem
    // module, a shape type that takes a different input, a non-Custom
    // simulation space, a render mode that draws no mesh, a renderer that does
    // not use a light-probe proxy. The same slot stays reported once the
    // configuration uses it.
    [TestFixture]
    public class UnusedEmptyRefsTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/UnusedEmptyRefs";

        // A ParticleSystem (198) + its ParticleSystemRenderer (199) + a
        // MeshRenderer (23) + a MonoBehaviour (114), cut down to the fields
        // the filter reads, in Unity's two-space layout.
        private static string[] Yaml(
            string moveWithTransform = "0",
            string shapeEnabled = "1", string shapeType = "4",
            string uvEnabled = "0", string uvMode = "0",
            string subEnabled = "0", string lightsEnabled = "0",
            string collisionEnabled = "0", string collisionType = "0",
            string triggerEnabled = "0",
            string forcesEnabled = "0", string forcesFilter = "0",
            string trailEnabled = "0",
            string renderMode = "0", string psProbeUsage = "0",
            string meshProbeUsage = "1")
        {
            return new[]
            {
                "%YAML 1.1",
                "%TAG !u! tag:unity3d.com,2011:",
                "--- !u!1 &100",
                "GameObject:",
                "  m_Component:",
                "  - component: {fileID: 400}",
                "  - component: {fileID: 198000}",
                "  - component: {fileID: 199000}",
                "  - component: {fileID: 23000}",
                "  - component: {fileID: 114000}",
                "  m_Name: Fx",
                "--- !u!4 &400",
                "Transform:",
                "  m_GameObject: {fileID: 100}",
                "  m_Children: []",
                "  m_Father: {fileID: 0}",
                "--- !u!198 &198000",
                "ParticleSystem:",
                "  m_GameObject: {fileID: 100}",
                "  serializedVersion: 8",
                "  moveWithTransform: " + moveWithTransform,
                "  moveWithCustomTransform: {fileID: 0}",
                "  InitialModule:",
                "    serializedVersion: 3",
                "    enabled: 1",
                "  ShapeModule:",
                "    serializedVersion: 6",
                "    enabled: " + shapeEnabled,
                "    type: " + shapeType,
                "    radius:",
                "      value: 1",
                "      mode: 0",
                "    m_Mesh: {fileID: 0}",
                "    m_MeshRenderer: {fileID: 0}",
                "    m_SkinnedMeshRenderer: {fileID: 0}",
                "    m_Sprite: {fileID: 0}",
                "    m_SpriteRenderer: {fileID: 0}",
                "    m_UseMeshMaterialIndex: 0",
                "    m_Texture: {fileID: 0}",
                "  UVModule:",
                "    serializedVersion: 2",
                "    enabled: " + uvEnabled,
                "    mode: " + uvMode,
                "    sprites:",
                "    - sprite: {fileID: 0}",
                "  ExternalForcesModule:",
                "    serializedVersion: 2",
                "    enabled: " + forcesEnabled,
                "    influenceFilter: " + forcesFilter,
                "    influenceList:",
                "    - {fileID: 0}",
                "  CollisionModule:",
                "    enabled: " + collisionEnabled,
                "    serializedVersion: 4",
                "    type: " + collisionType,
                "    m_Planes:",
                "    - {fileID: 0}",
                "  TriggerModule:",
                "    enabled: " + triggerEnabled,
                "    primitives:",
                "    - {fileID: 0}",
                "  SubModule:",
                "    serializedVersion: 2",
                "    enabled: " + subEnabled,
                "    subEmitters:",
                "    - serializedVersion: 3",
                "      emitter: {fileID: 0}",
                "      type: 0",
                "  LightsModule:",
                "    enabled: " + lightsEnabled,
                "    ratio: 0",
                "    light: {fileID: 0}",
                "  TrailModule:",
                "    enabled: " + trailEnabled,
                "--- !u!199 &199000",
                "ParticleSystemRenderer:",
                "  serializedVersion: 7",
                "  m_GameObject: {fileID: 100}",
                "  m_LightProbeUsage: " + psProbeUsage,
                "  m_Materials:",
                "  - {fileID: 0}",
                "  - {fileID: 0}",
                "  m_LightProbeVolumeOverride: {fileID: 0}",
                "  m_RenderMode: " + renderMode,
                "  m_Mesh: {fileID: 0}",
                "  m_Mesh1: {fileID: 0}",
                "  m_Mesh2: {fileID: 0}",
                "  m_Mesh3: {fileID: 0}",
                "--- !u!23 &23000",
                "MeshRenderer:",
                "  m_GameObject: {fileID: 100}",
                "  m_LightProbeUsage: " + meshProbeUsage,
                "  m_LightProbeVolumeOverride: {fileID: 0}",
                "--- !u!114 &114000",
                "MonoBehaviour:",
                "  m_GameObject: {fileID: 100}",
                "  m_Script: {fileID: 11500000, guid: 0123456789abcdef0123456789abcdef, type: 3}",
                "  m_LightProbeVolumeOverride: {fileID: 0}",
            };
        }

        // The sites the scanner records — `{fileID: 0}` lines with the owning
        // anchor and the key before the reference — minus the filtered ones,
        // as "anchor:property" strings (a list slot keeps its "" property, so
        // its anchor plus the line number tells it apart).
        private static List<string> Remaining(string[] lines)
        {
            var empties = new List<EmptyLocalFileIDRegistry>();
            long anchor = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var header = SharedRegex.ObjectHeaderAnchor.Match(line);
                if (header.Success) { anchor = long.Parse(header.Groups[1].Value); continue; }
                if (line.IndexOf("{fileID: 0}", System.StringComparison.Ordinal) < 0) continue;
                var prop = SharedRegex.PropertyKeyBeforeFileId.Match(line);
                empties.Add(new EmptyLocalFileIDRegistry(i, anchor, prop.Success ? prop.Groups[1].Value : ""));
            }
            var map = EmptyRefMetadata.Build(lines, new HashSet<long>());
            UnusedEmptyRefs.Remove(lines, empties, map);
            return empties.Select(e => e.Anchor + ":" + e.Property).ToList();
        }

        private static int Count(List<string> remaining, string site) => remaining.Count(s => s == site);

        [Test]
        public void DefaultParticleSystem_OnlyTheEmptyParticleMaterialRemains()
        {
            var remaining = Remaining(Yaml());
            // m_Father on the root transform is not a component field the
            // filter judges, the empty particle material (m_Materials[0]) is a
            // real miswire, the MeshRenderer probe proxy is unused (BlendProbes)
            // and the MonoBehaviour field is a script field.
            CollectionAssert.AreEquivalent(new[]
            {
                "400:m_Father",
                "199000:",
                "114000:m_LightProbeVolumeOverride",
            }, remaining);
        }

        [TestCase("6", "m_Mesh")]
        [TestCase("13", "m_MeshRenderer")]
        [TestCase("14", "m_SkinnedMeshRenderer")]
        [TestCase("19", "m_Sprite")]
        [TestCase("20", "m_SpriteRenderer")]
        public void ShapeInput_StaysReported_WhenTheShapeTypeUsesIt(string shapeType, string field)
        {
            var remaining = Remaining(Yaml(shapeType: shapeType));
            Assert.AreEqual(1, Count(remaining, "198000:" + field), string.Join(", ", remaining));
            foreach (var other in new[] { "m_Mesh", "m_MeshRenderer", "m_SkinnedMeshRenderer", "m_Sprite", "m_SpriteRenderer" })
                if (other != field) Assert.AreEqual(0, Count(remaining, "198000:" + other), other);
            Assert.AreEqual(0, Count(remaining, "198000:m_Texture"), "an empty shape texture is the feature switched off");
        }

        [Test]
        public void ShapeInput_IsDropped_WhenTheShapeModuleIsDisabled()
        {
            var remaining = Remaining(Yaml(shapeEnabled: "0", shapeType: "6"));
            Assert.AreEqual(0, Count(remaining, "198000:m_Mesh"));
        }

        [TestCase("0", 0)]
        [TestCase("1", 0)]
        [TestCase("2", 1)]
        public void CustomSimulationTransform_StaysReported_OnlyInCustomSpace(string space, int expected)
        {
            Assert.AreEqual(expected, Count(Remaining(Yaml(moveWithTransform: space)), "198000:moveWithCustomTransform"));
        }

        [TestCase("0", "0", 0)]
        [TestCase("1", "0", 0)]
        [TestCase("1", "1", 1)]
        public void SheetSprite_StaysReported_OnlyInEnabledSpritesMode(string enabled, string mode, int expected)
        {
            Assert.AreEqual(expected, Count(Remaining(Yaml(uvEnabled: enabled, uvMode: mode)), "198000:sprite"));
        }

        [TestCase("0", 0)]
        [TestCase("1", 1)]
        public void SubEmitter_StaysReported_WhenTheModuleIsEnabled(string enabled, int expected)
        {
            Assert.AreEqual(expected, Count(Remaining(Yaml(subEnabled: enabled)), "198000:emitter"));
        }

        [TestCase("0", 0)]
        [TestCase("1", 1)]
        public void Light_StaysReported_WhenTheModuleIsEnabled(string enabled, int expected)
        {
            Assert.AreEqual(expected, Count(Remaining(Yaml(lightsEnabled: enabled)), "198000:light"));
        }

        [TestCase("0", "0", 0)]
        [TestCase("1", "1", 0)]
        [TestCase("1", "0", 1)]
        public void CollisionPlane_StaysReported_OnlyInEnabledPlanesMode(string enabled, string type, int expected)
        {
            var lines = Yaml(collisionEnabled: enabled, collisionType: type);
            Assert.AreEqual(expected, CountListSlot(lines, "m_Planes:"));
        }

        [TestCase("0", 0)]
        [TestCase("1", 1)]
        public void TriggerCollider_StaysReported_WhenTheModuleIsEnabled(string enabled, int expected)
        {
            Assert.AreEqual(expected, CountListSlot(Yaml(triggerEnabled: enabled), "primitives:"));
        }

        [TestCase("0", "1", 0)]
        [TestCase("1", "0", 0)]
        [TestCase("1", "1", 1)]
        [TestCase("1", "2", 1)]
        public void ForceFieldListEntry_StaysReported_WhenTheListFilterIsUsed(string enabled, string filter, int expected)
        {
            Assert.AreEqual(expected, CountListSlot(Yaml(forcesEnabled: enabled, forcesFilter: filter), "influenceList:"));
        }

        [TestCase("0", 0)]
        [TestCase("4", 1)]
        public void RendererMesh_StaysReported_OnlyInMeshRenderMode(string renderMode, int expected)
        {
            var remaining = Remaining(Yaml(renderMode: renderMode));
            Assert.AreEqual(expected, Count(remaining, "199000:m_Mesh"));
            Assert.AreEqual(0, Count(remaining, "199000:m_Mesh1") + Count(remaining, "199000:m_Mesh2")
                + Count(remaining, "199000:m_Mesh3"), "an empty extra mesh slot is no extra mesh");
        }

        [TestCase("0", 1)]
        [TestCase("1", 2)]
        public void TrailMaterialSlot_StaysReported_WhenTrailsAreEnabled(string trailEnabled, int expected)
        {
            // m_Materials[0] (the particle material) is always reported.
            Assert.AreEqual(expected, Count(Remaining(Yaml(trailEnabled: trailEnabled)), "199000:"));
        }

        [TestCase("1", "1", 0)]
        [TestCase("2", "1", 1)]
        [TestCase("1", "2", 1)]
        public void LightProbeProxy_StaysReported_OnlyInProxyVolumeMode(string psUsage, string meshUsage, int expected)
        {
            var remaining = Remaining(Yaml(psProbeUsage: psUsage, meshProbeUsage: meshUsage));
            Assert.AreEqual(expected, Count(remaining, "199000:m_LightProbeVolumeOverride")
                + Count(remaining, "23000:m_LightProbeVolumeOverride"));
            Assert.AreEqual(1, Count(remaining, "114000:m_LightProbeVolumeOverride"), "a script field is never judged");
        }

        // A module list slot has no property of its own; count the remaining
        // `- {fileID: 0}` sites right below `listKey`.
        private static int CountListSlot(string[] lines, string listKey)
        {
            var empties = new List<EmptyLocalFileIDRegistry>();
            long anchor = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var header = SharedRegex.ObjectHeaderAnchor.Match(lines[i]);
                if (header.Success) { anchor = long.Parse(header.Groups[1].Value); continue; }
                if (lines[i].IndexOf("{fileID: 0}", System.StringComparison.Ordinal) < 0) continue;
                var prop = SharedRegex.PropertyKeyBeforeFileId.Match(lines[i]);
                empties.Add(new EmptyLocalFileIDRegistry(i, anchor, prop.Success ? prop.Groups[1].Value : ""));
            }
            UnusedEmptyRefs.Remove(lines, empties, EmptyRefMetadata.Build(lines, new HashSet<long>()));
            return empties.Count(e => e.Line > 0 && lines[e.Line - 1].Trim() == listKey);
        }

        // End to end through the rule on a prefab Unity wrote: a default
        // ParticleSystem reports none of its unused module slots; switching
        // the shape to Mesh with no mesh brings exactly that one back.
        [Test]
        public void Rule_DefaultParticleSystemPrefab_ReportsNoUnusedSlots_AndAnEmptyMeshShape()
        {
            EnsureDirectory(FixtureRoot);
            var path = FixtureRoot + "/Particles.prefab";
            var go = new GameObject("Particles");
            try
            {
                var ps = go.AddComponent<ParticleSystem>();
                PrefabUtility.SaveAsPrefabAsset(go, path);
                var defaults = ScanEmptyRefCodes(path);
                foreach (var unused in new[] { ":moveWithCustomTransform", ":m_Mesh", ":m_MeshRenderer",
                             ":m_SkinnedMeshRenderer", ":m_Sprite", ":m_SpriteRenderer", ":m_Texture", ":sprite",
                             ":emitter", ":light", ":m_LightProbeVolumeOverride", ":m_Mesh1" })
                    Assert.IsFalse(defaults.Any(c => c.EndsWith(unused)), $"{unused} reported: {string.Join(", ", defaults)}");

                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Mesh;
                PrefabUtility.SaveAsPrefabAsset(go, path);
                var meshShape = ScanEmptyRefCodes(path);
                Assert.AreEqual(1, meshShape.Count(c => c == "empty_local_ref:Particles:m_Mesh"),
                    string.Join(", ", meshShape));
            }
            finally
            {
                Object.DestroyImmediate(go);
                AssetDatabase.DeleteAsset(FixtureRoot);
            }
        }

        private static List<string> ScanEmptyRefCodes(string path)
        {
            var sink = new List<VerifyIssue>();
            new MissingReferencesRule().Scan(new VerifyScope(new[] { path }), VerifyRunMode.Validate, sink);
            return sink.Where(i => i.Severity == VerifySeverity.Warning
                                   && i.IssueCode.StartsWith("empty_local_ref:"))
                .Select(i => i.IssueCode).ToList();
        }

        private static void EnsureDirectory(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent)) EnsureDirectory(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }
    }
}
