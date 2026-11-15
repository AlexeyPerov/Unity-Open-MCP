using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UnityOpenMcpVerify.Rules.MissingReferences
{
    // Empty object references that the owning component's own settings never
    // read. Unity serializes every slot of a built-in component whether or not
    // the current configuration uses it, so a default ParticleSystem carries
    // ~20 `{fileID: 0}` fields (shape mesh / renderer / sprite slots, the
    // custom simulation-space transform, sub-emitter, light, collision planes,
    // trigger colliders, the trail material, ...). Reporting those as
    // empty_local_ref buried a VFX prefab under hundreds of warnings that no
    // edit could ever resolve.
    //
    // A site is dropped only when the component's configuration provably does
    // not use the reference:
    //   - ParticleSystem (198): the module holding it is disabled; or the
    //     shape type takes a different input (m_Mesh only for Mesh, ...); or
    //     the simulation space is not Custom (moveWithCustomTransform); or the
    //     texture-sheet animation is in Grid mode (sprites); or collision is
    //     World mode (planes); or the force-field filter is layer-mask only
    //     (influence list). The shape texture is optional input: empty means
    //     the feature is off.
    //   - ParticleSystemRenderer (199): the render mode is not Mesh (m_Mesh);
    //     an extra mesh slot (m_Mesh1..3) is empty; the trail material slot
    //     (m_Materials[1]) when the system's Trails module is disabled.
    //   - Any renderer: m_LightProbeVolumeOverride when m_LightProbeUsage is
    //     not "Use Proxy Volume".
    // Anything else stays reported: an empty mesh on an enabled Mesh-type
    // shape, an empty sub-emitter in an enabled Sub Emitters module, an empty
    // main material, and every user-script field.
    //
    // Reads the asset YAML only, so the Editor scan, the headless batch scan
    // and every gate checkpoint agree on the same file.
    internal static class UnusedEmptyRefs
    {
        internal const int ClassIdParticleSystem = 198;
        internal const int ClassIdParticleSystemRenderer = 199;
        private const int ClassIdMonoBehaviour = 114;

        // ParticleSystemShapeType values whose input lives in the named field.
        private static readonly Dictionary<string, string> ShapeTypeForField = new Dictionary<string, string>
        {
            { "m_Mesh", "6" },                // Mesh
            { "m_MeshRenderer", "13" },       // MeshRenderer
            { "m_SkinnedMeshRenderer", "14" },// SkinnedMeshRenderer
            { "m_Sprite", "19" },             // Sprite
            { "m_SpriteRenderer", "20" },     // SpriteRenderer
        };

        private const string RenderModeMesh = "4";              // ParticleSystemRenderMode.Mesh
        private const string SimulationSpaceCustom = "2";       // ParticleSystemSimulationSpace.Custom
        private const string UvModeSprites = "1";               // ParticleSystemAnimationMode.Sprites
        private const string CollisionTypePlanes = "0";         // ParticleSystemCollisionType.Planes
        private const string ForceFilterLayerMask = "0";        // ParticleSystemGameObjectFilter.LayerMask
        private const string LightProbeUsageProxyVolume = "2";  // LightProbeUsage.UseProxyVolume

        private static readonly Regex KeyLine = new Regex(@"^([A-Za-z0-9_]+):(.*)$", RegexOptions.Compiled);

        /// <summary>Remove every site in <paramref name="empties"/> whose
        /// component does not use the reference.</summary>
        public static void Remove(string[] lines, List<EmptyLocalFileIDRegistry> empties,
            Dictionary<long, AnchorMetadata> map)
        {
            if (lines == null || empties == null || empties.Count == 0 || map == null || map.Count == 0) return;
            var cache = new Dictionary<long, ComponentYaml>();
            empties.RemoveAll(e => IsUnused(lines, e, map, cache));
        }

        internal static bool IsUnused(string[] lines, EmptyLocalFileIDRegistry empty,
            Dictionary<long, AnchorMetadata> map, Dictionary<long, ComponentYaml> cache)
        {
            if (empty == null || empty.Anchor == 0 || !map.TryGetValue(empty.Anchor, out var meta)) return false;
            var prop = empty.Property ?? "";
            var candidate = meta.ClassId == ClassIdParticleSystem
                || meta.ClassId == ClassIdParticleSystemRenderer
                || (prop == "m_LightProbeVolumeOverride" && meta.ClassId != ClassIdMonoBehaviour);
            if (!candidate) return false;

            var component = Parse(lines, meta, cache);
            if (!component.Sites.TryGetValue(empty.Line, out var site)) return false;

            switch (meta.ClassId)
            {
                case ClassIdParticleSystem:
                    return ParticleSystemUnused(component, site, prop);
                case ClassIdParticleSystemRenderer:
                    if (ParticleRendererUnused(lines, component, site, prop, meta, map, cache)) return true;
                    break;
            }

            // Renderer light-probe proxy: read only in "Use Proxy Volume" mode.
            return site.Block == null && prop == "m_LightProbeVolumeOverride"
                && Differs(component.Field("m_LightProbeUsage"), LightProbeUsageProxyVolume);
        }

        private static bool ParticleSystemUnused(ComponentYaml ps, Site site, string prop)
        {
            if (site.Block == null)
            {
                // Main module: the custom simulation-space transform is read
                // only when simulationSpace (serialized as moveWithTransform)
                // is Custom.
                return prop == "moveWithCustomTransform"
                    && Differs(ps.Field("moveWithTransform"), SimulationSpaceCustom);
            }

            if (!site.Block.EndsWith("Module", StringComparison.Ordinal)) return false;

            // A disabled module reads none of its references.
            if (ps.ModuleDisabled(site.Block)) return true;

            switch (site.Block)
            {
                case "ShapeModule":
                    if (prop == "m_Texture") return true;
                    return ShapeTypeForField.TryGetValue(prop, out var shapeType)
                        && Differs(ps.BlockField("ShapeModule", "type"), shapeType);
                case "UVModule":
                    return prop == "sprite" && Differs(ps.BlockField("UVModule", "mode"), UvModeSprites);
                case "CollisionModule":
                    // Planes (m_Planes list entries, or legacy plane0..plane5).
                    return Differs(ps.BlockField("CollisionModule", "type"), CollisionTypePlanes);
                case "ExternalForcesModule":
                    // influenceList entries.
                    return ps.BlockField("ExternalForcesModule", "influenceFilter") == ForceFilterLayerMask;
                default:
                    return false;
            }
        }

        private static bool ParticleRendererUnused(string[] lines, ComponentYaml renderer, Site site, string prop,
            AnchorMetadata meta, Dictionary<long, AnchorMetadata> map, Dictionary<long, ComponentYaml> cache)
        {
            if (site.Block == null)
            {
                if (prop == "m_Mesh") return Differs(renderer.Field("m_RenderMode"), RenderModeMesh);
                // Extra mesh slots: an empty slot is simply no additional mesh.
                return prop == "m_Mesh1" || prop == "m_Mesh2" || prop == "m_Mesh3";
            }

            // m_Materials[1] is the trail material, read only when the system
            // on the same GameObject has its Trails module enabled. An empty
            // m_Materials[0] (the particle material) stays reported.
            if (site.Block == "m_Materials" && site.ListIndex >= 1 && meta.GameObjectId != 0)
            {
                foreach (var other in map.Values)
                {
                    if (other.ClassId != ClassIdParticleSystem || other.GameObjectId != meta.GameObjectId) continue;
                    return Parse(lines, other, cache).ModuleDisabled("TrailModule");
                }
            }
            return false;
        }

        // True when the setting is known and is not the value that uses the
        // reference. An unknown (absent) setting never suppresses.
        private static bool Differs(string value, string usingValue) => value != null && value != usingValue;

        private static ComponentYaml Parse(string[] lines, AnchorMetadata meta, Dictionary<long, ComponentYaml> cache)
        {
            if (cache.TryGetValue(meta.Anchor, out var parsed)) return parsed;
            parsed = ComponentYaml.Read(lines, meta.HeaderLine);
            cache[meta.Anchor] = parsed;
            return parsed;
        }

        internal struct Site
        {
            // The top-level field (2-space indent) the line sits under, or
            // null when the line IS a top-level field.
            public string Block;
            // 0-based position among the block's top-level list items
            // (`  - {fileID: 0}` under `  m_Materials:`); -1 otherwise.
            public int ListIndex;
        }

        // One YAML object's top-level scalars, its blocks' first-level scalars
        // (a ParticleSystem module's `enabled` / `type` / `mode`), and where
        // each `{fileID: 0}` line sits. Unity writes two-space indentation.
        internal sealed class ComponentYaml
        {
            private readonly Dictionary<string, string> _fields = new Dictionary<string, string>();
            private readonly Dictionary<string, Dictionary<string, string>> _blocks =
                new Dictionary<string, Dictionary<string, string>>();
            public readonly Dictionary<int, Site> Sites = new Dictionary<int, Site>();

            public string Field(string key) => _fields.TryGetValue(key, out var v) ? v : null;

            public string BlockField(string block, string key) =>
                _blocks.TryGetValue(block, out var f) && f.TryGetValue(key, out var v) ? v : null;

            public bool ModuleDisabled(string module) => BlockField(module, "enabled") == "0";

            public static ComponentYaml Read(string[] lines, int headerLine)
            {
                var c = new ComponentYaml();
                string block = null;
                var listIndex = -1;
                for (var i = headerLine + 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.StartsWith("---", StringComparison.Ordinal)) break;
                    var indent = Indent(line);
                    var isEmptyRef = line.IndexOf("{fileID: 0}", StringComparison.Ordinal) >= 0;

                    if (indent == 2)
                    {
                        if (line.Length > 2 && line[2] == '-')
                        {
                            listIndex++;
                            if (isEmptyRef) c.Sites[i] = new Site { Block = block, ListIndex = listIndex };
                            continue;
                        }
                        var m = KeyLine.Match(line.Substring(2));
                        if (!m.Success) { block = null; continue; }
                        var key = m.Groups[1].Value;
                        var value = m.Groups[2].Value.Trim();
                        if (value.Length == 0)
                        {
                            block = key;
                            listIndex = -1;
                            if (!c._blocks.ContainsKey(key)) c._blocks[key] = new Dictionary<string, string>();
                        }
                        else
                        {
                            block = null;
                            if (!c._fields.ContainsKey(key)) c._fields[key] = value;
                            if (isEmptyRef) c.Sites[i] = new Site { Block = null, ListIndex = -1 };
                        }
                        continue;
                    }

                    if (indent > 2 && block != null)
                    {
                        if (indent == 4 && line.Length > 4 && line[4] != '-')
                        {
                            var m = KeyLine.Match(line.Substring(4));
                            if (m.Success)
                            {
                                var value = m.Groups[2].Value.Trim();
                                var fields = c._blocks[block];
                                if (value.Length > 0 && !fields.ContainsKey(m.Groups[1].Value))
                                    fields[m.Groups[1].Value] = value;
                            }
                        }
                        if (isEmptyRef) c.Sites[i] = new Site { Block = block, ListIndex = -1 };
                    }
                }
                return c;
            }

            private static int Indent(string line)
            {
                var n = 0;
                while (n < line.Length && line[n] == ' ') n++;
                return n;
            }
        }
    }
}
