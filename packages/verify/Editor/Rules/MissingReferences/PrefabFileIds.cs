namespace UnityOpenMcpVerify.Rules.MissingReferences
{
    // Conventional Unity fileIDs used inside .prefab / scene YAML. Centralized
    // so the missing_references scanner can recognize prefab-composition
    // constructs and avoid false-positive broken-reference reports
    // (feedback-fable-31-07 §6).
    internal static class PrefabFileIds
    {
        // Unity serializes a prefab's root GameObject with local fileID
        // 100100000. It is a synthetic id rarely returned by
        // AssetDatabase.LoadAllAssetsAtPath, so an external PPtr referencing it
        // (common for nested prefab instances pointing back at the prefab root)
        // otherwise reads as a broken reference. The editor resolves it fine;
        // treat it as always-present in the target asset.
        public const long RootGameObject = 100100000;
    }
}
