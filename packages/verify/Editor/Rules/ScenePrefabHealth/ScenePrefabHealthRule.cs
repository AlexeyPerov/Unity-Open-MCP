using System.Collections.Generic;

namespace UnityAgentVerify.Rules
{
    public class ScenePrefabHealthRule : IVerifyRule
    {
        public string Id => "scene_prefab_health";

        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            var settings = ScenePrefabHealth.ScanSettings.Default();
            var scenes = new List<ScenePrefabHealth.SceneData>();
            var prefabs = new List<ScenePrefabHealth.PrefabData>();

            ScenePrefabHealth.Scanner.ScanPaths(scope.Paths, settings, scenes, prefabs);
            ScenePrefabHealth.IssueMapper.MapToIssues(scenes, prefabs, settings, sink);
        }
    }
}
