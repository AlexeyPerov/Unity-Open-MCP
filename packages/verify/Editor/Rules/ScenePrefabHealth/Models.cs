// Extracted from Unity-Scanner: Editor/Categories/ScenePrefabHealth/ScenePrefabHealthResultModels.cs

using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.ScenePrefabHealth
{
    public class SceneData
    {
        public string Path;
        public string Name;
        public int RootCount;
        public int TotalObjectCount;
        public int TotalComponentCount;
        public int InactiveObjectCount;
        public int InactiveRendererCount;
        public bool IsBootstrapScene;
        public long FileSizeBytes;
        public List<string> BrokenReferences = new List<string>();
        public List<string> HotspotPaths = new List<string>();
        public List<InactiveObjectInfo> ExpensiveInactiveObjects = new List<InactiveObjectInfo>();
    }

    public class PrefabData
    {
        public string Path;
        public string Name;
        public int NestingDepth;
        public int OverrideCount;
        public int ComponentCount;
        public int ChildCount;
        public long FileSizeBytes;
    }

    public class InactiveObjectInfo
    {
        public string ObjectPath;
        public string ComponentType;
        public string Description;
    }

    public struct ScanSettings
    {
        public int MaxPrefabNestingDepth;
        public int MaxPrefabOverrideCount;
        public int MaxSceneObjectCount;
        public int MaxComponentCountPerObject;
        public int MaxInactiveObjectThreshold;
        public bool DetectDeepNesting;
        public bool DetectOverrideExplosion;
        public bool DetectHierarchyHotspots;
        public bool DetectBrokenReferences;
        public bool DetectInactiveAntiPatterns;
        public bool DetectHighRiskBootstrap;

        public static ScanSettings Default()
        {
            return new ScanSettings
            {
                MaxPrefabNestingDepth = 5,
                MaxPrefabOverrideCount = 50,
                MaxSceneObjectCount = 5000,
                MaxComponentCountPerObject = 20,
                MaxInactiveObjectThreshold = 100,
                DetectDeepNesting = true,
                DetectOverrideExplosion = true,
                DetectHierarchyHotspots = true,
                DetectBrokenReferences = true,
                DetectInactiveAntiPatterns = true,
                DetectHighRiskBootstrap = true,
            };
        }
    }
}
