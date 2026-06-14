// Extracted from Unity-Scanner: Editor/Categories/ScenePrefabHealth/ScenePrefabHealthIssueMapper.cs

using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.ScenePrefabHealth
{
    public static class IssueMapper
    {
        public const string CodeBrokenReference = "broken_reference";
        public const string CodeHighRiskBootstrap = "high_risk_bootstrap";
        public const string CodeSceneObjectCount = "scene_object_count";
        public const string CodeComponentHotspot = "component_hotspot";
        public const string CodeInactiveExpensive = "inactive_expensive";
        public const string CodeInactiveHeavy = "inactive_heavy";
        public const string CodeDeepNesting = "deep_nesting";
        public const string CodeOverrideExplosion = "override_explosion";

        public static void MapToIssues(
            List<SceneData> scenes,
            List<PrefabData> prefabs,
            ScanSettings settings,
            List<VerifyIssue> sink)
        {
            foreach (var scene in scenes)
                MapSceneIssues(scene, settings, sink);

            foreach (var prefab in prefabs)
                MapPrefabIssues(prefab, settings, sink);
        }

        static void MapSceneIssues(SceneData scene, ScanSettings settings, List<VerifyIssue> sink)
        {
            if (settings.DetectBrokenReferences)
            {
                foreach (var br in scene.BrokenReferences)
                {
                    sink.Add(MakeIssue(scene.Path, CodeBrokenReference,
                        "Scene '" + scene.Name + "': " + br,
                        VerifySeverity.Error));
                }
            }

            if (settings.DetectHighRiskBootstrap && scene.IsBootstrapScene && scene.TotalObjectCount > settings.MaxSceneObjectCount / 2)
            {
                sink.Add(MakeIssue(scene.Path, CodeHighRiskBootstrap,
                    "Bootstrap scene '" + scene.Name + "' has " + scene.TotalObjectCount + " objects (budget: " + (settings.MaxSceneObjectCount / 2) + ").",
                    VerifySeverity.Warning));
            }

            if (settings.DetectHierarchyHotspots && scene.TotalObjectCount > settings.MaxSceneObjectCount)
            {
                sink.Add(MakeIssue(scene.Path, CodeSceneObjectCount,
                    "Scene '" + scene.Name + "' has " + scene.TotalObjectCount + " objects (budget: " + settings.MaxSceneObjectCount + ").",
                    VerifySeverity.Warning));
            }

            if (settings.DetectHierarchyHotspots)
            {
                foreach (var hotspot in scene.HotspotPaths)
                {
                    sink.Add(MakeIssue(scene.Path, CodeComponentHotspot,
                        "Scene '" + scene.Name + "' hotspot: " + hotspot,
                        VerifySeverity.Warning));
                }
            }

            if (settings.DetectInactiveAntiPatterns && scene.InactiveRendererCount > 0)
            {
                sink.Add(MakeIssue(scene.Path, CodeInactiveExpensive,
                    "Scene '" + scene.Name + "' has " + scene.InactiveRendererCount + " inactive objects with renderers.",
                    VerifySeverity.Warning));
            }

            if (settings.DetectInactiveAntiPatterns && scene.InactiveObjectCount > settings.MaxInactiveObjectThreshold)
            {
                sink.Add(MakeIssue(scene.Path, CodeInactiveHeavy,
                    "Scene '" + scene.Name + "' has " + scene.InactiveObjectCount + " inactive objects (threshold: " + settings.MaxInactiveObjectThreshold + ").",
                    VerifySeverity.Warning));
            }
        }

        static void MapPrefabIssues(PrefabData prefab, ScanSettings settings, List<VerifyIssue> sink)
        {
            if (settings.DetectDeepNesting && prefab.NestingDepth > settings.MaxPrefabNestingDepth)
            {
                sink.Add(MakeIssue(prefab.Path, CodeDeepNesting,
                    "Prefab '" + prefab.Name + "' has nesting depth " + prefab.NestingDepth + " (max: " + settings.MaxPrefabNestingDepth + ").",
                    VerifySeverity.Warning));
            }

            if (settings.DetectOverrideExplosion && prefab.OverrideCount > settings.MaxPrefabOverrideCount)
            {
                sink.Add(MakeIssue(prefab.Path, CodeOverrideExplosion,
                    "Prefab '" + prefab.Name + "' has " + prefab.OverrideCount + " overrides (max: " + settings.MaxPrefabOverrideCount + ").",
                    VerifySeverity.Warning));
            }
        }

        static VerifyIssue MakeIssue(
            string assetPath, string code, string description,
            VerifySeverity severity)
        {
            return new VerifyIssue("scene_prefab_health", severity, assetPath, code, description);
        }
    }
}
