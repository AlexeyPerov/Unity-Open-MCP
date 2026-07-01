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

        private static void MapSceneIssues(SceneData scene, ScanSettings settings, List<VerifyIssue> sink)
        {
            if (settings.DetectBrokenReferences)
            {
                foreach (var br in scene.BrokenReferences)
                {
                    sink.Add(MakeIssue(scene.Path, CodeBrokenReference,
                        "Scene '" + scene.Name + "': " + br,
                        VerifySeverity.Error,
                        Evidence(("detail", br))));
                }
            }

            if (settings.DetectHighRiskBootstrap && scene.IsBootstrapScene && scene.TotalObjectCount > settings.MaxSceneObjectCount / 2)
            {
                sink.Add(MakeIssue(scene.Path, CodeHighRiskBootstrap,
                    "Bootstrap scene '" + scene.Name + "' has " + scene.TotalObjectCount + " objects (budget: " + (settings.MaxSceneObjectCount / 2) + ").",
                    VerifySeverity.Warning,
                    Evidence(("scene", scene.Name),
                        ("objectCount", scene.TotalObjectCount.ToString()),
                        ("budget", (settings.MaxSceneObjectCount / 2).ToString()))));
            }

            if (settings.DetectHierarchyHotspots && scene.TotalObjectCount > settings.MaxSceneObjectCount)
            {
                sink.Add(MakeIssue(scene.Path, CodeSceneObjectCount,
                    "Scene '" + scene.Name + "' has " + scene.TotalObjectCount + " objects (budget: " + settings.MaxSceneObjectCount + ").",
                    VerifySeverity.Warning,
                    Evidence(("scene", scene.Name),
                        ("objectCount", scene.TotalObjectCount.ToString()),
                        ("budget", settings.MaxSceneObjectCount.ToString()))));
            }

            if (settings.DetectHierarchyHotspots)
            {
                foreach (var hotspot in scene.HotspotPaths)
                {
                    sink.Add(MakeIssue(scene.Path, CodeComponentHotspot,
                        "Scene '" + scene.Name + "' hotspot: " + hotspot,
                        VerifySeverity.Warning,
                        Evidence(("hotspot", hotspot))));
                }
            }

            if (settings.DetectInactiveAntiPatterns && scene.InactiveRendererCount > 0)
            {
                sink.Add(MakeIssue(scene.Path, CodeInactiveExpensive,
                    "Scene '" + scene.Name + "' has " + scene.InactiveRendererCount + " inactive objects with renderers.",
                    VerifySeverity.Warning,
                    Evidence(("scene", scene.Name),
                        ("inactiveRendererCount", scene.InactiveRendererCount.ToString()))));
            }

            if (settings.DetectInactiveAntiPatterns && scene.InactiveObjectCount > settings.MaxInactiveObjectThreshold)
            {
                sink.Add(MakeIssue(scene.Path, CodeInactiveHeavy,
                    "Scene '" + scene.Name + "' has " + scene.InactiveObjectCount + " inactive objects (threshold: " + settings.MaxInactiveObjectThreshold + ").",
                    VerifySeverity.Warning,
                    Evidence(("scene", scene.Name),
                        ("inactiveObjectCount", scene.InactiveObjectCount.ToString()),
                        ("threshold", settings.MaxInactiveObjectThreshold.ToString()))));
            }
        }

        private static void MapPrefabIssues(PrefabData prefab, ScanSettings settings, List<VerifyIssue> sink)
        {
            if (settings.DetectDeepNesting && prefab.NestingDepth > settings.MaxPrefabNestingDepth)
            {
                sink.Add(MakeIssue(prefab.Path, CodeDeepNesting,
                    "Prefab '" + prefab.Name + "' has nesting depth " + prefab.NestingDepth + " (max: " + settings.MaxPrefabNestingDepth + ").",
                    VerifySeverity.Warning,
                    Evidence(("prefab", prefab.Name),
                        ("nestingDepth", prefab.NestingDepth.ToString()),
                        ("max", settings.MaxPrefabNestingDepth.ToString()))));
            }

            if (settings.DetectOverrideExplosion && prefab.OverrideCount > settings.MaxPrefabOverrideCount)
            {
                sink.Add(MakeIssue(prefab.Path, CodeOverrideExplosion,
                    "Prefab '" + prefab.Name + "' has " + prefab.OverrideCount + " overrides (max: " + settings.MaxPrefabOverrideCount + ").",
                    VerifySeverity.Warning,
                    Evidence(("prefab", prefab.Name),
                        ("overrideCount", prefab.OverrideCount.ToString()),
                        ("max", settings.MaxPrefabOverrideCount.ToString()))));
            }
        }

        private static VerifyIssue MakeIssue(
            string assetPath, string code, string description,
            VerifySeverity severity,
            IReadOnlyDictionary<string, string> evidence = null)
        {
            return new VerifyIssue("scene_prefab_health", severity, assetPath, code, description, evidence);
        }

        private static IReadOnlyDictionary<string, string> Evidence(params (string, string)[] pairs)
        {
            var dict = new Dictionary<string, string>();
            foreach (var (k, v) in pairs)
            {
                if (!string.IsNullOrEmpty(k) && v != null)
                    dict[k] = v;
            }
            return dict;
        }
    }
}
